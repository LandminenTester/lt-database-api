using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;

namespace Ldb.Api.Buffering;

/// <summary>
/// The optional write-behind queue. Writes are validated against the stored schema, journaled, acknowledged as "queued" and
/// applied later, in order, by <see cref="FlushAsync"/>. Nothing here changes how reads, schema changes or the default (live)
/// mode work.
/// </summary>
public sealed class WriteBuffer
{
    static readonly Meter Meter = new("Ldb.Api");
    static readonly Counter<long> Queued = Meter.CreateCounter<long>("ldb_writebehind_queued_total");
    static readonly Counter<long> Applied = Meter.CreateCounter<long>("ldb_writebehind_applied_total");
    static readonly Counter<long> Dead = Meter.CreateCounter<long>("ldb_writebehind_dead_lettered_total");

    static readonly TimeSpan SchemaTtl = TimeSpan.FromSeconds(15);

    readonly IDatabaseProvider provider;
    readonly WriteBehindOptions options;
    readonly LimitsOptions limits;
    readonly IWriteJournal journal;
    readonly TimeProvider time;
    readonly ILogger<WriteBuffer> logger;
    readonly object gate = new();
    readonly List<PendingWrite> queue = [];
    readonly SemaphoreSlim flushLock = new(1, 1);
    readonly ConcurrentDictionary<string, (SchemaDefinition Schema, DateTimeOffset At)> schemas = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> namespaceLocks = new();
    readonly HashSet<string> operations;
    long queuedBytes;
    bool anyJournalWrites;

    /// <summary>Milliseconds to wait before the first retry of a temporary database error; doubles each time. Tests shorten it.</summary>
    public int RetryBaseMs { get; init; } = 200;

    public WriteBuffer(IDatabaseProvider provider, WriteBehindOptions options, LimitsOptions limits, IWriteJournal journal, TimeProvider time, ILogger<WriteBuffer> logger)
    {
        this.provider = provider; this.options = options; this.limits = limits; this.journal = journal; this.time = time; this.logger = logger;
        operations = options.Operations.Select(o => o.Trim().ToLowerInvariant()).ToHashSet();
        Meter.CreateObservableGauge("ldb_writebehind_pending", () => Pending());
    }

    public bool Enabled => options.Enabled;
    public bool FlushBeforeRead => options.Enabled && options.FlushBeforeRead;
    public WriteBehindOptions Options => options;

    // Keep a schema change, its drain, and concurrent requests of that namespace in one ordered section.
    public async Task<object> RunNamespaceAsync(string ns, Func<Task<object>> action, CancellationToken ct)
    {
        var mutex = namespaceLocks.GetOrAdd(ns, _ => new SemaphoreSlim(1, 1));
        await mutex.WaitAsync(ct);
        try { return await action(); }
        finally { mutex.Release(); }
    }

    public void InvalidateSchema(string ns) => schemas.TryRemove(ns, out _);

    public async Task DrainAsync(string ns, CancellationToken ct)
    {
        var result = await FlushAsync(ns, ct);
        if (result.Halted || Pending(ns) > 0)
            throw new ApiException(503, "write_buffer_halted", "Older writes could not be applied. Resolve the pending writes before continuing.");
    }
    public DateTimeOffset LastFlush { get; private set; }
    public bool Halted { get; private set; }

    public bool Applies(string operation, string ns) =>
        Enabled && operations.Contains(operation) && (options.Namespaces.Count == 0 || options.Namespaces.Contains(ns));

    /// <summary>Loads writes that a previous run accepted but did not finish. Call once before serving.</summary>
    public void Recover()
    {
        var pending = journal.Recover();
        lock (gate)
        {
            queue.Clear(); queuedBytes = 0;
            foreach (var w in pending) { queue.Add(w); queuedBytes += w.Bytes; }
        }
        if (pending.Count > 0) logger.LogWarning("Recovered {Count} queued write(s) from the journal", pending.Count);
        LastFlush = time.GetUtcNow();
    }

    public int Pending(string? ns = null)
    {
        lock (gate) return ns is null ? queue.Count : queue.Count(w => w.Namespace == ns);
    }

    public long OldestAgeMs()
    {
        lock (gate) return queue.Count == 0 ? 0 : Math.Max(0, time.GetUtcNow().ToUnixTimeMilliseconds() - queue[0].EnqueuedUnixMs);
    }

    // ---------------------------------------------------------------- accepting writes

