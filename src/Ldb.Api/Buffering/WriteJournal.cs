using System.Text;
using Ldb.Core;

namespace Ldb.Api.Buffering;

/// <summary>A write that was accepted and is waiting to be applied to the database.</summary>
public sealed record PendingWrite(string Id, string Operation, string Namespace, string Payload, long EnqueuedUnixMs, int Bytes);

/// <summary>Answer to a buffered write. The id is only a receipt: generated ids and affected rows are not known yet.</summary>
public sealed record QueuedAck(bool Queued, string Id, int Pending);

public sealed record FlushResult(int Flushed, int DeadLettered, int Remaining, bool Halted, bool Enabled = true);

/// <summary>Keeps queued writes safe across restarts. Implementations must be thread safe.</summary>
public interface IWriteJournal
{
    /// <summary>Writes that were accepted but never confirmed as applied, oldest first. Called once at start-up.</summary>
    IReadOnlyList<PendingWrite> Recover();
    /// <summary>Must not return before the entry is durable (when the journal promises durability). Throws IOException if it cannot.</summary>
    void Append(PendingWrite write);
    void Done(IReadOnlyCollection<string> ids);
    void DeadLetter(PendingWrite write, string code, string message);
    /// <summary>Rewrites the journal so it only contains <paramref name="pending"/>.</summary>
    void Compact(IReadOnlyCollection<PendingWrite> pending);
}

public sealed class MemoryWriteJournal : IWriteJournal
{
    public IReadOnlyList<PendingWrite> Recover() => [];
    public void Append(PendingWrite write) { }
    public void Done(IReadOnlyCollection<string> ids) { }
    public void DeadLetter(PendingWrite write, string code, string message) { }
    public void Compact(IReadOnlyCollection<PendingWrite> pending) { }
}

/// <summary>
/// Append-only JSON lines: {"k":"w",...} a write, {"k":"d","id":...} confirmed as applied. Rejected writes go to "&lt;path&gt;.dead".
/// A write is only removed after the database confirmed it, so a crash can at worst apply the last write twice.
/// </summary>
public sealed class FileWriteJournal : IWriteJournal, IDisposable
{
    sealed record Line(string K, string? Id = null, string? Op = null, string? Ns = null, string? P = null, long? T = null, int? B = null);
    sealed record DeadLine(string Id, string Op, string Ns, string P, string Code, string Message, long T);

    readonly string path;
    readonly bool fsync;
    readonly object gate = new();
    FileStream? stream;

    public FileWriteJournal(string path, bool fsync)
    {
        this.path = Path.GetFullPath(path);
        this.fsync = fsync;
        var dir = Path.GetDirectoryName(this.path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string DeadLetterPath => path + ".dead";

    public IReadOnlyList<PendingWrite> Recover()
    {
        lock (gate)
        {
            var pending = new List<PendingWrite>();
            if (File.Exists(path))
            {
                foreach (var raw in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    Line? l;
                    try { l = Json.Parse<Line>(raw); } catch (ApiException) { continue; } // a torn last line after a crash
                    if (l is null) continue;
                    if (l.K == "w" && l.Id is not null && l.Op is not null && l.Ns is not null && l.P is not null)
                        pending.Add(new PendingWrite(l.Id, l.Op, l.Ns, l.P, l.T ?? 0, l.B ?? Encoding.UTF8.GetByteCount(l.P)));
                    else if (l.K == "d" && l.Id is not null) pending.RemoveAll(w => w.Id == l.Id);
                }
            }
            CompactLocked(pending);
            return pending;
        }
    }

    public void Append(PendingWrite w)
    {
        lock (gate)
        {
            WriteLine(new Line("w", w.Id, w.Operation, w.Namespace, w.Payload, w.EnqueuedUnixMs, w.Bytes));
        }
    }

    public void Done(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        lock (gate)
        {
            foreach (var id in ids) WriteLine(new Line("d", id), flush: false);
            Flush();
        }
    }

    public void DeadLetter(PendingWrite w, string code, string message)
    {
        lock (gate)
        {
            File.AppendAllText(DeadLetterPath, Json.Write(new DeadLine(w.Id, w.Operation, w.Namespace, w.Payload, code, message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) + "\n", Encoding.UTF8);
            WriteLine(new Line("d", w.Id));
        }
    }

    public void Compact(IReadOnlyCollection<PendingWrite> pending)
    {
        lock (gate) CompactLocked(pending);
    }

    void CompactLocked(IReadOnlyCollection<PendingWrite> pending)
    {
        stream?.Dispose(); stream = null;
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var w in pending)
            {
                var bytes = Encoding.UTF8.GetBytes(Json.Write(new Line("w", w.Id, w.Operation, w.Namespace, w.Payload, w.EnqueuedUnixMs, w.Bytes)) + "\n");
                fs.Write(bytes);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
        stream = OpenForAppend();
    }

    FileStream OpenForAppend() => new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    void WriteLine(Line line, bool flush = true)
    {
        stream ??= OpenForAppend();
        var bytes = Encoding.UTF8.GetBytes(Json.Write(line) + "\n");
        stream.Write(bytes);
        if (flush) Flush();
    }

    void Flush() => stream?.Flush(flushToDisk: fsync);

    public void Dispose() { lock (gate) { stream?.Dispose(); stream = null; } }
}
