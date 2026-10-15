using System.Net;
using System.Text.Json;
using Ldb.Api.Buffering;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ldb.Tests;

/// <summary>A provider that records what reaches the database layer and can be told to fail.</summary>
public sealed class RecordingProvider : IDatabaseProvider
{
    public static readonly SchemaDefinition Players = SchemaValidator.Normalize(Json.Parse<SchemaDefinition>("""
    {"tables":[{"name":"players","primaryKey":["id"],"columns":[
      {"name":"id","type":"int64","autoIncrement":true},
      {"name":"name","type":"string","length":16,"unique":true},
      {"name":"score","type":"int32","default":0},
      {"name":"bio","type":"text","nullable":true}]}]}
    """));

    public string Name { get; set; } = "recording";
    public List<string> Calls { get; } = [];
    public SchemaDefinition? Schema { get; set; } = Players;
    /// <summary>Called before each write with its description. Throw an ApiException to make it fail.</summary>
    public Action<string, object>? Intercept { get; set; }

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<SyncResult> SyncAsync(string ns, SchemaDefinition desired, bool dryRun, DestructiveMode mode, CancellationToken ct) { Calls.Add("sync:" + ns); return Task.FromResult(new SyncResult(ns, "h", true, dryRun, "applied", [])); }
    public Task<DescribeResult> DescribeAsync(string ns, CancellationToken ct) => Task.FromResult(new DescribeResult(ns, "h", Schema, null));

    Task<T> Record<T>(string name, object request, T result)
    {
        Intercept?.Invoke(name, request);
        Calls.Add(name);
        return Task.FromResult(result);
    }

    public Task<InsertResult> InsertAsync(InsertRequest r, CancellationToken ct) => Record($"insert:{r.Rows.Count}", r, new InsertResult(r.Rows.Count, []));
    public Task<FindResult> FindAsync(FindRequest r, int maxRows, CancellationToken ct) { Calls.Add("find"); return Task.FromResult(new FindResult([], false)); }
    public Task<AffectedResult> UpdateAsync(UpdateRequest r, CancellationToken ct) => Record("update", r, new AffectedResult(1));
    public Task<AffectedResult> UpsertAsync(UpsertRequest r, CancellationToken ct) => Record("upsert", r, new AffectedResult(1));
    public Task<AffectedResult> DeleteAsync(DeleteRequest r, CancellationToken ct) => Record("delete", r, new AffectedResult(1));
    public Task<CountResult> CountAsync(CountRequest r, CancellationToken ct) { Calls.Add("count"); return Task.FromResult(new CountResult(0)); }
}

public sealed class ClockedTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class BrokenJournal : IWriteJournal
{
    public IReadOnlyList<PendingWrite> Recover() => [];
    public void Append(PendingWrite write) => throw new IOException("disk full");
    public void Done(IReadOnlyCollection<string> ids) { }
    public void DeadLetter(PendingWrite write, string code, string message) { }
    public void Compact(IReadOnlyCollection<PendingWrite> pending) { }
}

