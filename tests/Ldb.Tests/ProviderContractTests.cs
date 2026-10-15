using System.Diagnostics;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;
using Ldb.Providers.Mongo;
using Ldb.Providers.MySql;
using Ldb.Providers.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MongoDb;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace Ldb.Tests;

/// <summary>Skips integration tests when no container runtime is reachable (CI always has one).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    static readonly Lazy<string?> Problem = new(() =>
    {
        if (Environment.GetEnvironmentVariable("LDB_SKIP_DOCKER") == "1") return "LDB_SKIP_DOCKER=1";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return "docker did not answer"; }
            return p.ExitCode == 0 ? null : "docker engine is not running";
        }
        catch (Exception) { return "docker is not installed"; }
    });

    public DockerFactAttribute() { if (Problem.Value is { } why) Skip = "Needs Docker: " + why; }
}

/// <summary>
/// The same scenarios run against every engine, so MySQL, PostgreSQL and MongoDB are held to one behaviour.
/// Each test uses its own namespace and therefore its own tables.
/// </summary>
public abstract class ProviderContract : IAsyncLifetime
{
    protected IDatabaseProvider Db = null!;
    protected abstract Task<IDatabaseProvider> StartAsync();
    protected virtual Task StopAsync() => Task.CompletedTask;
    /// <summary>MongoDB cannot check existing data when a column gets shorter.</summary>
    protected virtual bool ChecksNarrowing => true;

    public async Task InitializeAsync() { Db = await StartAsync(); await Db.InitializeAsync(default); }
    public Task DisposeAsync() => StopAsync();

    static string Ns() => "t" + Guid.NewGuid().ToString("N")[..16];
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static SchemaDefinition Schema(string json) => SchemaValidator.Normalize(Json.Parse<SchemaDefinition>(json));
    static JsonElement Out(object value) => JsonSerializer.SerializeToElement(value, Json.Options);

    const string PlayersV1 = """
    {"tables":[{"name":"players","primaryKey":["id"],"columns":[
      {"name":"id","type":"int64","autoIncrement":true},
      {"name":"name","type":"string","length":32,"unique":true},
      {"name":"score","type":"int32","default":0},
      {"name":"bio","type":"text","nullable":true},
      {"name":"banned","type":"bool","default":false}]}]}
    """;

    Task<SyncResult> Sync(string ns, string schema, bool dryRun = false, DestructiveMode mode = DestructiveMode.Apply) =>
        Db.SyncAsync(ns, Schema(schema), dryRun, mode, default);

    Task<InsertResult> Insert(string ns, string table, params string[] rows) =>
        Db.InsertAsync(new InsertRequest(ns, table, [.. rows.Select(J)]), default);

    async Task<List<JsonElement>> Rows(string ns, string table, string? filter = null, string? sort = null, int? limit = null, int? offset = null)
    {
        var r = await Db.FindAsync(new FindRequest(ns, table, filter is null ? null : J(filter), null, sort is null ? null : Json.Parse<List<SortSpec>>(sort), limit, offset), 1000, default);
        return [.. r.Rows.Select(Out)];
    }

    static async Task<ApiException> Fails(Task task) => await Assert.ThrowsAsync<ApiException>(() => task);

    [DockerFact]
    public async Task Sync_creates_tables_and_a_second_sync_changes_nothing()
    {
        var ns = Ns();
        var first = await Sync(ns, PlayersV1);
        Assert.Equal("applied", first.Status);
        Assert.Contains(first.Plan, o => o.Kind == SchemaOpKind.CreateTable);
        var second = await Sync(ns, PlayersV1);
        Assert.Equal("unchanged", second.Status);
        Assert.False(second.Changed);
        var described = await Db.DescribeAsync(ns, default);
        Assert.Equal(first.Hash, described.Hash);
        Assert.Single(described.Schema!.Tables);
    }

    [DockerFact]
    public async Task Create_read_update_delete_roundtrip()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        var inserted = await Insert(ns, "players", """{"name":"alice","score":10}""", """{"name":"bob","score":20,"bio":"hi"}""", """{"name":"carol","score":30,"banned":true}""");
        Assert.Equal(3, inserted.Inserted);
        Assert.Equal(3, inserted.Ids.Distinct().Count());

