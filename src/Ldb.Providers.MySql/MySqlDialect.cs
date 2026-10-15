using Ldb.Core.Schema;
using Ldb.Providers.Sql;

namespace Ldb.Providers.MySql;

public sealed class MySqlDialect : SqlDialect
{
    public override string Name => "mysql";
    public override string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";
    public override string IdentitySql(ColumnDefinition c) => "AUTO_INCREMENT";
    // utf8mb4_bin keeps comparisons case-sensitive, like PostgreSQL and MongoDB.
    public override string TableOptions => " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin";
    public override string LikeEscape => "ESCAPE '\\\\'";
    public override object ToDriver(ColumnDefinition c, object value) => value is Guid g ? g.ToString() : value;
    public override string EmptyInsertSql(string table) => $"INSERT INTO {Quote(table)} () VALUES ()";
    public override string ReturningSql(string primaryKeyColumn) => "";

    public override string TypeSql(ColumnDefinition c) => c.Type switch
    {
        ColumnTypes.Int32 => "INT",
        ColumnTypes.Int64 => "BIGINT",
        ColumnTypes.Decimal => $"DECIMAL({c.Precision},{c.Scale})",
        ColumnTypes.Float64 => "DOUBLE",
        ColumnTypes.Bool => "TINYINT(1)",
        ColumnTypes.String => $"VARCHAR({c.Length})",
        ColumnTypes.Text => "LONGTEXT",
        ColumnTypes.Json => "JSON",
        ColumnTypes.DateTime => "DATETIME(6)",
        ColumnTypes.Date => "DATE",
        ColumnTypes.Uuid => "CHAR(36)",
        ColumnTypes.Bytes => c.Length is { } n ? $"VARBINARY({n})" : "LONGBLOB",
        _ => throw new NotSupportedException(c.Type),
    };

    public override string UpsertSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> values, IReadOnlyList<string> keys, IReadOnlyList<string> updateColumns)
    {
        var action = updateColumns.Count == 0
            ? $"{Quote(keys[0])} = {Quote(keys[0])}"
            : string.Join(", ", updateColumns.Select(c => $"{Quote(c)} = VALUES({Quote(c)})"));
        return $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", values)}) ON DUPLICATE KEY UPDATE {action}";
    }

    public override string TableExistsSql => "SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @p0";
    public override string ColumnNamesSql => "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @p0";
    public override string IndexNamesSql => "SELECT DISTINCT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @p0";

    public override string DefaultSql(ColumnDefinition c)
    {
        var d = c.Default!.Value;
        var sql = c.Type switch
        {
            ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64 => Number(d),
            ColumnTypes.Bool => d.GetBoolean() ? "1" : "0",
            ColumnTypes.DateTime => d.GetString() == "now"
                ? "CURRENT_TIMESTAMP(6)"
                : Literal(DateTime.Parse(d.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal).ToString("yyyy-MM-dd HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture), true),
            ColumnTypes.Json => Literal(d.GetRawText(), true),
            _ => Literal(d.GetString()!, true),
        };
        // TEXT, JSON and BLOB columns only accept expression defaults, which need parentheses.
        return c.Type is ColumnTypes.Text or ColumnTypes.Json ? $"({sql})" : sql;
    }

    public override IEnumerable<string> AddColumnSql(string table, ColumnDefinition c)
    {
        // MySQL fills existing rows with the type's neutral value, so NOT NULL needs no extra step.
        yield return $"ALTER TABLE {Quote(table)} ADD COLUMN {ColumnSql(c)}";
    }

    public override IEnumerable<string> AlterColumnSql(string table, ColumnDefinition old, ColumnDefinition nw)
    {
        yield return $"ALTER TABLE {Quote(table)} MODIFY COLUMN {ColumnSql(nw)}";
    }

    public override string RenameColumnSql(string table, string from, string to) => $"ALTER TABLE {Quote(table)} RENAME COLUMN {Quote(from)} TO {Quote(to)}";
    public override string RenameTableSql(string from, string to) => $"RENAME TABLE {Quote(from)} TO {Quote(to)}";
    public override string DropIndexSql(string table, string index) => $"DROP INDEX {Quote(index)} ON {Quote(table)}";
    public override string RelaxNullabilitySql(string table, ColumnDefinition c) => $"ALTER TABLE {Quote(table)} MODIFY COLUMN {ColumnSql(c with { Nullable = true, AutoIncrement = false })}";
}
