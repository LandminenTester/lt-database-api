using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ldb.Core.Schema;

namespace Ldb.Providers.Sql;

/// <summary>Everything that differs between SQL engines. The shared provider only talks to this class.</summary>
public abstract class SqlDialect
{
    public abstract string Name { get; }
    public abstract string Quote(string identifier);
    public abstract string TypeSql(ColumnDefinition c);
    /// <summary>Appended after the type, before NOT NULL.</summary>
    public abstract string IdentitySql(ColumnDefinition c);
    public virtual string TableOptions => "";
    public abstract string LikeEscape { get; }
    /// <summary>Wraps a parameter for columns that need an explicit cast (for example jsonb).</summary>
    public virtual string ParamExpr(ColumnDefinition c, string parameter) => parameter;
    /// <summary>Converts a coerced value to the type the driver expects for this engine.</summary>
    public virtual object ToDriver(ColumnDefinition c, object value) => value;
    public abstract string EmptyInsertSql(string table);
    public abstract string ReturningSql(string primaryKeyColumn);
    public abstract string UpsertSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> values, IReadOnlyList<string> keys, IReadOnlyList<string> updateColumns);

    public abstract string TableExistsSql { get; }
    public abstract string ColumnNamesSql { get; }
    public abstract string IndexNamesSql { get; }

    public abstract string DefaultSql(ColumnDefinition c);
    public abstract IEnumerable<string> AddColumnSql(string table, ColumnDefinition c);
    public abstract IEnumerable<string> AlterColumnSql(string table, ColumnDefinition old, ColumnDefinition nw);
    public abstract string RenameColumnSql(string table, string from, string to);
    public abstract string RenameTableSql(string from, string to);
    public abstract string DropIndexSql(string table, string index);
    /// <summary>Makes a column nullable again so a quarantined column cannot block future inserts.</summary>
    public abstract string RelaxNullabilitySql(string table, ColumnDefinition c);

    public string ColumnSql(ColumnDefinition c)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(c.Name)).Append(' ').Append(TypeSql(c));
        var id = c.AutoIncrement ? IdentitySql(c) : "";
        if (id.Length > 0) sb.Append(' ').Append(id);
        sb.Append(c.Nullable ? " NULL" : " NOT NULL");
        if (!c.AutoIncrement && c.Default is not null) sb.Append(" DEFAULT ").Append(DefaultSql(c));
        return sb.ToString();
    }

    public virtual string CreateTableSql(string table, TableDefinition t)
    {
        var parts = t.Columns.Select(ColumnSql).ToList();
        if (t.PrimaryKey is { Count: > 0 } pk) parts.Add($"PRIMARY KEY ({string.Join(", ", pk.Select(Quote))})");
        return $"CREATE TABLE {Quote(table)} ({string.Join(", ", parts)}){TableOptions}";
    }

    public virtual string CreateIndexSql(string table, string indexName, IndexDefinition ix) =>
        $"CREATE {(ix.Unique ? "UNIQUE " : "")}INDEX {Quote(indexName)} ON {Quote(table)} ({string.Join(", ", ix.Columns.Select(Quote))})";

    public virtual string DropTableSql(string table) => $"DROP TABLE IF EXISTS {Quote(table)}";

    public static string IndexName(string table, string index) => Ldb.Core.Names.IndexName(table, index);

    protected static string Literal(string value, bool escapeBackslash)
    {
        var v = value.Replace("'", "''");
        return "'" + (escapeBackslash ? v.Replace("\\", "\\\\") : v) + "'";
    }

    protected static string Number(System.Text.Json.JsonElement e) => e.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase)
        ? e.GetDouble().ToString("R", CultureInfo.InvariantCulture)
        : e.GetRawText();
}