        var all = await Rows(ns, "players", sort: """[{"field":"score","dir":"desc"}]""");
        Assert.Equal(["carol", "bob", "alice"], all.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(0, all[2].GetProperty("bio").ValueKind == JsonValueKind.Null ? 0 : 1);
        Assert.True(all[0].GetProperty("banned").GetBoolean());
        Assert.False(all[1].GetProperty("banned").GetBoolean());

        Assert.Equal(2, (await Rows(ns, "players", """{"field":"score","op":"gte","value":20}""")).Count);
        Assert.Equal(2, (await Rows(ns, "players", """{"field":"name","op":"in","value":["alice","carol"]}""")).Count);
        Assert.Single(await Rows(ns, "players", """{"field":"name","op":"startsWith","value":"ca"}"""));
        Assert.Single(await Rows(ns, "players", """{"field":"name","op":"contains","value":"ob"}"""));
        Assert.Equal(2, (await Rows(ns, "players", """{"not":{"field":"name","op":"eq","value":"bob"}}""")).Count);
        Assert.Equal(2, (await Rows(ns, "players", """{"or":[{"field":"name","op":"eq","value":"alice"},{"and":[{"field":"score","op":"gt","value":15},{"field":"banned","op":"eq","value":true}]}]}""")).Count);
        Assert.Equal(2, (await Rows(ns, "players", """{"field":"bio","op":"isNull"}""")).Count);
        Assert.Equal(2, (await Rows(ns, "players", """{"field":"bio","op":"eq","value":null}""")).Count);

        var page = await Db.FindAsync(new FindRequest(ns, "players", null, null, Json.Parse<List<SortSpec>>("""[{"field":"score"}]"""), 2, 0), 1000, default);
        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.HasMore);
        var last = await Db.FindAsync(new FindRequest(ns, "players", null, null, Json.Parse<List<SortSpec>>("""[{"field":"score"}]"""), 2, 2), 1000, default);
        Assert.Single(last.Rows);
        Assert.False(last.HasMore);
        var projected = await Db.FindAsync(new FindRequest(ns, "players", J("""{"field":"name","op":"eq","value":"bob"}"""), ["name", "score"]), 1000, default);
        Assert.Equal(["name", "score"], projected.Rows.Single().Keys.OrderBy(k => k));

        Assert.Equal(3, (await Db.CountAsync(new CountRequest(ns, "players"), default)).Count);
        Assert.Equal(1, (await Db.CountAsync(new CountRequest(ns, "players", J("""{"field":"banned","op":"eq","value":true}""")), default)).Count);

        var updated = await Db.UpdateAsync(new UpdateRequest(ns, "players", J("""{"field":"name","op":"eq","value":"alice"}"""), J("""{"bio":"updated"}"""), J("""{"score":5}""")), default);
        Assert.Equal(1, updated.Affected);
        var alice = (await Rows(ns, "players", """{"field":"name","op":"eq","value":"alice"}""")).Single();
        Assert.Equal(15, alice.GetProperty("score").GetInt32());
        Assert.Equal("updated", alice.GetProperty("bio").GetString());

        Assert.Equal(1, (await Db.UpsertAsync(new UpsertRequest(ns, "players", J("""{"name":"dave","score":1}"""), ["name"]), default)).Affected);
        Assert.Equal(1, (await Db.UpsertAsync(new UpsertRequest(ns, "players", J("""{"name":"dave","score":99}"""), ["name"]), default)).Affected);
        var dave = await Rows(ns, "players", """{"field":"name","op":"eq","value":"dave"}""");
        Assert.Single(dave);
        Assert.Equal(99, dave[0].GetProperty("score").GetInt32());