public class WriteBehindTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "ldb-wb-" + Guid.NewGuid().ToString("N"));
    readonly RecordingProvider db = new();

    public WriteBehindTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    WriteBuffer Buffer(Action<WriteBehindOptions>? configure = null, IWriteJournal? journal = null, TimeProvider? time = null, LimitsOptions? limits = null)
    {
        var o = new WriteBehindOptions { Enabled = true, FlushIntervalSeconds = 0, FlushThreshold = 0, MaxRetries = 2 };
        configure?.Invoke(o);
        return new WriteBuffer(db, o, limits ?? new LimitsOptions(), journal ?? new MemoryWriteJournal(), time ?? TimeProvider.System, NullLogger<WriteBuffer>.Instance) { RetryBaseMs = 1 };
    }

    static string Insert(params string[] names) =>
        "{\"namespace\":\"demo\",\"table\":\"players\",\"rows\":[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\"}}")) + "]}";
    const string UpdateAlice = """{"namespace":"demo","table":"players","filter":{"field":"name","op":"eq","value":"alice"},"inc":{"score":5}}""";
    const string DeleteBob = """{"namespace":"demo","table":"players","filter":{"field":"name","op":"eq","value":"bob"}}""";
    const string UpsertCarol = """{"namespace":"demo","table":"players","row":{"name":"carol"},"keys":["name"]}""";

    [Fact]
    public void Applies_only_where_the_owner_asked_for_it()
    {
        Assert.False(Buffer(o => o.Enabled = false).Applies("insert", "demo"));
        var b = Buffer(o => { o.Operations = ["insert"]; o.Namespaces = ["demo"]; });
        Assert.True(b.Applies("insert", "demo"));
        Assert.False(b.Applies("delete", "demo"));
        Assert.False(b.Applies("insert", "other"));
        Assert.True(Buffer().Applies("delete", "anything"));
    }

    [Fact]
    public async Task Accepted_writes_are_acknowledged_without_touching_the_database()
    {
        var b = Buffer();
        var ack = await b.EnqueueAsync("insert", "demo", Insert("alice"), default);
        Assert.True(ack.Queued);
        Assert.False(string.IsNullOrEmpty(ack.Id));
        Assert.Equal(1, ack.Pending);
        Assert.DoesNotContain(db.Calls, c => c.StartsWith("insert"));
        Assert.Equal(1, b.Pending("demo"));
        Assert.Equal(0, b.Pending("other"));
    }

    [Theory]
    [InlineData("insert", """{"namespace":"demo","table":"ghosts","rows":[{"name":"a"}]}""", 404)]
    [InlineData("insert", """{"namespace":"demo","table":"players","rows":[{"name":"a","nope":1}]}""", 400)]
    [InlineData("insert", """{"namespace":"demo","table":"players","rows":[{"score":1}]}""", 400)]
    [InlineData("insert", """{"namespace":"demo","table":"players","rows":[{"name":5}]}""", 400)]
    [InlineData("insert", """{"namespace":"demo","table":"players","rows":[{"name":"this name is far too long"}]}""", 400)]
    [InlineData("insert", """{"namespace":"demo","table":"players","rows":[{"name":null}]}""", 400)]
    [InlineData("insert", """{"namespace":"demo","table":"players","rows":[]}""", 400)]
    [InlineData("update", """{"namespace":"demo","table":"players","set":{"score":1}}""", 400)]
    [InlineData("update", """{"namespace":"demo","table":"players","filter":{"field":"name","op":"eq","value":"a"},"set":{"id":9}}""", 400)]
    [InlineData("update", """{"namespace":"demo","table":"players","filter":{"field":"name","op":"eq","value":"a"},"inc":{"bio":1}}""", 400)]
    [InlineData("update", """{"namespace":"demo","table":"players","filter":{"field":"zzz","op":"eq","value":"a"},"set":{"score":1}}""", 400)]
    [InlineData("delete", """{"namespace":"demo","table":"players"}""", 400)]
    [InlineData("upsert", """{"namespace":"demo","table":"players","row":{"name":"a"},"keys":["score"]}""", 400)]
    public async Task Requests_that_can_never_succeed_are_refused_at_once(string operation, string payload, int status)
    {
        var b = Buffer();
        var ex = await Assert.ThrowsAsync<ApiException>(() => b.EnqueueAsync(operation, "demo", payload, default));
        Assert.Equal(status, ex.Status);
        Assert.Equal(0, b.Pending());
    }

    [Fact]
    public async Task A_namespace_without_schema_cannot_be_written_to()
    {
        db.Schema = null;
        var ex = await Assert.ThrowsAsync<ApiException>(() => Buffer().EnqueueAsync("insert", "demo", Insert("a"), default));
        Assert.Equal("namespace_unknown", ex.Code);
    }

    [Fact]
    public async Task Flush_applies_writes_in_order_and_merges_neighbouring_inserts()
    {
        var b = Buffer();
        await b.EnqueueAsync("insert", "demo", Insert("alice"), default);
        await b.EnqueueAsync("insert", "demo", Insert("bob", "carol"), default);
        await b.EnqueueAsync("update", "demo", UpdateAlice, default);
        await b.EnqueueAsync("insert", "demo", Insert("dave"), default);
        await b.EnqueueAsync("delete", "demo", DeleteBob, default);
        await b.EnqueueAsync("upsert", "demo", UpsertCarol, default);

        var r = await b.FlushAsync(null, default);
        Assert.Equal(new FlushResult(6, 0, 0, false), r);
        Assert.Equal(["insert:3", "update", "insert:1", "delete", "upsert"], db.Calls.Where(c => c != "find"));
        Assert.Equal(0, b.Pending());
    }

    [Fact]
    public async Task Merged_inserts_respect_the_row_limit()
    {
        var b = Buffer(limits: new LimitsOptions { MaxInsertRows = 3 });
        for (var i = 0; i < 5; i++) await b.EnqueueAsync("insert", "demo", Insert("a" + i, "b" + i), default);
        await b.FlushAsync(null, default);
        Assert.All(db.Calls, c => Assert.True(int.Parse(c.Split(':')[1]) <= 3));
        Assert.Equal(10, db.Calls.Sum(c => int.Parse(c.Split(':')[1])));
    }

    [Fact]
    public async Task Mongo_receipts_are_not_merged_because_bulk_inserts_can_partially_commit()
    {
        db.Name = "mongodb";
        var b = Buffer();
        await b.EnqueueAsync("insert", "demo", Insert("alice"), default);
        await b.EnqueueAsync("insert", "demo", Insert("bob"), default);
        await b.FlushAsync(null, default);
        Assert.Equal(["insert:1", "insert:1"], db.Calls);
    }

    [Fact]
    public async Task A_failed_drain_refuses_to_continue_and_keeps_older_writes()
    {
        var b = Buffer(o => o.OnError = WriteBehindErrorMode.Stop);
        await b.EnqueueAsync("insert", "demo", Insert("alice"), default);
        db.Intercept = (_, _) => throw new ApiException(409, "constraint_violation", "duplicate");
        var error = await Assert.ThrowsAsync<ApiException>(() => b.DrainAsync("demo", default));
        Assert.Equal("write_buffer_halted", error.Code);
        Assert.Equal(1, b.Pending("demo"));
    }

    [Fact]
    public async Task Schema_invalidation_rejects_writes_for_a_removed_table_immediately()
    {
        var b = Buffer();
        await b.EnqueueAsync("insert", "demo", Insert("alice"), default);
        await b.DrainAsync("demo", default);
        db.Schema = new SchemaDefinition([]);
        b.InvalidateSchema("demo");
        var error = await Assert.ThrowsAsync<ApiException>(() => b.EnqueueAsync("insert", "demo", Insert("bob"), default));
        Assert.Equal(404, error.Status);
        Assert.Equal(0, b.Pending());
    }

    [Fact]
    public async Task Namespace_requests_wait_for_an_in_progress_schema_change()
    {
        var b = Buffer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = b.RunNamespaceAsync("demo", async () => { entered.SetResult(); await release.Task; return new object(); }, default);
        await entered.Task;
        var ran = false;
        var second = b.RunNamespaceAsync("demo", () => { ran = true; return Task.FromResult<object>(new object()); }, default);
        Assert.False(ran);
        // Another namespace is independent.
        await b.RunNamespaceAsync("other", () => Task.FromResult<object>(new object()), default);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(ran);
    }

    [Fact]
    public async Task Flushing_one_namespace_leaves_the_others_queued()
    {
        var b = Buffer();
        await b.EnqueueAsync("insert", "demo", Insert("a"), default);
        await b.EnqueueAsync("insert", "other", Insert("b"), default);
        var r = await b.FlushAsync("demo", default);
        Assert.Equal(1, r.Flushed);
        Assert.Equal(1, r.Remaining);
        Assert.Equal(1, b.Pending("other"));
    }

    [Fact]
    public async Task A_write_the_database_rejects_for_good_is_dead_lettered_and_the_rest_continues()
    {
        var journal = new FileWriteJournal(Path.Combine(dir, "j.log"), fsync: false);
        db.Intercept = (name, req) => { if (req is UpdateRequest) throw new ApiException(409, "constraint_violation", "duplicate"); };
        var b = Buffer(journal: journal);
        await b.EnqueueAsync("insert", "demo", Insert("a"), default);
        await b.EnqueueAsync("update", "demo", UpdateAlice, default);
        await b.EnqueueAsync("delete", "demo", DeleteBob, default);

        var r = await b.FlushAsync(null, default);
        Assert.Equal(new FlushResult(2, 1, 0, false), r);
        Assert.Contains("delete", db.Calls);
        var dead = File.ReadAllLines(journal.DeadLetterPath);
        var line = Assert.Single(dead);
        Assert.Contains("constraint_violation", line);
        Assert.Contains("\"op\":\"update\"", line);
        journal.Dispose();
    }

    [Fact]
    public async Task OnError_stop_keeps_the_rejected_write_at_the_head()
    {
        db.Intercept = (name, req) => { if (req is UpdateRequest) throw new ApiException(409, "constraint_violation", "duplicate"); };
        var b = Buffer(o => o.OnError = WriteBehindErrorMode.Stop);
        await b.EnqueueAsync("update", "demo", UpdateAlice, default);
        await b.EnqueueAsync("delete", "demo", DeleteBob, default);
        var r = await b.FlushAsync(null, default);
        Assert.True(r.Halted);
        Assert.Equal(2, r.Remaining);
        Assert.DoesNotContain("delete", db.Calls);
    }

    [Fact]
    public async Task A_refused_merged_insert_is_split_so_only_the_bad_write_is_rejected()
    {
        db.Intercept = (name, req) =>
        {
            if (req is InsertRequest r && r.Rows.Any(x => x.GetProperty("name").GetString() == "bad")) throw new ApiException(409, "constraint_violation", "duplicate");
        };
        var b = Buffer();
        await b.EnqueueAsync("insert", "demo", Insert("a"), default);
        await b.EnqueueAsync("insert", "demo", Insert("bad"), default);
        await b.EnqueueAsync("insert", "demo", Insert("c"), default);
        var r = await b.FlushAsync(null, default);
        Assert.Equal(new FlushResult(2, 1, 0, false), r);
        Assert.Equal(["insert:1", "insert:1"], db.Calls.Where(c => c.StartsWith("insert")));
    }

    [Fact]
    public async Task Temporary_database_errors_are_retried()
    {
        var failures = 2;
        db.Intercept = (name, req) => { if (failures-- > 0) throw new ApiException(503, "database_error", "down"); };
        var b = Buffer();
        await b.EnqueueAsync("update", "demo", UpdateAlice, default);
        var r = await b.FlushAsync(null, default);
        Assert.Equal(1, r.Flushed);
        Assert.False(r.Halted);
    }

    [Fact]
    public async Task When_the_database_stays_down_the_flush_pauses_and_nothing_is_lost()
    {
        db.Intercept = (name, req) => throw new ApiException(503, "database_error", "down");
        var b = Buffer();
        await b.EnqueueAsync("update", "demo", UpdateAlice, default);
        await b.EnqueueAsync("delete", "demo", DeleteBob, default);
        var r = await b.FlushAsync(null, default);
        Assert.True(r.Halted);
        Assert.Equal(2, r.Remaining);
        db.Intercept = null;
        r = await b.FlushAsync(null, default);
        Assert.Equal(new FlushResult(2, 0, 0, false), r);
    }

    [Fact]
    public async Task The_buffer_has_hard_limits()
    {
        var b = Buffer(o => o.MaxQueuedOperations = 2);
        await b.EnqueueAsync("insert", "demo", Insert("a"), default);
        await b.EnqueueAsync("insert", "demo", Insert("b"), default);
        var ex = await Assert.ThrowsAsync<ApiException>(() => b.EnqueueAsync("insert", "demo", Insert("c"), default));
        Assert.Equal((503, "write_buffer_full"), (ex.Status, ex.Code));

        var small = Buffer(o => o.MaxQueuedBytes = 100);
        ex = await Assert.ThrowsAsync<ApiException>(() => small.EnqueueAsync("insert", "demo", Insert("a", "b", "c", "d", "e", "f"), default));
        Assert.Equal("write_buffer_full", ex.Code);
    }

    [Fact]
    public async Task A_write_that_cannot_be_made_durable_is_not_accepted()
    {
        var b = Buffer(journal: new BrokenJournal());
        var ex = await Assert.ThrowsAsync<ApiException>(() => b.EnqueueAsync("insert", "demo", Insert("a"), default));
        Assert.Equal("write_buffer_unavailable", ex.Code);
        Assert.Equal(0, b.Pending());
    }

    [Fact]
    public async Task Queued_writes_survive_a_crash_and_are_not_applied_twice()
    {
        var path = Path.Combine(dir, "journal.log");
        var first = new FileWriteJournal(path, fsync: true);
        var a = Buffer(journal: first);
        await a.EnqueueAsync("insert", "demo", Insert("alice"), default);
        await a.EnqueueAsync("update", "demo", UpdateAlice, default);
        await a.EnqueueAsync("delete", "demo", DeleteBob, default);
        first.Dispose(); // "crash": nothing was flushed

        var second = new FileWriteJournal(path, fsync: true);
        var b = Buffer(journal: second);
        b.Recover();
        Assert.Equal(3, b.Pending());
        var r = await b.FlushAsync(null, default);
        Assert.Equal(3, r.Flushed);
        second.Dispose();

        var third = new FileWriteJournal(path, fsync: true);
        var c = Buffer(journal: third);
        c.Recover();
        Assert.Equal(0, c.Pending());
        third.Dispose();
        Assert.Equal("", File.ReadAllText(path).Trim());
    }

    [Fact]
    public async Task A_torn_last_line_after_a_crash_is_ignored()
    {
        var path = Path.Combine(dir, "torn.log");
        var j = new FileWriteJournal(path, fsync: false);
        var a = Buffer(journal: j);
        await a.EnqueueAsync("insert", "demo", Insert("alice"), default);
        j.Dispose();
        File.AppendAllText(path, "{\"k\":\"w\",\"id\":\"broken\",\"op\":\"ins");
        var j2 = new FileWriteJournal(path, fsync: false);
        var b = Buffer(journal: j2);
        b.Recover();
        Assert.Equal(1, b.Pending());
        j2.Dispose();
    }

    [Fact]
    public async Task Reads_can_be_made_to_see_queued_writes()
    {
        var b = Buffer(o => o.FlushBeforeRead = true);
        Assert.True(b.FlushBeforeRead);
        await b.EnqueueAsync("insert", "demo", Insert("a"), default);
        await b.FlushAsync("demo", default);
        Assert.Equal(0, b.Pending("demo"));
        Assert.False(Buffer().FlushBeforeRead);
    }

    [Fact]
    public async Task A_disabled_buffer_flushes_nothing()
    {
        var r = await Buffer(o => o.Enabled = false).FlushAsync(null, default);
        Assert.False(r.Enabled);
    }
}