    public async Task<QueuedAck> EnqueueAsync(string operation, string ns, string payload, CancellationToken ct)
    {
        var schema = await SchemaAsync(ns, ct);
        WriteValidator.Validate(operation, payload, schema, limits.MaxInsertRows);

        var bytes = Encoding.UTF8.GetByteCount(payload);
        PendingWrite write;
        lock (gate)
        {
            if (queue.Count >= options.MaxQueuedOperations || queuedBytes + bytes > options.MaxQueuedBytes)
                throw new ApiException(503, "write_buffer_full", "The write buffer is full. Retry later, or send the write with durability \"immediate\".");
            write = new PendingWrite(Guid.NewGuid().ToString("N"), operation, ns, payload, time.GetUtcNow().ToUnixTimeMilliseconds(), bytes);
            try { journal.Append(write); anyJournalWrites = true; }
            catch (IOException ex)
            {
                logger.LogError("Write journal failed: {Type}", ex.GetType().Name);
                throw new ApiException(503, "write_buffer_unavailable", "The write could not be made durable, so it was not accepted.");
            }
            queue.Add(write); queuedBytes += bytes;
        }
        Queued.Add(1);
        return new QueuedAck(true, write.Id, Pending());
    }

    async Task<SchemaDefinition> SchemaAsync(string ns, CancellationToken ct)
    {
        if (schemas.TryGetValue(ns, out var hit) && time.GetUtcNow() - hit.At < SchemaTtl) return hit.Schema;
        var d = await provider.DescribeAsync(ns, ct);
        var schema = d.Schema ?? throw new ApiException(404, "namespace_unknown", "This namespace has no schema yet. Run a schema sync first.");
        schemas[ns] = (schema, time.GetUtcNow());
        return schema;
    }

    // ---------------------------------------------------------------- applying writes

    /// <summary>Applies the waiting writes of one namespace (or all) in the order they were accepted.</summary>
    public async Task<FlushResult> FlushAsync(string? ns, CancellationToken ct)
    {
        if (!Enabled) return new FlushResult(0, 0, 0, false, Enabled: false);
        await flushLock.WaitAsync(ct);
        try
        {
            List<PendingWrite> work;
            lock (gate) work = queue.Where(w => ns is null || w.Namespace == ns).ToList();
            int flushed = 0, dead = 0, i = 0;
            Halted = false;

            while (i < work.Count && !ct.IsCancellationRequested)
            {
                var group = NextGroup(work, i);
                var outcome = await ApplyGroupAsync(group, ct);
                flushed += outcome.Applied; dead += outcome.Dead;
                i += group.Count;
                if (outcome.Halt) { Halted = true; break; }
            }

            LastFlush = time.GetUtcNow();
            int remaining;
            lock (gate)
            {
                remaining = queue.Count;
                if (queue.Count == 0 && anyJournalWrites) { journal.Compact([]); anyJournalWrites = false; }
            }
            if (flushed + dead > 0) logger.LogInformation("write-behind flush applied={Applied} rejected={Rejected} remaining={Remaining}", flushed, dead, remaining);
            return new FlushResult(flushed, dead, remaining, Halted);
        }
        finally { flushLock.Release(); }
    }

    /// <summary>Consecutive inserts into one table are merged into a single database call (up to the row limit).</summary>
    List<PendingWrite> NextGroup(List<PendingWrite> work, int start)
    {
        var first = work[start];
        var group = new List<PendingWrite> { first };
        // MongoDB ordered bulk inserts can partially commit before a duplicate-key error. Splitting and replaying such
        // a merged group would repeat successful writes. Keep separate receipts in separate provider calls.
        if (first.Operation != "insert" || provider.Name == "mongodb") return group;
        var head = ParseInsert(first);
        var rows = head.Rows.Count;
        for (var j = start + 1; j < work.Count; j++)
        {
            var next = work[j];
            if (next.Operation != "insert" || next.Namespace != first.Namespace) break;
            var r = ParseInsert(next);
            if (r.Table != head.Table || rows + r.Rows.Count > limits.MaxInsertRows) break;
            group.Add(next); rows += r.Rows.Count;
        }
        return group;
    }

    static InsertRequest ParseInsert(PendingWrite w) => Json.Parse<InsertRequest>(w.Payload);

    readonly record struct Outcome(int Applied, int Dead, bool Halt);

    async Task<Outcome> ApplyGroupAsync(List<PendingWrite> group, CancellationToken ct)
    {
        if (group.Count == 1) return await ApplyOneAsync(group[0], ct);

        // A merged insert is all-or-nothing in one database call. If the database refuses it for good, split it so that only
        // the offending write is rejected.
        var merged = ParseInsert(group[0]) with { Rows = [.. group.SelectMany(w => ParseInsert(w).Rows)] };
        try
        {
            await WithRetryAsync(() => provider.InsertAsync(merged, ct), ct);
            Confirm(group);
            return new Outcome(group.Count, 0, false);
        }
        catch (ApiException ex) when (!IsTransient(ex))
        {
            int applied = 0, dead = 0;
            foreach (var w in group)
            {
                var o = await ApplyOneAsync(w, ct);
                applied += o.Applied; dead += o.Dead;
                if (o.Halt) return new Outcome(applied, dead, true);
            }
            return new Outcome(applied, dead, false);
        }
        catch (ApiException) { return new Outcome(0, 0, true); }
    }

