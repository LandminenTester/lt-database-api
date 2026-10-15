using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ldb.Core;

/// <summary>The one set of JSON options used on the wire (camelCase, strict about unknown enum values).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 32,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }

    public static T Parse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? throw new ApiException(400, "invalid_request", "Payload is empty."); }
        catch (JsonException) { throw new ApiException(400, "invalid_request", "Payload is not valid for this operation."); }
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