public class FlushScheduleTests
{
    static readonly WriteBehindOptions Interval = new() { Enabled = true, FlushIntervalSeconds = 30, FlushThreshold = 100 };
    static readonly DateTimeOffset T0 = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_waiting_means_nothing_to_do() =>
        Assert.False(FlushSchedule.Due(Interval, [], TimeZoneInfo.Utc, T0, T0, T0.AddHours(5), pending: 0));

    [Fact]
    public void Interval_threshold_and_quiet_periods()
    {
        Assert.False(FlushSchedule.Due(Interval, [], TimeZoneInfo.Utc, T0, T0, T0.AddSeconds(10), 5));
        Assert.True(FlushSchedule.Due(Interval, [], TimeZoneInfo.Utc, T0, T0, T0.AddSeconds(30), 5));
        Assert.True(FlushSchedule.Due(Interval, [], TimeZoneInfo.Utc, T0, T0, T0.AddSeconds(1), 100));
        var off = new WriteBehindOptions { FlushIntervalSeconds = 0, FlushThreshold = 0 };
        Assert.False(FlushSchedule.Due(off, [], TimeZoneInfo.Utc, T0, T0, T0.AddDays(2), 9999));
    }

    [Fact]
    public void Fixed_times_fire_once_when_crossed()
    {
        var o = new WriteBehindOptions { FlushIntervalSeconds = 0, FlushThreshold = 0 };
        var times = FlushSchedule.ParseTimes(["04:00", "16:30"]);
        var zone = TimeZoneInfo.Utc;
        var before = new DateTimeOffset(2026, 10, 16, 3, 59, 59, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 10, 16, 4, 0, 1, TimeSpan.Zero);
        Assert.False(FlushSchedule.Due(o, times, zone, before, before.AddSeconds(-1), before, 1));
        Assert.True(FlushSchedule.Due(o, times, zone, before, before, after, 1));
        // the next tick, after the time was already handled, must not fire again
        Assert.False(FlushSchedule.Due(o, times, zone, after, after, after.AddSeconds(1), 1));
        Assert.True(FlushSchedule.Due(o, times, zone, after, after, new DateTimeOffset(2026, 10, 16, 16, 30, 0, TimeSpan.Zero), 1));
    }

