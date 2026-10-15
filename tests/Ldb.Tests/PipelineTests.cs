using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Protocol;
using Ldb.Core.Query;
using Ldb.Core.Schema;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ldb.Tests;

/// <summary>Counts what reaches the database layer and answers with fixed data.</summary>
public sealed class FakeProvider : IDatabaseProvider
{
    public string Name => "fake";
    public List<string> Calls { get; } = [];
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<SyncResult> SyncAsync(string ns, SchemaDefinition desired, bool dryRun, DestructiveMode mode, CancellationToken ct) { Calls.Add("sync:" + ns); return Task.FromResult(new SyncResult(ns, "h", true, dryRun, "planned", [])); }
    public Task<DescribeResult> DescribeAsync(string ns, CancellationToken ct) => Task.FromResult(new DescribeResult(ns, "h", RecordingProvider.Players, null));
    public Task<InsertResult> InsertAsync(InsertRequest r, CancellationToken ct) { Calls.Add("insert"); return Task.FromResult(new InsertResult(r.Rows.Count, [1])); }
    public Task<FindResult> FindAsync(FindRequest r, int maxRows, CancellationToken ct) { Calls.Add("find"); return Task.FromResult(new FindResult([], false)); }
    public Task<AffectedResult> UpdateAsync(UpdateRequest r, CancellationToken ct) { Calls.Add("update"); return Task.FromResult(new AffectedResult(1)); }
    public Task<AffectedResult> UpsertAsync(UpsertRequest r, CancellationToken ct) { Calls.Add("upsert"); return Task.FromResult(new AffectedResult(1)); }
    public Task<AffectedResult> DeleteAsync(DeleteRequest r, CancellationToken ct) { Calls.Add("delete"); return Task.FromResult(new AffectedResult(1)); }
    public Task<CountResult> CountAsync(CountRequest r, CancellationToken ct) { Calls.Add("count"); return Task.FromResult(new CountResult(3)); }
}

public sealed class ApiHarness : IDisposable
{
    public const string Secret = "0123456789abcdef0123456789abcdef-secret-one";
    public const string Next = "fedcba9876543210fedcba9876543210-secret-two";
    readonly List<string> envNames = [];
    public readonly FakeProvider Provider = new();
    public readonly WebApplicationFactory<Program> Factory;
    public readonly HttpClient Client;

    public ApiHarness(Action<Dictionary<string, string>>? configure = null)
    {
        var env = new Dictionary<string, string>
        {
            ["LDB_Ldb__Database__DmlConnectionString"] = "unused",
            ["LDB_Ldb__Security__AllowInsecureHttp"] = "true",
            ["LDB_Ldb__Security__Keys__0__KeyId"] = "main",
            ["LDB_Ldb__Security__Keys__0__Current"] = "env:LDB_TEST_SECRET",
            ["LDB_Ldb__Security__Keys__0__Next"] = "env:LDB_TEST_NEXT",
            ["LDB_Ldb__Security__Keys__0__AllowedNamespaces__0"] = "demo",
            ["LDB_TEST_SECRET"] = Secret,
            ["LDB_TEST_NEXT"] = Next,
        };
        configure?.Invoke(env);
        foreach (var (k, v) in env) { Environment.SetEnvironmentVariable(k, v); envNames.Add(k); }
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IDatabaseProvider>();
            s.AddSingleton<IDatabaseProvider>(Provider);
        }));
        Client = Factory.CreateClient();
    }

    public static string Nonce() => Base64Url(RandomNumberGenerator.GetBytes(16));
    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public HttpRequestMessage Request(string path, object payload, string? secret = null, long? ts = null, string? nonce = null, string kid = "main", bool tamper = false)
    {
        var body = JsonSerializer.Serialize(payload, Json.Options);
        ts ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        nonce ??= Nonce();
        var sig = Signing.SignRequest(Encoding.UTF8.GetBytes(secret ?? Secret), path, kid, ts.Value, nonce, body);
        var env = new RequestEnvelope(1, kid, ts.Value, nonce, tamper ? body + " " : body, sig);
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(env, options: Json.Options) };
    }

    public async Task<(HttpStatusCode Status, ResponseEnvelope? Body, string Nonce)> SendAsync(string path, object payload, string? secret = null, long? ts = null, string? nonce = null, string kid = "main", bool tamper = false)
    {
        nonce ??= Nonce();
        var res = await Client.SendAsync(Request(path, payload, secret, ts, nonce, kid, tamper));
        var env = await res.Content.ReadFromJsonAsync<ResponseEnvelope>(Json.Options);
        return (res.StatusCode, env, nonce);
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        foreach (var n in envNames) Environment.SetEnvironmentVariable(n, null);
    }
}

