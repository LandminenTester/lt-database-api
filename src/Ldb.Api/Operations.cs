using System.Text.Json;
using Ldb.Api.Buffering;
using Ldb.Api.Security;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;

namespace Ldb.Api;

/// <summary>What an operation needs while it runs.</summary>
public sealed record OperationContext(IDatabaseProvider Provider, LdbOptions Options, ApiKey Key, WriteBuffer? Buffer = null);

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
        new("health", "/v1/health", false, async (_, c, ct) => new
        {
            status = "ok",
            provider = c.Provider.Name,
            database = await c.Provider.PingAsync(ct),
            writeBuffer = c.Buffer?.Stats() ?? new { enabled = false, pending = 0, oldestAgeSeconds = 0L, halted = false },
        }),
        new("schema.sync", "/v1/schema/sync", true, async (p, c, ct) =>
        {
            var r = Json.Parse<SyncRequest>(p);
            var ns = Names.Namespace(r.Namespace);
            var normalized = SchemaValidator.Normalize(r.Schema, c.Options.Schema.MaxTables);
            // Structure changes always run live, but writes waiting for the old structure must be applied first.
            if (c.Buffer is { Enabled: true } b && !r.DryRun) await b.DrainAsync(ns, ct);
            var result = await c.Provider.SyncAsync(ns, normalized, r.DryRun, c.Options.Schema.DestructiveMode, ct);
            if (!r.DryRun) c.Buffer?.InvalidateSchema(ns);
            return result;
        }),
        new("schema.describe", "/v1/schema/describe", false, async (p, c, ct) => await c.Provider.DescribeAsync(Json.Parse<DescribeRequest>(p).Namespace, ct)),
        new("insert", "/v1/data/insert", true, Write("insert", async (p, c, ct) =>
        {
            var r = Json.Parse<InsertRequest>(p);
            if (r.Rows is { Count: > 0 } rows && rows.Count > c.Options.Limits.MaxInsertRows)
                throw new ApiException(413, "too_many_rows", $"At most {c.Options.Limits.MaxInsertRows} rows can be inserted per request.");
            return await c.Provider.InsertAsync(r, ct);
        })),
        new("find", "/v1/data/find", false, async (p, c, ct) =>
        {
            await FlushBeforeRead(p, c, ct);
            return await c.Provider.FindAsync(Json.Parse<FindRequest>(p), c.Options.Limits.MaxRows, ct);
        }),
        new("update", "/v1/data/update", true, Write("update", async (p, c, ct) => await c.Provider.UpdateAsync(Json.Parse<UpdateRequest>(p), ct))),
        new("upsert", "/v1/data/upsert", true, Write("upsert", async (p, c, ct) => await c.Provider.UpsertAsync(Json.Parse<UpsertRequest>(p), ct))),
        new("delete", "/v1/data/delete", true, Write("delete", async (p, c, ct) => await c.Provider.DeleteAsync(Json.Parse<DeleteRequest>(p), ct))),
        new("count", "/v1/data/count", false, async (p, c, ct) =>
        {
            await FlushBeforeRead(p, c, ct);
            return await c.Provider.CountAsync(Json.Parse<CountRequest>(p), ct);
        }),
        new("flush", "/v1/data/flush", true, async (p, c, ct) =>
        {
            var ns = Names.Namespace(Json.Parse<DescribeRequest>(p).Namespace);
            return c.Buffer is null ? new FlushResult(0, 0, 0, false, Enabled: false) : await c.Buffer.FlushAsync(ns, ct);
        }),
    ];

    /// <summary>
    /// A write that is applied at once (live, the default) or, when the server owner turned on write-behind for this
    /// namespace and operation, validated, journaled and queued. A caller can always ask for "durability":"immediate".
    /// </summary>
    static Func<string, OperationContext, CancellationToken, Task<object>> Write(string name, Func<string, OperationContext, CancellationToken, Task<object>> live) =>
        async (p, c, ct) =>
        {
            var info = Info(p);
            if (info.Namespace is not null && c.Buffer is { } buffer && info.Durability != "immediate" && buffer.Applies(name, info.Namespace))
            {
                if (name is "update" or "delete" && info.All) RequireUnfilteredPermission(c, true);
                return await buffer.EnqueueAsync(name, info.Namespace, p, ct);
            }
            // An immediate write must not overtake older writes of the same namespace that are still queued.
            if (info.Namespace is not null && c.Buffer is { Enabled: true } b && b.Pending(info.Namespace) > 0) await b.DrainAsync(info.Namespace, ct);
            if (name is "update" or "delete") RequireUnfilteredPermission(c, info.All);
            return await live(p, c, ct);
        };

    static async Task FlushBeforeRead(string payload, OperationContext c, CancellationToken ct)
    {
        if (c.Buffer is { FlushBeforeRead: true } b && NamespaceOf(payload) is { } ns && b.Pending(ns) > 0) await b.DrainAsync(ns, ct);
    }

    static void RequireUnfilteredPermission(OperationContext c, bool all)
    {
        if (all && !c.Key.AllowUnfilteredWrites)
            throw new ApiException(403, "forbidden", "This key may not change or delete without a filter.");
    }

    public readonly record struct PayloadInfo(string? Namespace, string? IdempotencyKey, string? Durability, bool All);

    /// <summary>Reads the few top-level fields the API itself needs, without trusting the payload's shape.</summary>
    public static PayloadInfo Info(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return default;
            string? Str(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var all = doc.RootElement.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
            return new PayloadInfo(Str("namespace"), Str("idempotencyKey"), Str("durability")?.ToLowerInvariant(), all);
        }
        catch (JsonException) { return default; }
    }

    public static string? NamespaceOf(string payload) => Info(payload).Namespace;
    public static string? IdempotencyKeyOf(string payload) => Info(payload).IdempotencyKey;
}
