using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ldb.Api.Security;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Protocol;
using Microsoft.Extensions.Options;

namespace Ldb.Api;

/// <summary>
/// Authenticates, authorizes and runs one request. The order is deliberate: cheap checks first, the signature before
/// anything is looked up, the nonce only after the signature, and every authenticated response is signed.
/// </summary>
public sealed class RequestPipeline(
    IOptions<LdbOptions> options, IDatabaseProvider provider, KeyRegistry keys, NonceCache nonces,
    MinuteCounter failures, MinuteCounter keyRates, IdempotencyCache idempotency, ILogger<RequestPipeline> logger, TimeProvider time)
{
    static readonly Meter Meter = new("Ldb.Api");
    static readonly Counter<long> Requests = Meter.CreateCounter<long>("ldb_requests_total");
    static readonly Counter<long> AuthFailures = Meter.CreateCounter<long>("ldb_auth_failures_total");

    readonly LdbOptions o = options.Value;

    public async Task<IResult> HandleAsync(HttpContext ctx, Operation op)
    {
        var started = Stopwatch.GetTimestamp();
        var ip = ctx.Connection.RemoteIpAddress;
        var ipKey = ip?.ToString() ?? "unknown";
        var now = time.GetUtcNow();
        var unix = now.ToUnixTimeSeconds();
        string kid = "-", ns = "-"; var status = 500;
        try
        {
            if (failures.Peek(ipKey, unix) >= o.Security.FailedAuthPerMinute)
            {
                status = 429;
                return Plain(429, "rate_limited", "Too many failed attempts. Try again later.");
            }

            var body = await ReadBodyAsync(ctx);
            if (body is null) { status = 413; return Plain(413, "payload_too_large", "The request body is too large."); }

            RequestEnvelope? env;
            try { env = JsonSerializer.Deserialize<RequestEnvelope>(body, Json.Options); }
            catch (JsonException) { env = null; }
            if (env is null || env.V != 1 || string.IsNullOrEmpty(env.Kid) || string.IsNullOrEmpty(env.Nonce) || env.Nonce.Length > 64 || string.IsNullOrEmpty(env.Sig) || env.Payload is null)
            {
                status = 400;
                return Plain(400, "invalid_request", "The request is not a valid envelope.");
            }

            // 1. Signature, with the same amount of work for known and unknown key ids.
            var key = keys.Find(env.Kid);
            byte[]? matched = null;
            foreach (var secret in key?.Secrets ?? [KeyRegistry.DummySecret])
            {
                var expected = Signing.SignRequest(secret, op.Path, env.Kid, env.Ts, env.Nonce, env.Payload);
                if (Signing.FixedEquals(expected, env.Sig) && key is not null) matched = secret;
            }
            var fresh = Math.Abs(unix - env.Ts) <= o.Security.TimestampToleranceSeconds;
            if (key is null || matched is null || !fresh || !key.AllowsAddress(ip) || !nonces.TryUse(env.Kid, env.Nonce, now.UtcTicks))
            {
                failures.Hit(ipKey, unix);
                AuthFailures.Add(1);
                status = 401;
                return Plain(401, "unauthorized", "Authentication failed.");
            }
            kid = key.KeyId;

            // From here on the caller is known, so errors are signed and can be trusted by the client.
            try
            {
                if (keyRates.Hit(kid, unix) > key.RequestsPerMinute) throw new ApiException(429, "rate_limited", "This key sent too many requests this minute.");
                if (!key.AllowsOperation(op.Name)) throw new ApiException(403, "forbidden", "This key may not use this operation.");
                if (op.UsesNamespace)
                {
                    var requested = Operations.NamespaceOf(env.Payload);
                    if (requested is null || !Names.IsValid(requested, Names.MaxNamespace)) throw new ApiException(400, "invalid_identifier", "The payload needs a valid 'namespace'.");
                    ns = requested;
                    if (!key.AllowsNamespace(ns)) throw new ApiException(403, "forbidden", "This key may not use this namespace.");
                }

                var idemKey = op.Writes ? Operations.IdempotencyKeyOf(env.Payload) : null;
                var cacheKey = idemKey is null ? null : $"{kid}|{op.Name}|{idemKey}";
                var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(env.Payload)));
                if (cacheKey is not null && idempotency.Find(cacheKey, now.UtcTicks) is { } hit)
                {
                    if (hit.PayloadHash != payloadHash) throw new ApiException(422, "idempotency_conflict", "This idempotency key was already used with a different request.");
                    status = hit.Status;
                    return Signed(matched, env.Nonce, hit.Status, hit.Payload, null);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.Database.CommandTimeoutSeconds * 4)));
                var result = await op.Run(env.Payload, new OperationContext(provider, o, key), timeout.Token);
                var payload = Json.Write(result);
                if (cacheKey is not null) idempotency.Store(cacheKey, payloadHash, 200, payload, now.UtcTicks);
                status = 200;
                return Signed(matched, env.Nonce, 200, payload, null);
            }
            catch (ApiException ex)
            {
                status = ex.Status;
                return Signed(matched, env.Nonce, ex.Status, Json.Write(new { error = new { code = ex.Code, message = ex.Message } }), ex.Code);
            }
            catch (OperationCanceledException)
            {
                status = 504;
                return Signed(matched, env.Nonce, 504, Json.Write(new { error = new { code = "timeout", message = "The operation took too long." } }), "timeout");
            }
            catch (Exception ex)
            {
                // Details stay in the log. The client only learns that something went wrong.
                logger.LogError("Unhandled {Type} in {Operation}", ex.GetType().Name, op.Name);
                status = 500;
                return Signed(matched, env.Nonce, 500, Json.Write(new { error = new { code = "internal_error", message = "The server could not complete the request." } }), "internal_error");
            }
        }
        finally
        {
            Requests.Add(1, new KeyValuePair<string, object?>("operation", op.Name), new KeyValuePair<string, object?>("status", status));
            logger.LogInformation("audit kid={Kid} ip={Ip} op={Operation} ns={Namespace} status={Status} ms={Ms} request={RequestId}",
                kid, ipKey, op.Name, ns, status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ctx.TraceIdentifier);
        }
    }

    async Task<string?> ReadBodyAsync(HttpContext ctx)
    {
        var limit = o.Security.MaxBodyBytes;
        if (ctx.Request.ContentLength > limit) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            if (ms.Length + read > limit) return null;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    static IResult Plain(int status, string code, string message) =>
        Results.Json(new ResponseEnvelope(1, false, status, Json.Write(new { error = new { code, message } }), null, code), Json.Options, statusCode: status);

    static IResult Signed(byte[] secret, string requestNonce, int status, string payload, string? code) =>
        Results.Json(new ResponseEnvelope(1, status < 400, status, payload, Signing.SignResponse(secret, requestNonce, status, payload), code), Json.Options, statusCode: status);
}
