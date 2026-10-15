using System.Collections.Concurrent;

namespace Ldb.Api.Security;

/// <summary>Remembers nonces of valid requests until they can no longer be replayed (the timestamp window has passed).</summary>
public sealed class NonceCache(TimeSpan ttl, int maxEntries = 500_000)
{
    readonly ConcurrentDictionary<string, long> seen = new(StringComparer.Ordinal);
    long nextSweep;

    /// <summary>False if the nonce was used before, or if the cache is full (fail closed).</summary>
    public bool TryUse(string kid, string nonce, long nowTicks)
    {
        Sweep(nowTicks);
        if (seen.Count >= maxEntries) return false;
        return seen.TryAdd(kid + "|" + nonce, nowTicks + ttl.Ticks);
    }

    void Sweep(long now)
    {
        if (now < Interlocked.Read(ref nextSweep)) return;
        Interlocked.Exchange(ref nextSweep, now + TimeSpan.FromSeconds(10).Ticks);
        foreach (var (key, expires) in seen) if (expires < now) seen.TryRemove(key, out _);
    }
}

/// <summary>Counts events per key and minute.</summary>
public sealed class MinuteCounter
{
    readonly ConcurrentDictionary<string, (long Window, int Count)> counts = new(StringComparer.Ordinal);

    /// <summary>Records one event and returns how many happened in the current minute.</summary>
    public int Hit(string key, long unixSeconds)
    {
        var window = unixSeconds / 60;
        var next = counts.AddOrUpdate(key, (window, 1), (_, old) => old.Window == window ? (window, old.Count + 1) : (window, 1));
        if (counts.Count > 100_000) foreach (var (k, v) in counts) if (v.Window < window) counts.TryRemove(k, out _);
        return next.Count;
    }

    public int Peek(string key, long unixSeconds) =>
        counts.TryGetValue(key, out var v) && v.Window == unixSeconds / 60 ? v.Count : 0;
}

/// <summary>Results of requests that carried an idempotency key, so a retry after a timeout does not write twice.</summary>
public sealed class IdempotencyCache(TimeSpan ttl, int maxEntries = 50_000)
{
    public sealed record Entry(string PayloadHash, int Status, string Payload, long Expires);
    readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public Entry? Find(string key, long nowTicks) =>
        entries.TryGetValue(key, out var e) && e.Expires > nowTicks ? e : null;

    public void Store(string key, string payloadHash, int status, string payload, long nowTicks)
    {
        if (entries.Count >= maxEntries)
            foreach (var (k, v) in entries) if (v.Expires < nowTicks) entries.TryRemove(k, out _);
        if (entries.Count < maxEntries) entries[key] = new Entry(payloadHash, status, payload, nowTicks + ttl.Ticks);
    }
}