    async Task<Outcome> ApplyOneAsync(PendingWrite w, CancellationToken ct)
    {
        try
        {
            await WithRetryAsync(() => ApplyAsync(w, ct), ct);
            Confirm([w]);
            return new Outcome(1, 0, false);
        }
        catch (ApiException ex) when (IsTransient(ex))
        {
            logger.LogWarning("write-behind: database not available ({Code}), flush paused", ex.Code);
            return new Outcome(0, 0, true);
        }
        catch (ApiException ex)
        {
            if (options.OnError == WriteBehindErrorMode.Stop)
            {
                logger.LogError("write-behind: write {Id} was rejected ({Code}); stopping because OnError is Stop", w.Id, ex.Code);
                return new Outcome(0, 0, true);
            }
            journal.DeadLetter(w, ex.Code, ex.Message);
            Remove([w]);
            Dead.Add(1);
            logger.LogError("write-behind: write {Id} ({Operation}, namespace {Namespace}) was rejected for good: {Code}", w.Id, w.Operation, w.Namespace, ex.Code);
            return new Outcome(0, 1, false);
        }
    }

    Task ApplyAsync(PendingWrite w, CancellationToken ct) => w.Operation switch
    {
        "insert" => provider.InsertAsync(Json.Parse<InsertRequest>(w.Payload), ct),
        "update" => provider.UpdateAsync(Json.Parse<UpdateRequest>(w.Payload), ct),
        "upsert" => provider.UpsertAsync(Json.Parse<UpsertRequest>(w.Payload), ct),
        "delete" => provider.DeleteAsync(Json.Parse<DeleteRequest>(w.Payload), ct),
        _ => throw new ApiException(400, "invalid_request", "This operation cannot be buffered."),
    };

    async Task WithRetryAsync(Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await action(); return; }
            catch (ApiException ex) when (IsTransient(ex) && attempt < options.MaxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(5000, RetryBaseMs * Math.Pow(2, attempt)));
                await Task.Delay(delay, time, ct);
            }
        }
    }

    static bool IsTransient(ApiException ex) => ex.Status >= 500 || ex.Code is "conflict_retry" or "database_timeout" or "sync_in_progress";

    void Confirm(IReadOnlyCollection<PendingWrite> done)
    {
        journal.Done([.. done.Select(w => w.Id)]);
        Remove(done);
        Applied.Add(done.Count);
    }

    void Remove(IReadOnlyCollection<PendingWrite> items)
    {
        var ids = items.Select(w => w.Id).ToHashSet();
        lock (gate)
        {
            queue.RemoveAll(w => ids.Contains(w.Id));
            queuedBytes = queue.Sum(w => (long)w.Bytes);
        }
    }

    public object Stats() => new { enabled = Enabled, pending = Pending(), oldestAgeSeconds = OldestAgeMs() / 1000, halted = Halted };
}

/// <summary>Decides when the buffer is flushed on its own. Pure, so it can be tested with fixed times.</summary>
public static class FlushSchedule
{
    public static IReadOnlyList<TimeOnly> ParseTimes(IEnumerable<string> values)
    {
        var list = new List<TimeOnly>();
        foreach (var v in values)
        {
            if (!TimeOnly.TryParseExact(v.Trim(), ["HH:mm", "H:mm"], null, System.Globalization.DateTimeStyles.None, out var t))
                throw new InvalidOperationException($"WriteBehind:FlushAt entry '{v}' is not a time like \"04:00\".");
            list.Add(t);
        }
        return list;
    }

    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "UTC" : id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"WriteBehind:TimeZone '{id}' is not a known time zone id.");
        }
    }

    /// <param name="lastFlush">When the buffer last flushed.</param>
    /// <param name="lastCheck">When this method last ran, so a scheduled time is not missed or counted twice.</param>
    public static bool Due(WriteBehindOptions o, IReadOnlyList<TimeOnly> times, TimeZoneInfo zone, DateTimeOffset lastFlush, DateTimeOffset lastCheck, DateTimeOffset now, int pending)
    {
        if (pending == 0) return false;
        if (o.FlushThreshold > 0 && pending >= o.FlushThreshold) return true;
        if (o.FlushIntervalSeconds > 0 && now - lastFlush >= TimeSpan.FromSeconds(o.FlushIntervalSeconds)) return true;
        if (times.Count == 0) return false;

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        foreach (var dayOffset in new[] { 0, -1 })
        {
            var day = DateOnly.FromDateTime(localNow.DateTime).AddDays(dayOffset);
            foreach (var t in times)
            {
                var local = day.ToDateTime(t, DateTimeKind.Unspecified);
                var instant = new DateTimeOffset(local, zone.GetUtcOffset(local));
                if (instant > lastCheck && instant <= now) return true;
            }
        }
        return false;
    }
}