    [Fact]
    public void Fixed_times_follow_the_configured_time_zone_and_midnight()
    {
        var o = new WriteBehindOptions { FlushIntervalSeconds = 0, FlushThreshold = 0 };
        var plus2 = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var times = FlushSchedule.ParseTimes(["04:00"]);
        // 04:00 at +02:00 is 02:00 UTC
        var a = new DateTimeOffset(2026, 10, 16, 1, 59, 59, TimeSpan.Zero);
        Assert.False(FlushSchedule.Due(o, times, plus2, a, a.AddSeconds(-1), a, 1));
        Assert.True(FlushSchedule.Due(o, times, plus2, a, a, a.AddSeconds(2), 1));
        // a check window that spans local midnight still finds yesterday's 23:30
        var late = FlushSchedule.ParseTimes(["23:30"]);
        var start = new DateTimeOffset(2026, 10, 15, 21, 29, 0, TimeSpan.Zero);   // 23:29 local
        var end = new DateTimeOffset(2026, 10, 15, 21, 31, 0, TimeSpan.Zero);     // 23:31 local
        Assert.True(FlushSchedule.Due(o, late, plus2, start, start, end, 1));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("noon")]
    [InlineData("4")]
    public void Bad_times_stop_the_service_at_start_up(string value) => Assert.Throws<InvalidOperationException>(() => FlushSchedule.ParseTimes([value]));