        Assert.Equal(1, (await Db.DeleteAsync(new DeleteRequest(ns, "players", J("""{"field":"name","op":"eq","value":"bob"}""")), default)).Affected);
        Assert.Equal(3, (await Db.CountAsync(new CountRequest(ns, "players"), default)).Count);
    }

    [DockerFact]
    public async Task All_column_types_roundtrip()
    {
        var ns = Ns();
        await Sync(ns, """
        {"tables":[{"name":"kinds","primaryKey":["id"],"columns":[
          {"name":"id","type":"uuid"},
          {"name":"a","type":"int32"},{"name":"b","type":"int64"},{"name":"c","type":"decimal","precision":10,"scale":2},
          {"name":"d","type":"float64"},{"name":"e","type":"bool"},{"name":"f","type":"string","length":10},
          {"name":"g","type":"text"},{"name":"h","type":"json"},{"name":"i","type":"datetime"},
          {"name":"j","type":"date"},{"name":"k","type":"bytes","length":16},{"name":"l","type":"int32","nullable":true}]}]}
        """);
        var id = Guid.NewGuid().ToString();
        await Insert(ns, "kinds", $$"""
        {"id":"{{id}}","a":-7,"b":9007199254740993,"c":12.5,"d":1.5,"e":true,"f":"häppy","g":"long text","h":{"x":[1,2,{"y":null}],"s":"t"},
         "i":"2026-10-15T12:34:56.789Z","j":"2026-10-15","k":"AQID","l":null}
        """);
        var row = (await Rows(ns, "kinds")).Single();
        Assert.Equal(id, row.GetProperty("id").GetString());
        Assert.Equal(-7, row.GetProperty("a").GetInt32());
        Assert.Equal(9007199254740993L, row.GetProperty("b").GetInt64());
        Assert.Equal(12.5m, row.GetProperty("c").GetDecimal());
        Assert.Equal(1.5, row.GetProperty("d").GetDouble());
        Assert.True(row.GetProperty("e").GetBoolean());
        Assert.Equal("häppy", row.GetProperty("f").GetString());
        Assert.Equal("t", row.GetProperty("h").GetProperty("s").GetString());
        Assert.Equal(2, row.GetProperty("h").GetProperty("x")[1].GetInt32());
        Assert.Equal(new DateTime(2026, 10, 15, 12, 34, 56, 789, DateTimeKind.Utc), row.GetProperty("i").GetDateTime().ToUniversalTime());
        Assert.Equal("2026-10-15", row.GetProperty("j").GetString());
        Assert.Equal("AQID", row.GetProperty("k").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("l").ValueKind);
    }

    [DockerFact]
    public async Task Constraints_and_bad_input_are_reported_as_clean_errors()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        await Insert(ns, "players", """{"name":"alice"}""");
        Assert.Equal(409, (await Fails(Insert(ns, "players", """{"name":"alice"}"""))).Status);
        Assert.Equal(400, (await Fails(Insert(ns, "players", """{"score":1}"""))).Status);
        Assert.Equal(400, (await Fails(Insert(ns, "players", $$"""{"name":"{{new string('x', 33)}}"}"""))).Status);
        Assert.Equal(400, (await Fails(Insert(ns, "players", """{"name":"x","nope":1}"""))).Status);
        Assert.Equal(400, (await Fails(Insert(ns, "players", """{"name":5}"""))).Status);
        Assert.Equal(404, (await Fails(Insert(ns, "ghosts", """{"name":"x"}"""))).Status);
        Assert.Equal(404, (await Fails(Insert(Ns(), "players", """{"name":"x"}"""))).Status);
        Assert.Equal(400, (await Fails(Db.UpdateAsync(new UpdateRequest(ns, "players", null, J("""{"score":1}""")), default))).Status);
        Assert.Equal(400, (await Fails(Db.DeleteAsync(new DeleteRequest(ns, "players"), default))).Status);
        Assert.Equal(400, (await Fails(Db.UpdateAsync(new UpdateRequest(ns, "players", J("""{"field":"name","op":"eq","value":"alice"}"""), J("""{"id":5}""")), default))).Status);
        Assert.Equal(400, (await Fails(Db.UpsertAsync(new UpsertRequest(ns, "players", J("""{"name":"a","score":1}"""), ["score"]), default))).Status);
        Assert.Equal(1, (await Db.CountAsync(new CountRequest(ns, "players"), default)).Count);

        Assert.Equal(1, (await Db.UpdateAsync(new UpdateRequest(ns, "players", null, J("""{"score":7}"""), null, All: true), default)).Affected);
        Assert.Equal(1, (await Db.DeleteAsync(new DeleteRequest(ns, "players", null, All: true), default)).Affected);
    }

    [DockerFact]
    public async Task Hostile_values_are_stored_as_plain_text()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        string[] nasty = ["Robert'); DROP TABLE players;--", "\" OR \"1\"=\"1", "50%_off\\", "{\"$gt\":\"\"}", "a'b`c\"d", "äöü \U0001F600"];
        foreach (var (name, i) in nasty.Select((n, i) => (n, i)))
        {
            var row = JsonSerializer.Serialize(new { name, score = i });
            await Insert(ns, "players", row);
        }
        var stored = await Rows(ns, "players", sort: """[{"field":"score"}]""");
        Assert.Equal(nasty, stored.Select(r => r.GetProperty("name").GetString()));
        foreach (var n in nasty)
        {
            var filter = JsonSerializer.Serialize(new { field = "name", op = "eq", value = n });
            Assert.Single(await Rows(ns, "players", filter));
        }
        Assert.Single(await Rows(ns, "players", JsonSerializer.Serialize(new { field = "name", op = "contains", value = "50%_" })));
        Assert.Empty(await Rows(ns, "players", JsonSerializer.Serialize(new { field = "name", op = "contains", value = "zz%" })));
        Assert.Equal(nasty.Length, (await Db.CountAsync(new CountRequest(ns, "players"), default)).Count);
    }

    [DockerFact]
    public async Task Schema_changes_keep_existing_data()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        await Insert(ns, "players", """{"name":"alice","score":10,"bio":"keep me"}""", """{"name":"bob","score":20}""");

        var v2 = """
        {"tables":[{"name":"players","primaryKey":["id"],"columns":[
          {"name":"id","type":"int64","autoIncrement":true},
          {"name":"name","type":"string","length":64,"unique":true},
          {"name":"score","type":"int32","default":0},
          {"name":"about","type":"text","nullable":true,"renamedFrom":"bio"},
          {"name":"level","type":"int32","default":1},
          {"name":"tag","type":"string","length":8,"nullable":true}],
          "indexes":[{"name":"by_score","columns":["score"]}]}]}
        """;
        var plan = await Sync(ns, v2, dryRun: true);
        Assert.Equal("planned", plan.Status);
        Assert.Contains(plan.Plan, o => o.Kind == SchemaOpKind.RenameColumn);
        Assert.Contains(plan.Plan, o => o.Kind == SchemaOpKind.DropColumn && o.Column == "banned" && o.Destructive);
        Assert.Equal("players", (await Db.DescribeAsync(ns, default)).Schema!.Tables.Single().Name);
        Assert.Contains(await Rows(ns, "players"), r => r.TryGetProperty("banned", out _));

        var applied = await Sync(ns, v2);
        Assert.Equal("applied", applied.Status);
        Assert.Equal("unchanged", (await Sync(ns, v2)).Status);

        var rows = await Rows(ns, "players", sort: """[{"field":"score"}]""");
        Assert.Equal("keep me", rows[0].GetProperty("about").GetString());
        Assert.Equal(1, rows[0].GetProperty("level").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("level").GetInt32());
        Assert.False(rows[0].TryGetProperty("banned", out _));
        Assert.False(rows[0].TryGetProperty("bio", out _));
        await Insert(ns, "players", $$"""{"name":"{{new string('n', 60)}}"}""");
        await Insert(ns, "players", """{"name":"carol","level":4,"tag":"x"}""");
    }

    [DockerFact]
    public async Task Removed_tables_are_dropped_in_apply_mode()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        await Insert(ns, "players", """{"name":"alice"}""");
        var gone = await Sync(ns, """{"tables":[]}""");
        Assert.Contains(gone.Plan, o => o.Kind == SchemaOpKind.DropTable);
        Assert.Equal(404, (await Fails(Insert(ns, "players", """{"name":"alice"}"""))).Status);
        await Sync(ns, PlayersV1);
        Assert.Equal(0, (await Db.CountAsync(new CountRequest(ns, "players"), default)).Count);
    }

    [DockerFact]
    public async Task Deny_mode_refuses_destructive_changes_and_leaves_everything_alone()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        await Insert(ns, "players", """{"name":"alice","bio":"x"}""");
        var smaller = PlayersV1.Replace("""{"name":"bio","type":"text","nullable":true},""", "");
        Assert.Equal("denied", (await Sync(ns, smaller, dryRun: true, mode: DestructiveMode.Deny)).Status);
        Assert.Equal("destructive_denied", (await Fails(Sync(ns, smaller, mode: DestructiveMode.Deny))).Code);
        Assert.Equal("x", (await Rows(ns, "players")).Single().GetProperty("bio").GetString());
        // Safe changes still go through.
        var added = PlayersV1.Replace("""{"name":"banned","type":"bool","default":false}""", """{"name":"banned","type":"bool","default":false},{"name":"extra","type":"int32","default":3}""");
        Assert.Equal("applied", (await Sync(ns, added, mode: DestructiveMode.Deny)).Status);
    }

    [DockerFact]
    public async Task Quarantine_mode_parks_removed_columns_instead_of_deleting_them()
    {
        var ns = Ns();
        await Sync(ns, PlayersV1);
        await Insert(ns, "players", """{"name":"alice","score":4}""");
        var smaller = PlayersV1.Replace("""{"name":"score","type":"int32","default":0},""", "");
        var result = await Sync(ns, smaller, mode: DestructiveMode.Quarantine);
        var op = result.Plan.Single(o => o.Kind == SchemaOpKind.DropColumn);
        Assert.Contains("_dropped_", op.Note);
        var row = (await Rows(ns, "players")).Single();
        Assert.False(row.TryGetProperty("score", out _));
        // The parked column must not block new rows.
        await Insert(ns, "players", """{"name":"bob"}""");
        Assert.Equal(2, (await Db.CountAsync(new CountRequest(ns, "players"), default)).Count);
    }

    [DockerFact]
    public async Task Shortening_a_column_below_stored_data_is_refused()
    {
        if (!ChecksNarrowing) return;
        var ns = Ns();
        await Sync(ns, PlayersV1);
        await Insert(ns, "players", $$"""{"name":"{{new string('a', 30)}}"}""");
        var shorter = PlayersV1.Replace("\"length\":32", "\"length\":10");
        Assert.Equal("data_does_not_fit", (await Fails(Sync(ns, shorter))).Code);
        Assert.Equal(30, (await Rows(ns, "players")).Single().GetProperty("name").GetString()!.Length);
    }

    [DockerFact]
    public async Task Namespaces_do_not_see_each_other()
    {
        var a = Ns(); var b = Ns();
        await Sync(a, PlayersV1); await Sync(b, PlayersV1);
        await Insert(a, "players", """{"name":"only-in-a"}""");
        Assert.Equal(1, (await Db.CountAsync(new CountRequest(a, "players"), default)).Count);
        Assert.Equal(0, (await Db.CountAsync(new CountRequest(b, "players"), default)).Count);
        await Sync(b, """{"tables":[]}""");
        Assert.Equal(1, (await Db.CountAsync(new CountRequest(a, "players"), default)).Count);
    }

    [DockerFact]
    public async Task Concurrent_syncs_of_one_namespace_end_in_one_consistent_state()
    {
        var ns = Ns();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Sync(ns, PlayersV1)));
        Assert.All(results, r => Assert.Contains(r.Status, new[] { "applied", "unchanged" }));
        Assert.Equal(1, results.Count(r => r.Status == "applied"));
        await Insert(ns, "players", """{"name":"alice"}""");
    }

    [DockerFact]
    public async Task Composite_keys_and_unique_indexes_are_enforced()
    {
        var ns = Ns();
        await Sync(ns, """
        {"tables":[{"name":"scores","primaryKey":["player","season"],"columns":[
          {"name":"player","type":"string","length":20},{"name":"season","type":"int32"},{"name":"points","type":"int32","default":0}]}]}
        """);
        await Insert(ns, "scores", """{"player":"a","season":1,"points":5}""");
        Assert.Equal(409, (await Fails(Insert(ns, "scores", """{"player":"a","season":1}"""))).Status);
        await Insert(ns, "scores", """{"player":"a","season":2}""");
        await Db.UpsertAsync(new UpsertRequest(ns, "scores", J("""{"player":"a","season":1,"points":50}"""), ["player", "season"]), default);
        var rows = await Rows(ns, "scores", """{"field":"season","op":"eq","value":1}""");
        Assert.Equal(50, rows.Single().GetProperty("points").GetInt32());
        Assert.Equal(2, (await Db.CountAsync(new CountRequest(ns, "scores"), default)).Count);
    }
}

