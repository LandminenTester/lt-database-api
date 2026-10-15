using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ldb.Core;
using Ldb.Core.Schema;

namespace Ldb.Providers.Sql;

/// <summary>Converts JSON values to typed driver values (and back) according to the column type. Nothing is ever concatenated into SQL.</summary>
public static class SqlValues
{
    const int MaxJsonChars = 1_000_000;

    public static object? FromJson(ColumnDefinition c, JsonElement e, SqlDialect dialect)
    {
        if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var v = Coerce(c, e);
        return v is null ? null : dialect.ToDriver(c, v);
    }

    static object Coerce(ColumnDefinition c, JsonElement e)
    {
        try
        {
            switch (c.Type)
            {
                case ColumnTypes.Int32: return e.ValueKind == JsonValueKind.Number ? e.GetInt32() : throw Mismatch(c);
                case ColumnTypes.Int64: return e.ValueKind == JsonValueKind.Number ? e.GetInt64() : throw Mismatch(c);
                case ColumnTypes.Float64: return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : throw Mismatch(c);
                case ColumnTypes.Decimal:
                    return e.ValueKind switch
                    {
                        JsonValueKind.Number => e.GetDecimal(),
                        JsonValueKind.String => decimal.Parse(e.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
                        _ => throw Mismatch(c),
                    };
                case ColumnTypes.Bool: return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw Mismatch(c);
                case ColumnTypes.String or ColumnTypes.Text:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    var s = e.GetString()!;
                    if (s.Contains('\0')) throw new ApiException(400, "invalid_value", $"Column '{c.Name}' contains an invalid character.");
                    if (c.Type == ColumnTypes.String && c.Length is { } max && s.Length > max)
                        throw new ApiException(400, "value_too_long", $"Column '{c.Name}' accepts at most {max} characters.");
                    return s;
                case ColumnTypes.Json:
                    var raw = e.GetRawText();
                    if (raw.Length > MaxJsonChars) throw new ApiException(400, "value_too_long", $"Column '{c.Name}' is too large.");
                    return raw;
                case ColumnTypes.DateTime:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    return DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                case ColumnTypes.Date:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    return DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                case ColumnTypes.Uuid:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    return Guid.Parse(e.GetString()!);
                case ColumnTypes.Bytes:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    var bytes = Convert.FromBase64String(e.GetString()!);
                    if (c.Length is { } n && bytes.Length > n) throw new ApiException(400, "value_too_long", $"Column '{c.Name}' accepts at most {n} bytes.");
                    return bytes;
                default: throw Mismatch(c);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw Mismatch(c);
        }
    }

    public static object? ToJson(ColumnDefinition c, object? v)
    {
        if (v is null or DBNull) return null;
        return c.Type switch
        {
            ColumnTypes.Int32 => Convert.ToInt32(v, CultureInfo.InvariantCulture),
            ColumnTypes.Int64 => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            ColumnTypes.Float64 => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            ColumnTypes.Decimal => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
            ColumnTypes.Bool => v is bool b ? b : Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
            ColumnTypes.DateTime => AsUtc((DateTime)v).ToString("O", CultureInfo.InvariantCulture),
            ColumnTypes.Date => v is DateOnly d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ((DateTime)v).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ColumnTypes.Uuid => v.ToString(),
            ColumnTypes.Bytes => Convert.ToBase64String((byte[])v),
            ColumnTypes.Json => ParseJson(v.ToString()!),
            _ => v.ToString(),
        };
    }

    static JsonNode? ParseJson(string text)
    {
        try { return JsonNode.Parse(text); } catch (JsonException) { return JsonValue.Create(text); }
    }

    static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    static ApiException Mismatch(ColumnDefinition c) => new(400, "invalid_value", $"The value for column '{c.Name}' does not fit its type '{c.Type}'.");
}