[Collection("api")]
public class PipelineTests
{
    static readonly object Find = new { @namespace = "demo", table = "players" };

    [Fact]
    public async Task Valid_request_returns_a_signed_result()
    {
        using var h = new ApiHarness();
        var (status, body, nonce) = await h.SendAsync("/v1/data/count", Find);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!.Ok);
        Assert.Contains("\"count\":3", body.Payload);
        Assert.Equal(Signing.SignResponse(Encoding.UTF8.GetBytes(ApiHarness.Secret), nonce, 200, body.Payload), body.Sig);
        Assert.Contains("count", h.Provider.Calls);
    }

    [Fact]
    public async Task Second_secret_is_accepted_during_rotation_and_signs_the_answer()
    {
        using var h = new ApiHarness();
        var (status, body, nonce) = await h.SendAsync("/v1/data/count", Find, secret: ApiHarness.Next);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Signing.SignResponse(Encoding.UTF8.GetBytes(ApiHarness.Next), nonce, 200, body!.Payload), body.Sig);
    }

    [Fact]
    public async Task Wrong_secret_unknown_key_and_tampered_payload_all_look_the_same()
    {
        using var h = new ApiHarness();
        var wrong = await h.SendAsync("/v1/data/count", Find, secret: "this-is-a-different-secret-of-sufficient-length");
        var unknown = await h.SendAsync("/v1/data/count", Find, kid: "nobody");
        var tampered = await h.SendAsync("/v1/data/count", Find, tamper: true);
        foreach (var r in new[] { wrong, unknown, tampered })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
            Assert.Null(r.Body!.Sig);
            Assert.Equal("unauthorized", r.Body.Code);
        }
        Assert.Equal(wrong.Body!.Payload, unknown.Body!.Payload);
        Assert.Equal(wrong.Body.Payload, tampered.Body!.Payload);
        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task A_request_cannot_be_replayed()
    {
        using var h = new ApiHarness();
        var nonce = ApiHarness.Nonce();
        Assert.Equal(HttpStatusCode.OK, (await h.SendAsync("/v1/data/count", Find, nonce: nonce)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SendAsync("/v1/data/count", Find, nonce: nonce)).Status);
        Assert.Single(h.Provider.Calls);
    }

    [Fact]
    public async Task Old_and_future_timestamps_are_refused()
    {
        using var h = new ApiHarness();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SendAsync("/v1/data/count", Find, ts: now - 120)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SendAsync("/v1/data/count", Find, ts: now + 120)).Status);
        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task A_signature_for_one_endpoint_does_not_work_on_another()
    {
        using var h = new ApiHarness();
        var body = JsonSerializer.Serialize(Find, Json.Options);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var nonce = ApiHarness.Nonce();
        var sig = Signing.SignRequest(Encoding.UTF8.GetBytes(ApiHarness.Secret), "/v1/data/count", "main", ts, nonce, body);
        var res = await h.Client.PostAsJsonAsync("/v1/data/delete", new RequestEnvelope(1, "main", ts, nonce, body, sig), Json.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Other_namespaces_are_forbidden_with_a_signed_error()
    {
        using var h = new ApiHarness();
        var (status, body, nonce) = await h.SendAsync("/v1/data/count", new { @namespace = "secrets", table = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(Signing.SignResponse(Encoding.UTF8.GetBytes(ApiHarness.Secret), nonce, 403, body!.Payload), body.Sig);
        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task Missing_or_invalid_namespace_is_a_bad_request()
    {
        using var h = new ApiHarness();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.SendAsync("/v1/data/count", new { table = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.SendAsync("/v1/data/count", new { @namespace = "Bad Name", table = "x" })).Status);
    }

    [Fact]
    public async Task Operations_can_be_limited_per_key()
    {
        using var h = new ApiHarness(e => { e["LDB_Ldb__Security__Keys__0__AllowedOperations__0"] = "find"; e["LDB_Ldb__Security__Keys__0__AllowedOperations__1"] = "count"; });
        Assert.Equal(HttpStatusCode.OK, (await h.SendAsync("/v1/data/count", Find)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync("/v1/data/delete", new { @namespace = "demo", table = "players", filter = new { field = "id", op = "eq", value = 1 } })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync("/v1/schema/sync", new { @namespace = "demo", schema = new { tables = Array.Empty<object>() } })).Status);
    }

    [Fact]
    public async Task Unfiltered_writes_need_an_explicit_permission()
    {
        using var h = new ApiHarness();
        var denied = await h.SendAsync("/v1/data/delete", new { @namespace = "demo", table = "players", all = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.DoesNotContain("delete", h.Provider.Calls);

        using var allowed = new ApiHarness(e => e["LDB_Ldb__Security__Keys__0__AllowUnfilteredWrites"] = "true");
        Assert.Equal(HttpStatusCode.OK, (await allowed.SendAsync("/v1/data/delete", new { @namespace = "demo", table = "players", all = true })).Status);
    }

    [Fact]
    public async Task Keys_can_be_limited_to_network_ranges()
    {
        using var h = new ApiHarness(e => e["LDB_Ldb__Security__Keys__0__AllowedCidrs__0"] = "10.0.0.0/8");
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SendAsync("/v1/data/count", Find)).Status);
    }

    [Fact]
    public async Task Each_key_has_its_own_rate_limit()
    {
        using var h = new ApiHarness(e => e["LDB_Ldb__Security__Keys__0__RequestsPerMinute"] = "2");
        Assert.Equal(HttpStatusCode.OK, (await h.SendAsync("/v1/data/count", Find)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.SendAsync("/v1/data/count", Find)).Status);
        Assert.Equal((HttpStatusCode)429, (await h.SendAsync("/v1/data/count", Find)).Status);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_address_out_for_the_minute()
    {
        using var h = new ApiHarness(e => e["LDB_Ldb__Security__FailedAuthPerMinute"] = "3");
        for (var i = 0; i < 3; i++) await h.SendAsync("/v1/data/count", Find, secret: "wrong-secret-wrong-secret-wrong-secret-xx");
        Assert.Equal((HttpStatusCode)429, (await h.SendAsync("/v1/data/count", Find)).Status);
    }

    [Fact]
    public async Task Large_bodies_are_refused()
    {
        using var h = new ApiHarness(e => e["LDB_Ldb__Security__MaxBodyBytes"] = "2000");
        var res = await h.SendAsync("/v1/data/insert", new { @namespace = "demo", table = "players", rows = new[] { new { name = new string('a', 5000) } } });
        Assert.Contains(res.Status, new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest });
        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task Garbage_is_a_bad_request()
    {
        using var h = new ApiHarness();
        var res = await h.Client.PostAsync("/v1/data/count", new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Idempotency_keys_prevent_double_writes_and_detect_misuse()
    {
        using var h = new ApiHarness();
        object Insert(string name) => new { @namespace = "demo", table = "players", rows = new[] { new { name } }, idempotencyKey = "abc-1" };
        var first = await h.SendAsync("/v1/data/insert", Insert("a"));
        var retry = await h.SendAsync("/v1/data/insert", Insert("a"));
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(first.Body!.Payload, retry.Body!.Payload);
        Assert.Single(h.Provider.Calls, c => c == "insert");
        // The retry is signed for its own nonce, so the client can verify it.
        Assert.Equal(Signing.SignResponse(Encoding.UTF8.GetBytes(ApiHarness.Secret), retry.Nonce, 200, retry.Body.Payload), retry.Body.Sig);
        Assert.Equal((HttpStatusCode)422, (await h.SendAsync("/v1/data/insert", Insert("different"))).Status);
    }

    [Fact]
    public async Task Plain_http_from_outside_is_refused_unless_allowed()
    {
        using var h = new ApiHarness(e => e["LDB_Ldb__Security__AllowInsecureHttp"] = "false");
        var res = await h.Client.PostAsync("/v1/data/count", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var h = new ApiHarness();
        var res = await h.Client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", res.Headers.GetValues("Cache-Control").Single());
        Assert.False(res.Headers.Contains("Server"));
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", json);
        Assert.Contains("\"time\":", json);
        Assert.DoesNotContain("provider", json);
    }

    [Fact]
    public async Task Weak_secrets_stop_the_server_from_starting()
    {
        var ex = Assert.ThrowsAny<Exception>(() => { using var h = new ApiHarness(e => e["LDB_TEST_SECRET"] = "short"); });
        Assert.Contains("too short", ex.ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Plain_secrets_in_configuration_are_refused()
    {
        var ex = Assert.ThrowsAny<Exception>(() => { using var h = new ApiHarness(e => e["LDB_Ldb__Security__Keys__0__Current"] = "0123456789abcdef0123456789abcdef-plain"); });
        Assert.Contains("env:NAME or file:", ex.ToString());
        await Task.CompletedTask;
    }
}

[CollectionDefinition("api", DisableParallelization = true)]
public class ApiCollection;