public sealed class PostgresContractTests : ProviderContract
{
    PostgreSqlContainer? container;
    protected override async Task<IDatabaseProvider> StartAsync()
    {
        container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        return new PostgresProvider(new DatabaseOptions { Provider = "postgres", DmlConnectionString = container.GetConnectionString() }, NullLogger<PostgresProvider>.Instance);
    }
    protected override async Task StopAsync() { if (container is not null) await container.DisposeAsync(); }
}

public sealed class MySqlContractTests : ProviderContract
{
    MySqlContainer? container;
    protected override async Task<IDatabaseProvider> StartAsync()
    {
        container = new MySqlBuilder("mysql:8.4").Build();
        await container.StartAsync();
        return new MySqlProvider(new DatabaseOptions { Provider = "mysql", DmlConnectionString = container.GetConnectionString() }, NullLogger<MySqlProvider>.Instance);
    }
    protected override async Task StopAsync() { if (container is not null) await container.DisposeAsync(); }
}

public sealed class MongoContractTests : ProviderContract
{
    MongoDbContainer? container;
    protected override bool ChecksNarrowing => false;
    protected override async Task<IDatabaseProvider> StartAsync()
    {
        container = new MongoDbBuilder("mongo:8.0").Build();
        await container.StartAsync();
        return new MongoProvider(new DatabaseOptions { Provider = "mongodb", DmlConnectionString = container.GetConnectionString(), MongoDatabase = "ldb_test" }, NullLogger<MongoProvider>.Instance);
    }
    protected override async Task StopAsync() { if (container is not null) await container.DisposeAsync(); }
}
