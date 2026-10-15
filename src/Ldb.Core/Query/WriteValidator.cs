using System.Text.Json;
using Ldb.Core.Schema;

namespace Ldb.Core.Query;

/// <summary>
/// Checks a write request against the stored schema without touching the database. Used by the write-behind buffer so that a
/// request that could never succeed is refused at once (400) instead of being acknowledged and failing minutes later.
/// The database still has the last word at flush time (unique keys, constraints, value formats).
/// </summary>
public static class WriteValidator
{
    public static void Validate(string operation, string payload, SchemaDefinition schema, int maxInsertRows)
    {
        switch (operation)
        {
            case "insert":
            {
                var r = Json.Parse<InsertRequest>(payload);
                var t = Table(schema, r.Table);
                if (r.Rows is null || r.Rows.Count == 0) throw Bad("Provide at least one row.");
                if (r.Rows.Count > maxInsertRows) throw new ApiException(413, "too_many_rows", $"At most {maxInsertRows} rows can be inserted per request.");
                foreach (var row in r.Rows) CheckRow(t, row, requireAll: true);
                break;
            }
            case "upsert":
            {
                var r = Json.Parse<UpsertRequest>(payload);
                var t = Table(schema, r.Table);
                if (r.Keys is null || r.Keys.Count == 0) throw Bad("Provide the key columns of the upsert.");
                var keys = r.Keys.Distinct().ToList();
                foreach (var k in keys) Column(t, k);
                var isKey = (t.PrimaryKey ?? []).ToHashSet().SetEquals(keys) || (t.Indexes ?? []).Any(i => i.Unique && i.Columns.ToHashSet().SetEquals(keys));
                if (!isKey) throw Bad("The keys must match the primary key or a unique index exactly.");
                var given = CheckRow(t, r.Row, requireAll: true);
                foreach (var k in keys) if (!given.Contains(k)) throw new ApiException(400, "missing_value", $"The row needs a value for key column '{k}'.");
                break;
            }
            case "update":
            {
                var r = Json.Parse<UpdateRequest>(payload);
                var t = Table(schema, r.Table);
                var filter = FilterParser.Parse(r.Filter);
                if (filter is null && !r.All) throw new ApiException(400, "filter_required", "An update needs a filter, or all=true where the key allows it.");
                CheckFilter(t, filter);
                var changes = 0;
                if (r.Set is { ValueKind: JsonValueKind.Object } set)
                    foreach (var p in set.EnumerateObject()) { var c = Writable(t, p.Name); CheckValue(c, p.Value); changes++; }
                if (r.Inc is { ValueKind: JsonValueKind.Object } inc)
                    foreach (var p in inc.EnumerateObject())
                    {
                        var c = Writable(t, p.Name);
                        if (c.Type is not (ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64))
                            throw Bad($"Column '{c.Name}' is not numeric, so it cannot be incremented.");
                        if (p.Value.ValueKind != JsonValueKind.Number) throw new ApiException(400, "invalid_value", "Increments must be numbers.");
                        changes++;
                    }
                if (changes == 0) throw Bad("Provide 'set' or 'inc' with at least one column.");
                break;
            }
            case "delete":
            {
                var r = Json.Parse<DeleteRequest>(payload);
                var t = Table(schema, r.Table);
                var filter = FilterParser.Parse(r.Filter);
                if (filter is null && !r.All) throw new ApiException(400, "filter_required", "A delete needs a filter, or all=true where the key allows it.");
                CheckFilter(t, filter);
                break;
            }
            default: throw Bad("This operation cannot be buffered.");
        }
    }

    static TableDefinition Table(SchemaDefinition schema, string name) =>
        schema.Tables.FirstOrDefault(t => t.Name == Names.Table(name)) ?? throw new ApiException(404, "unknown_table", $"The namespace has no table '{name}'.");

    static ColumnDefinition Column(TableDefinition t, string name) =>
        t.Columns.FirstOrDefault(c => c.Name == name) ?? throw new ApiException(400, "unknown_column", $"Table '{t.Name}' has no column '{name}'.");

    static ColumnDefinition Writable(TableDefinition t, string name)
    {
        var c = Column(t, name);
        if (t.PrimaryKey?.Contains(c.Name) == true) throw Bad($"Primary key column '{c.Name}' cannot be changed.");
        return c;
    }

    static HashSet<string> CheckRow(TableDefinition t, JsonElement row, bool requireAll)
    {
        if (row.ValueKind != JsonValueKind.Object) throw Bad("Each row must be an object.");
        var given = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in row.EnumerateObject())
        {
            var c = Column(t, p.Name);
            if (!given.Add(c.Name)) throw Bad($"Column '{c.Name}' appears twice.");
            CheckValue(c, p.Value);
        }
        if (requireAll)
            foreach (var c in t.Columns.Where(x => !x.Nullable && x.Default is null && !x.AutoIncrement && !given.Contains(x.Name)))
                throw new ApiException(400, "missing_value", $"Column '{c.Name}' needs a value.");
        return given;
    }

    /// <summary>Only the JSON kind is checked here (a number for a number column, text for text, ...). Formats are the database's job.</summary>
    static void CheckValue(ColumnDefinition c, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (!c.Nullable) throw new ApiException(400, "invalid_value", $"Column '{c.Name}' cannot be null.");
            return;
        }
        var ok = c.Type switch
        {
            ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Float64 => v.ValueKind == JsonValueKind.Number,
            ColumnTypes.Decimal => v.ValueKind is JsonValueKind.Number or JsonValueKind.String,
            ColumnTypes.Bool => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ColumnTypes.Json => true,
            _ => v.ValueKind == JsonValueKind.String,
        };
        if (!ok) throw new ApiException(400, "invalid_value", $"The value for column '{c.Name}' does not fit its type '{c.Type}'.");
        if (c.Type == ColumnTypes.String && c.Length is { } max && v.GetString()!.Length > max)
            throw new ApiException(400, "value_too_long", $"Column '{c.Name}' accepts at most {max} characters.");
    }

    static void CheckFilter(TableDefinition t, FilterNode? node)
    {
        switch (node)
        {
            case null: return;
            case AndNode a: foreach (var c in a.Children) CheckFilter(t, c); return;
            case OrNode o: foreach (var c in o.Children) CheckFilter(t, c); return;
            case NotNode n: CheckFilter(t, n.Child); return;
            case CompareNode c: Column(t, c.Field); return;
        }
    }

    static ApiException Bad(string message) => new(400, "invalid_request", message);
}
