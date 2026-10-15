using System.Text.Json;

namespace Ldb.Core.Query;

/// <summary>
/// Filter syntax tree. Callers never send SQL or Mongo operators: they send this small DSL, which the parser validates
/// and the providers translate into parameters. See docs/API.md.
/// </summary>
public abstract record FilterNode;
public sealed record AndNode(IReadOnlyList<FilterNode> Children) : FilterNode;
public sealed record OrNode(IReadOnlyList<FilterNode> Children) : FilterNode;
public sealed record NotNode(FilterNode Child) : FilterNode;
public sealed record CompareNode(string Field, string Op, JsonElement Value) : FilterNode;

public static class FilterOps
{
    public const string Eq = "eq", Ne = "ne", Gt = "gt", Gte = "gte", Lt = "lt", Lte = "lte", In = "in", Nin = "nin",
        IsNull = "isNull", StartsWith = "startsWith", Contains = "contains";

    public static readonly HashSet<string> All = [Eq, Ne, Gt, Gte, Lt, Lte, In, Nin, IsNull, StartsWith, Contains];
}

public static class FilterParser
{
    const int MaxDepth = 8, MaxNodes = 128, MaxListItems = 1000, MaxTextLength = 1024;

    /// <summary>Returns null for a missing or empty filter ("match everything").</summary>
    public static FilterNode? Parse(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (e.ValueKind != JsonValueKind.Object) throw Bad("A filter must be an object.");
        if (!e.EnumerateObject().Any()) return null;
        var nodes = 0;
        return Node(e, 1, ref nodes);
    }

    static FilterNode Node(JsonElement e, int depth, ref int nodes)
    {
        if (depth > MaxDepth) throw Bad($"Filters may be nested at most {MaxDepth} levels deep.");
        if (++nodes > MaxNodes) throw Bad($"Filters may contain at most {MaxNodes} conditions.");
        if (e.ValueKind != JsonValueKind.Object) throw Bad("A filter condition must be an object.");

        var props = e.EnumerateObject().ToList();
        if (props.Count == 1 && props[0].Name is "and" or "or")
        {
            var list = props[0].Value;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0) throw Bad($"'{props[0].Name}' needs a non-empty list.");
            var children = new List<FilterNode>();
            foreach (var item in list.EnumerateArray()) children.Add(Node(item, depth + 1, ref nodes));
            return props[0].Name == "and" ? new AndNode(children) : new OrNode(children);
        }
        if (props.Count == 1 && props[0].Name == "not") return new NotNode(Node(props[0].Value, depth + 1, ref nodes));

        string? field = null, op = null; JsonElement? value = null;
        foreach (var p in props)
        {
            switch (p.Name)
            {
                case "field": field = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                case "op": op = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                case "value": value = p.Value; break;
                default: throw Bad($"Unknown filter key '{p.Name}'.");
            }
        }
        if (field is null || op is null) throw Bad("A condition needs 'field' and 'op'.");
        Names.Column(field);
        if (!FilterOps.All.Contains(op)) throw Bad($"Unknown filter operator '{op}'.");

        switch (op)
        {
            case FilterOps.IsNull:
                if (value is { } b && b.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad("'isNull' takes true or false.");
                return new CompareNode(field, op, value ?? JsonDocument.Parse("true").RootElement.Clone());
            case FilterOps.In or FilterOps.Nin:
                if (value is not { ValueKind: JsonValueKind.Array } arr) throw Bad($"'{op}' needs a list.");
                var count = arr.GetArrayLength();
                if (count is 0 or > MaxListItems) throw Bad($"'{op}' needs between 1 and {MaxListItems} values.");
                foreach (var item in arr.EnumerateArray()) RequireScalar(item);
                return new CompareNode(field, op, arr.Clone());
            default:
                if (value is null) throw Bad($"'{op}' needs a value.");
                RequireScalar(value.Value);
                if (op is FilterOps.StartsWith or FilterOps.Contains && (value.Value.ValueKind != JsonValueKind.String || value.Value.GetString()!.Length is 0 or > MaxTextLength))
                    throw Bad($"'{op}' needs a text between 1 and {MaxTextLength} characters.");
                return new CompareNode(field, op, value.Value.Clone());
        }
    }

    static void RequireScalar(JsonElement e)
    {
        if (e.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
            throw Bad("Filter values must be text, numbers, booleans or null.");
    }

    static ApiException Bad(string message) => new(400, "invalid_filter", message);
}
