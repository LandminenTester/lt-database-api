using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ldb.Core;
using Ldb.Core.Schema;
using MongoDB.Bson;

namespace Ldb.Providers.Mongo;

/// <summary>Converts JSON values to BSON (and back) by column type. Documents are built value by value, never from raw JSON text.</summary>
public static class MongoValues
{
    public static BsonValue ToBson(ColumnDefinition c, JsonElement e)
    {
        if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return BsonNull.Value;
        try
        {
            switch (c.Type)
            {
                case ColumnTypes.Int32: return e.ValueKind == JsonValueKind.Number ? new BsonInt32(e.GetInt32()) : throw Mismatch(c);
                case ColumnTypes.Int64: return e.ValueKind == JsonValueKind.Number ? new BsonInt64(e.GetInt64()) : throw Mismatch(c);
                case ColumnTypes.Float64: return e.ValueKind == JsonValueKind.Number ? new BsonDouble(e.GetDouble()) : throw Mismatch(c);
                case ColumnTypes.Decimal:
                    return e.ValueKind switch
                    {
                        JsonValueKind.Number => new BsonDecimal128(e.GetDecimal()),
                        JsonValueKind.String => new BsonDecimal128(decimal.Parse(e.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)),
                        _ => throw Mismatch(c),
                    };
                case ColumnTypes.Bool: return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? BsonBoolean.Create(e.GetBoolean()) : throw Mismatch(c);
                case ColumnTypes.String or ColumnTypes.Text:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    var s = e.GetString()!;
                    if (s.Contains('\0')) throw new ApiException(400, "invalid_value", $"Column '{c.Name}' contains an invalid character.");
                    if (c.Type == ColumnTypes.String && c.Length is { } max && s.Length > max)
                        throw new ApiException(400, "value_too_long", $"Column '{c.Name}' accepts at most {max} characters.");
                    return new BsonString(s);
                case ColumnTypes.Json: return FromElement(e, 0);
                case ColumnTypes.DateTime:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    return new BsonDateTime(DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
                case ColumnTypes.Date:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    var d = DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return new BsonDateTime(DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));
                case ColumnTypes.Uuid:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    return new BsonString(Guid.Parse(e.GetString()!).ToString());
                case ColumnTypes.Bytes:
                    if (e.ValueKind != JsonValueKind.String) throw Mismatch(c);
                    var bytes = Convert.FromBase64String(e.GetString()!);
                    if (c.Length is { } n && bytes.Length > n) throw new ApiException(400, "value_too_long", $"Column '{c.Name}' accepts at most {n} bytes.");
                    return new BsonBinaryData(bytes);
                default: throw Mismatch(c);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw Mismatch(c);
        }
    }

    /// <summary>Plain JSON to BSON. Keys that start with '$' or contain '.' are refused, so stored data can never act as an operator.</summary>
    static BsonValue FromElement(JsonElement e, int depth)
    {
        if (depth > 32) throw new ApiException(400, "invalid_value", "JSON values are nested too deeply.");
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var doc = new BsonDocument();
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.StartsWith('$') || p.Name.Contains('.') || p.Name.Contains('\0'))
                        throw new ApiException(400, "invalid_value", "JSON keys may not start with '$' or contain '.'.");
                    doc[p.Name] = FromElement(p.Value, depth + 1);
                }
                return doc;
            case JsonValueKind.Array: return new BsonArray(e.EnumerateArray().Select(i => FromElement(i, depth + 1)));
            case JsonValueKind.String: return new BsonString(e.GetString()!);
            case JsonValueKind.Number: return e.TryGetInt64(out var l) ? new BsonInt64(l) : new BsonDouble(e.GetDouble());
            case JsonValueKind.True: return BsonBoolean.True;
            case JsonValueKind.False: return BsonBoolean.False;
            default: return BsonNull.Value;
        }
    }

    public static object? ToJson(ColumnDefinition c, BsonValue v)
    {
        if (v is null || v.IsBsonNull) return null;
        return c.Type switch
        {
            ColumnTypes.Int32 => v.ToInt32(),
            ColumnTypes.Int64 => v.ToInt64(),
            ColumnTypes.Float64 => v.ToDouble(),
            ColumnTypes.Decimal => v.ToDecimal(),
            ColumnTypes.Bool => v.ToBoolean(),
            ColumnTypes.DateTime => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ColumnTypes.Date => v.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ColumnTypes.Bytes => Convert.ToBase64String(v.AsBsonBinaryData.Bytes),
            ColumnTypes.Json => ToNode(v),
            _ => v.IsString ? v.AsString : v.ToString(),
        };
    }

    static JsonNode? ToNode(BsonValue v) => v.BsonType switch
    {
        BsonType.Document => new JsonObject(v.AsBsonDocument.Select(e => KeyValuePair.Create(e.Name, ToNode(e.Value)))),
        BsonType.Array => new JsonArray([.. v.AsBsonArray.Select(ToNode)]),
        BsonType.String => JsonValue.Create(v.AsString),
        BsonType.Boolean => JsonValue.Create(v.AsBoolean),
        BsonType.Int32 => JsonValue.Create(v.AsInt32),
        BsonType.Int64 => JsonValue.Create(v.AsInt64),
        BsonType.Double => JsonValue.Create(v.AsDouble),
        BsonType.Decimal128 => JsonValue.Create(v.ToDecimal()),
        BsonType.DateTime => JsonValue.Create(v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        _ => null,
    };

    /// <summary>Escapes every ASCII punctuation character so text is matched literally inside a regular expression.</summary>
    public static string EscapeRegex(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (ch < 128 && !char.IsLetterOrDigit(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static ApiException Mismatch(ColumnDefinition c) => new(400, "invalid_value", $"The value for column '{c.Name}' does not fit its type '{c.Type}'.");
}
