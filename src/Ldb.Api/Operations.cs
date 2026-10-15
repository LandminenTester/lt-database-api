using System.Text.Json;
using Ldb.Api.Security;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;

namespace Ldb.Api;

/// <summary>What an operation needs while it runs.</summary>
public sealed record OperationContext(IDatabaseProvider Provider, LdbOptions Options, ApiKey Key);

public sealed record Operation(string Name, string Path, bool Writes, Func<string, OperationContext, CancellationToken, Task<object>> Run)
{
    /// <summary>Operations that act on one namespace carry it in the payload and are checked against the key.</summary>
    public bool UsesNamespace => Name != "health";
}

/// <summary>The complete list of operations the API offers. Every request body is a signed envelope, see docs/API.md.</summary>
public static class Operations
{
    public static readonly IReadOnlyList<Operation> All =
    [
        new("health", "/v1/health", false, async (_, c, ct) => new { status = "ok", provider = c.Provider.Name, database = await c.Provider.PingAsync(ct) }),
        new("schema.sync", "/v1/schema/sync", true, async (p, c, ct) =>
        {
            var r = Json.Parse<SyncRequest>(p);
            var ns = Names.Namespace(r.Namespace);
            var normalized = SchemaValidator.Normalize(r.Schema, c.Options.Schema.MaxTables);
            return await c.Provider.SyncAsync(ns, normalized, r.DryRun, c.Options.Schema.DestructiveMode, ct);
        }),
        new("schema.describe", "/v1/schema/describe", false, async (p, c, ct) => await c.Provider.DescribeAsync(Json.Parse<DescribeRequest>(p).Namespace, ct)),
        new("insert", "/v1/data/insert", true, async (p, c, ct) =>
        {
            var r = Json.Parse<InsertRequest>(p);
            if (r.Rows is { Count: > 0 } rows && rows.Count > c.Options.Limits.MaxInsertRows)
                throw new ApiException(413, "too_many_rows", $"At most {c.Options.Limits.MaxInsertRows} rows can be inserted per request.");
            return await c.Provider.InsertAsync(r, ct);
        }),
        new("find", "/v1/data/find", false, async (p, c, ct) => await c.Provider.FindAsync(Json.Parse<FindRequest>(p), c.Options.Limits.MaxRows, ct)),
        new("update", "/v1/data/update", true, async (p, c, ct) =>
        {
            var r = Json.Parse<UpdateRequest>(p);
            RequireUnfilteredPermission(c, r.All);
            return await c.Provider.UpdateAsync(r, ct);
        }),
        new("upsert", "/v1/data/upsert", true, async (p, c, ct) => await c.Provider.UpsertAsync(Json.Parse<UpsertRequest>(p), ct)),
        new("delete", "/v1/data/delete", true, async (p, c, ct) =>
        {
            var r = Json.Parse<DeleteRequest>(p);
            RequireUnfilteredPermission(c, r.All);
            return await c.Provider.DeleteAsync(r, ct);
        }),
        new("count", "/v1/data/count", false, async (p, c, ct) => await c.Provider.CountAsync(Json.Parse<CountRequest>(p), ct)),
    ];

    static void RequireUnfilteredPermission(OperationContext c, bool all)
    {
        if (all && !c.Key.AllowUnfilteredWrites)
            throw new ApiException(403, "forbidden", "This key may not change or delete without a filter.");
    }

    /// <summary>Reads the namespace from a payload without trusting its shape.</summary>
    public static string? NamespaceOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.String
                ? ns.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string? IdempotencyKeyOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("idempotencyKey", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
