using System.Globalization;
using System.Text.Json;

namespace Ldb.Core.Schema;

/// <summary>
/// Validates and normalizes a desired schema. After normalization every column has explicit sizes, Unique flags are
/// folded into indexes and the primary key is a list, so two equal schemas always serialize and hash identically.
/// </summary>
public static class SchemaValidator
{
    /// <summary>MySQL limits index keys to 3072 bytes (4 bytes per character), so the portable limit is conservative.</summary>
    public const int MaxIndexedString = 255;

    public static SchemaDefinition Normalize(SchemaDefinition input, int maxTables = 64)
    {
        if (input?.Tables is null) throw Bad("The schema needs a tables list.");
        if (input.Tables.Count > maxTables) throw Bad($"A namespace may define at most {maxTables} tables.");
        var seenTables = new HashSet<string>();
        var tables = new List<TableDefinition>();
        foreach (var t in input.Tables)
        {
            Names.Table(t.Name);
            if (!seenTables.Add(t.Name)) throw Bad($"Table '{t.Name}' is defined twice.");
            if (t.RenamedFrom is not null) Names.Table(t.RenamedFrom);
            tables.Add(NormalizeTable(t));
        }
        return new SchemaDefinition(tables);
    }

    static TableDefinition NormalizeTable(TableDefinition t)
    {
        if (t.Columns is null || t.Columns.Count == 0) throw Bad($"Table '{t.Name}' needs at least one column.");
        if (t.Columns.Count > 256) throw Bad($"Table '{t.Name}' has more than 256 columns.");
        var columns = new List<ColumnDefinition>();
        var names = new HashSet<string>();
        var indexes = new List<IndexDefinition>(t.Indexes ?? []);
        foreach (var c in t.Columns)
        {
            Names.Column(c.Name);
            if (!names.Add(c.Name)) throw Bad($"Column '{t.Name}.{c.Name}' is defined twice.");
            if (c.RenamedFrom is not null) Names.Column(c.RenamedFrom);
            var n = NormalizeColumn(t.Name, c);
            if (c.Unique) indexes.Add(new IndexDefinition("uq_" + c.Name, [c.Name], true));
            columns.Add(n with { Unique = false });
        }

        var pk = t.PrimaryKey ?? [];
        if (pk.Distinct().Count() != pk.Count) throw Bad($"Table '{t.Name}' lists a primary key column twice.");
        foreach (var k in pk)
        {
            var col = columns.FirstOrDefault(x => x.Name == k) ?? throw Bad($"Primary key column '{t.Name}.{k}' does not exist.");
            if (col.Nullable) throw Bad($"Primary key column '{t.Name}.{k}' cannot be nullable.");
            if (col.Type is ColumnTypes.Json or ColumnTypes.Text) throw Bad($"Primary key column '{t.Name}.{k}' has a type that cannot be indexed.");
            if (col.Type == ColumnTypes.String && col.Length > MaxIndexedString) throw Bad($"Primary key column '{t.Name}.{k}': indexed strings may be at most {MaxIndexedString} characters.");
        }
        foreach (var c in columns.Where(x => x.AutoIncrement))
        {
            if (pk.Count != 1 || pk[0] != c.Name) throw Bad($"Auto-increment column '{t.Name}.{c.Name}' must be the only primary key column.");
        }

        if (indexes.Count > 32) throw Bad($"Table '{t.Name}' has more than 32 indexes.");
        var indexNames = new HashSet<string>();
        var normalizedIndexes = new List<IndexDefinition>();
        foreach (var ix in indexes)
        {
            Names.Column(ix.Name);
            if (!indexNames.Add(ix.Name)) throw Bad($"Index '{t.Name}.{ix.Name}' is defined twice.");
            if (ix.Columns is null || ix.Columns.Count is 0 or > 8) throw Bad($"Index '{t.Name}.{ix.Name}' needs 1 to 8 columns.");
            foreach (var cn in ix.Columns)
            {
                var col = columns.FirstOrDefault(x => x.Name == cn) ?? throw Bad($"Index '{t.Name}.{ix.Name}' uses unknown column '{cn}'.");
                if (col.Type is ColumnTypes.Json or ColumnTypes.Text || (col.Type == ColumnTypes.Bytes && col.Length is null))
                    throw Bad($"Index '{t.Name}.{ix.Name}' uses column '{cn}', whose type cannot be indexed.");
                if (col.Type == ColumnTypes.String && col.Length > MaxIndexedString)
                    throw Bad($"Index '{t.Name}.{ix.Name}': indexed strings may be at most {MaxIndexedString} characters ('{cn}' has {col.Length}).");
            }
            normalizedIndexes.Add(ix with { Columns = [.. ix.Columns] });
        }
        return t with { Columns = columns, PrimaryKey = [.. pk], Indexes = normalizedIndexes };
    }

    static ColumnDefinition NormalizeColumn(string table, ColumnDefinition c)
    {
        var where = $"{table}.{c.Name}";
        if (!ColumnTypes.All.Contains(c.Type)) throw Bad($"Column '{where}' has unknown type '{c.Type}'.");
        int? length = null, precision = null, scale = null;
        switch (c.Type)
        {
            case ColumnTypes.String:
                length = c.Length ?? 255;
                if (length is < 1 or > 4000) throw Bad($"Column '{where}': string length must be between 1 and 4000.");
                break;
            case ColumnTypes.Bytes:
                length = c.Length;
                if (length is < 1 or > 65535) throw Bad($"Column '{where}': bytes length must be between 1 and 65535.");
                break;
            case ColumnTypes.Decimal:
                precision = c.Precision ?? 18; scale = c.Scale ?? 0;
                if (precision is < 1 or > 38) throw Bad($"Column '{where}': precision must be between 1 and 38.");
                if (scale < 0 || scale > precision) throw Bad($"Column '{where}': scale must be between 0 and the precision.");
                break;
        }
        if (c.AutoIncrement && c.Type is not (ColumnTypes.Int32 or ColumnTypes.Int64)) throw Bad($"Column '{where}': only int32 and int64 can auto-increment.");
        if (c.AutoIncrement && c.Nullable) throw Bad($"Column '{where}': an auto-increment column cannot be nullable.");
        JsonElement? def = null;
        if (c.Default is { } d && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            ValidateDefault(where, c.Type, d);
            def = d.Clone();
        }
        return c with { Length = length, Precision = precision, Scale = scale, Default = def };
    }

    static void ValidateDefault(string where, string type, JsonElement d)
    {
        bool ok = type switch
        {
            ColumnTypes.Int32 or ColumnTypes.Int64 => d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out _),
            ColumnTypes.Decimal or ColumnTypes.Float64 => d.ValueKind == JsonValueKind.Number,
            ColumnTypes.Bool => d.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ColumnTypes.String or ColumnTypes.Text => d.ValueKind == JsonValueKind.String && IsSafeText(d.GetString()!),
            ColumnTypes.DateTime => d.ValueKind == JsonValueKind.String && (d.GetString() == "now" || DateTime.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _)),
            ColumnTypes.Date => d.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ColumnTypes.Uuid => d.ValueKind == JsonValueKind.String && Guid.TryParse(d.GetString(), out _),
            ColumnTypes.Json => IsSafeText(d.GetRawText()),
            _ => false,
        };
        if (!ok) throw Bad($"Column '{where}': the default value does not fit the column type.");
    }

    static bool IsSafeText(string value) => value.Length <= 255 && !value.Any(char.IsControl);

    static ApiException Bad(string message) => new(400, "invalid_schema", message);
}