    [Fact]
    public void Unknown_time_zones_stop_the_service_at_start_up() => Assert.Throws<InvalidOperationException>(() => FlushSchedule.Zone("Mars/Olympus"));
}

[Collection("api")]
public class WriteBehindPipelineTests
{
    const string InsertPayload = """{"namespace":"demo","table":"players","rows":[{"name":"alice"}]}""";

    static ApiHarness Harness(Action<Dictionary<string, string>>? more = null) => new(e =>
    {
        e["LDB_Ldb__WriteBehind__Enabled"] = "true";
        e["LDB_Ldb__WriteBehind__FlushIntervalSeconds"] = "0";
        e["LDB_Ldb__WriteBehind__FlushThreshold"] = "0";
        more?.Invoke(e);
    });

    [Fact]
    public async Task A_buffered_write_is_acknowledged_as_queued_and_nothing_reaches_the_database()
    {
        using var h = Harness();
        var res = await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        Assert.Equal(HttpStatusCode.OK, res.Status);
        using var doc = JsonDocument.Parse(res.Body!.Payload);
        Assert.True(doc.RootElement.GetProperty("queued").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
        Assert.DoesNotContain("insert", h.Provider.Calls);
    }

    [Fact]
    public async Task Flush_applies_the_queue_and_reports_it()
    {
        using var h = Harness();
        await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        var res = await h.SendAsync("/v1/data/flush", new { @namespace = "demo" });
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.Contains("\"flushed\":1", res.Body!.Payload);
        Assert.Contains("insert", h.Provider.Calls);
    }

    [Fact]
    public async Task A_caller_can_always_ask_for_an_immediate_write()
    {
        using var h = Harness();
        var res = await h.SendAsync("/v1/data/insert", new { @namespace = "demo", table = "players", rows = new[] { new { name = "alice" } }, durability = "immediate" });
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.Contains("insert", h.Provider.Calls);
        Assert.DoesNotContain("queued", res.Body!.Payload);
    }

    [Fact]
    public async Task Bad_writes_are_refused_before_they_are_queued()
    {
        using var h = Harness();
        var res = await h.SendAsync("/v1/data/insert", new { @namespace = "demo", table = "players", rows = new[] { new { nope = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, res.Status);
        var health = await h.SendAsync("/v1/health", new { });
        Assert.Contains("\"pending\":0", health.Body!.Payload);
    }

    [Fact]
    public async Task Unfiltered_writes_still_need_the_key_permission_when_buffered()
    {
        using var h = Harness();
        var res = await h.SendAsync("/v1/data/delete", new { @namespace = "demo", table = "players", all = true });
        Assert.Equal(HttpStatusCode.Forbidden, res.Status);
    }

    [Fact]
    public async Task Health_shows_the_queue()
    {
        using var h = Harness();
        await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        var health = await h.SendAsync("/v1/health", new { });
        Assert.Contains("\"writeBuffer\":{\"enabled\":true,\"pending\":1", health.Body!.Payload);
    }

    [Fact]
    public async Task Flush_is_an_operation_a_key_can_be_denied()
    {
        using var h = Harness(e => { e["LDB_Ldb__Security__Keys__0__AllowedOperations__0"] = "insert"; });
        var res = await h.SendAsync("/v1/data/flush", new { @namespace = "demo" });
        Assert.Equal(HttpStatusCode.Forbidden, res.Status);
    }

    [Fact]
    public async Task Without_write_behind_everything_stays_live_and_flush_is_a_harmless_no_op()
    {
        using var h = new ApiHarness();
        var res = await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.Contains("insert", h.Provider.Calls);
        var flush = await h.SendAsync("/v1/data/flush", new { @namespace = "demo" });
        Assert.Contains("\"enabled\":false", flush.Body!.Payload);
    }

    [Fact]
    public async Task Queued_writes_are_applied_when_the_namespace_gets_a_schema_change()
    {
        using var h = Harness();
        await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        var sync = await h.SendAsync("/v1/schema/sync", new { @namespace = "demo", schema = new { tables = new object[] { } } });
        Assert.Equal(HttpStatusCode.OK, sync.Status);
        Assert.Equal(["insert", "sync:demo"], h.Provider.Calls);
    }

    [Theory]
    [InlineData("/v1/data/insert")]
    [InlineData("/v1/data/find")]
    [InlineData("/v1/schema/sync")]
    public async Task A_halted_flush_blocks_immediate_writes_consistent_reads_and_schema_changes(string path)
    {
        using var h = Harness(e => {
            e["LDB_Ldb__WriteBehind__OnError"] = "Stop";
            e["LDB_Ldb__WriteBehind__FlushBeforeRead"] = "true";
        });
        await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        h.Provider.BeforeInsert = () => throw new ApiException(409, "constraint_violation", "duplicate");
        object payload = path == "/v1/schema/sync"
            ? new { @namespace = "demo", schema = new { tables = new object[] { } } }
            : new { @namespace = "demo", table = "players", rows = new[] { new { name = "bob" } }, durability = "immediate" };
        var result = await h.SendAsync(path, payload);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal("write_buffer_halted", result.Body!.Code);
        Assert.Empty(h.Provider.Calls);
    }

    [Fact]
    public async Task A_schema_sync_invalidates_the_validation_cache()
    {
        using var h = Harness();
        await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        await h.SendAsync("/v1/schema/sync", new { @namespace = "demo", schema = new { tables = new object[] { } } });
        var result = await h.SendAsync("/v1/data/insert", JsonSerializer.Deserialize<JsonElement>(InsertPayload));
        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        var health = await h.SendAsync("/v1/health", new { });
        Assert.Contains("\"pending\":0", health.Body!.Payload);
    }
}
