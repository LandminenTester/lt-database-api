using System.Text;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Query;
using Ldb.Core.Schema;

namespace Ldb.Providers.Sql;

/// <summary>Collects bind parameters for one statement.</summary>
public sealed class SqlParams
{
    public List<(string Name, object? Value)> Items { get; } = [];
    public string Add(object? value)
    {
        var name = "@p" + Items.Count;
        Items.Add((name, value));
        return name;
    }
}

/// <summary>Translates the filter DSL into a parameterized WHERE clause. Column names come only from the stored schema.</summary>
public sealed class SqlQueryBuilder(SqlDialect dialect)
{
    public string? Where(FilterNode? node, TableDefinition table, SqlParams ps) => node is null ? null : Build(node, table, ps);

    string Build(FilterNode node, TableDefinition table, SqlParams ps)
    {
        switch (node)
        {
            case AndNode a: return "(" + string.Join(" AND ", a.Children.Select(c => Build(c, table, ps))) + ")";
            case OrNode o: return "(" + string.Join(" OR ", o.Children.Select(c => Build(c, table, ps))) + ")";
            case NotNode n: return "(NOT " + Build(n.Child, table, ps) + ")";
            case CompareNode c: return Compare(c, table, ps);
            default: throw new ApiException(400, "invalid_filter", "Unsupported filter node.");
        }
    }

    string Compare(CompareNode c, TableDefinition table, SqlParams ps)
    {
        var col = Column(table, c.Field);
        var q = dialect.Quote(col.Name);
        switch (c.Op)
        {
            case FilterOps.IsNull:
                return c.Value.ValueKind == JsonValueKind.True ? $"{q} IS NULL" : $"{q} IS NOT NULL";
            case FilterOps.In or FilterOps.Nin:
                var items = c.Value.EnumerateArray().Select(i =>
                {
                    var v = SqlValues.FromJson(col, i, dialect) ?? throw new ApiException(400, "invalid_filter", "Lists cannot contain null. Use isNull.");
                    return dialect.ParamExpr(col, ps.Add(v));
                });
                return $"{q} {(c.Op == FilterOps.In ? "IN" : "NOT IN")} ({string.Join(", ", items)})";
            case FilterOps.StartsWith or FilterOps.Contains:
                if (col.Type is not (ColumnTypes.String or ColumnTypes.Text)) throw new ApiException(400, "invalid_filter", $"'{c.Op}' only works on text columns.");
                var text = EscapeLike(c.Value.GetString()!);
                var pattern = c.Op == FilterOps.StartsWith ? text + "%" : "%" + text + "%";
                return $"{q} LIKE {ps.Add(pattern)} {dialect.LikeEscape}";
        }

        var value = SqlValues.FromJson(col, c.Value, dialect);
        if (value is null)
        {
            if (c.Op == FilterOps.Eq) return $"{q} IS NULL";
            if (c.Op == FilterOps.Ne) return $"{q} IS NOT NULL";
            throw new ApiException(400, "invalid_filter", $"'{c.Op}' cannot compare with null.");
        }
        var op = c.Op switch
        {
            FilterOps.Eq => "=", FilterOps.Ne => "<>", FilterOps.Gt => ">", FilterOps.Gte => ">=", FilterOps.Lt => "<", FilterOps.Lte => "<=",
            _ => throw new ApiException(400, "invalid_filter", "Unsupported operator."),
        };
        return $"{q} {op} {dialect.ParamExpr(col, ps.Add(value))}";
    }

    public static ColumnDefinition Column(TableDefinition table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name == name) ?? throw new ApiException(400, "unknown_column", $"Table '{table.Name}' has no column '{name}'.");

    static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public string OrderBy(TableDefinition table, List<SortSpec>? sort)
    {
        var parts = new List<string>();
        foreach (var s in sort ?? [])
        {
            var col = Column(table, s.Field);
            var dir = (s.Dir ?? "asc").ToLowerInvariant();
            if (dir is not ("asc" or "desc")) throw new ApiException(400, "invalid_sort", "Sort direction must be asc or desc.");
            if (col.Type is ColumnTypes.Json) throw new ApiException(400, "invalid_sort", "json columns cannot be sorted.");
            parts.Add($"{dialect.Quote(col.Name)} {dir.ToUpperInvariant()}");
        }
        // A stable order makes paging reliable.
        if (parts.Count == 0 && table.PrimaryKey is { Count: > 0 } pk) parts.AddRange(pk.Select(dialect.Quote));
        return parts.Count == 0 ? "" : " ORDER BY " + string.Join(", ", parts);
    }

    public static StringBuilder Sb() => new();
}
