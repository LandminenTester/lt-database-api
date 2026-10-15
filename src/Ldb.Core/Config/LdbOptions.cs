namespace Ldb.Core.Config;

public enum DestructiveMode { Apply, Quarantine, Deny }

public sealed class LdbOptions
{
    public const string Section = "Ldb";
    public DatabaseOptions Database { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public SchemaOptions Schema { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
    public WriteBehindOptions WriteBehind { get; set; } = new();
}

public sealed class DatabaseOptions
{
    /// <summary>postgres, mysql or mongodb.</summary>
    public string Provider { get; set; } = "postgres";
    /// <summary>Connection string (or Mongo URL) for data access. Use a user that can only SELECT/INSERT/UPDATE/DELETE.</summary>
    public string DmlConnectionString { get; set; } = "";
    /// <summary>Connection used only by schema sync. Falls back to the data connection when empty.</summary>
    public string? DdlConnectionString { get; set; }
    public string MongoDatabase { get; set; } = "ldb";
    public int CommandTimeoutSeconds { get; set; } = 15;
}

public sealed class SecurityOptions
{
    /// <summary>Plain HTTP is only accepted from loopback unless this is set.</summary>
    public bool AllowInsecureHttp { get; set; }
    public int TimestampToleranceSeconds { get; set; } = 30;
    public long MaxBodyBytes { get; set; } = 1_048_576;
    /// <summary>Failed authentications per minute and client address before further attempts are refused.</summary>
    public int FailedAuthPerMinute { get; set; } = 10;
    public List<ApiKeyOptions> Keys { get; set; } = [];
    /// <summary>Addresses of reverse proxies whose X-Forwarded-For header is trusted.</summary>
    public List<string> TrustedProxies { get; set; } = [];
}

public sealed class ApiKeyOptions
{
    public string KeyId { get; set; } = "";
    /// <summary>Secret reference: env:NAME or file:/path. Plain secrets are not accepted.</summary>
    public string Current { get; set; } = "";
    /// <summary>Optional second secret, so a key can be rotated without downtime.</summary>
    public string? Next { get; set; }
    public List<string> AllowedNamespaces { get; set; } = [];
    /// <summary>Subset of: health, schema.sync, schema.describe, insert, find, update, upsert, delete, count. Empty means all.</summary>
    public List<string> AllowedOperations { get; set; } = [];
    /// <summary>Client address ranges (CIDR) this key may be used from. Empty means any address.</summary>
    public List<string> AllowedCidrs { get; set; } = [];
    public int RequestsPerMinute { get; set; } = 600;
    /// <summary>Allows update/delete without a filter ("all": true).</summary>
    public bool AllowUnfilteredWrites { get; set; }
}

public sealed class SchemaOptions
{
    /// <summary>Apply: removed columns and tables are dropped. Quarantine: they are renamed to _dropped_*. Deny: such syncs are refused.</summary>
    public DestructiveMode DestructiveMode { get; set; } = DestructiveMode.Apply;
    public int MaxTables { get; set; } = 64;
}

public sealed class LimitsOptions
{
    public int MaxRows { get; set; } = 1000;
    public int MaxInsertRows { get; set; } = 1000;
}

public enum WriteBehindErrorMode
{
    /// <summary>A write the database permanently rejects is moved to the dead-letter file and the queue carries on.</summary>
    Skip,
    /// <summary>The flush stops at the first permanently rejected write, which stays at the head of the queue.</summary>
    Stop,
}

/// <summary>
/// Optional write-behind mode. By default every write runs against the database at once (live). When enabled, insert, update,
/// upsert and delete are validated, journaled and answered with "queued", and applied to the database later: at an interval,
/// at fixed times of day, when enough writes are waiting, on request (POST /v1/data/flush) and on shutdown.
/// Schema changes and reads are never delayed.
/// </summary>
public sealed class WriteBehindOptions
{
    public bool Enabled { get; set; }
    /// <summary>Operations that are buffered. Any of: insert, update, upsert, delete.</summary>
    public List<string> Operations { get; set; } = ["insert", "update", "upsert", "delete"];
    /// <summary>Namespaces whose writes are buffered. Empty means all namespaces.</summary>
    public List<string> Namespaces { get; set; } = [];
    /// <summary>Flush every N seconds while writes are waiting. 0 disables interval flushing.</summary>
    public int FlushIntervalSeconds { get; set; } = 30;
    /// <summary>Flush at these times of day ("HH:mm", 24 h) in <see cref="TimeZone"/>, for example ["04:00", "16:00"].</summary>
    public List<string> FlushAt { get; set; } = [];
    /// <summary>Time zone id for <see cref="FlushAt"/> (IANA or Windows id).</summary>
    public string TimeZone { get; set; } = "UTC";
    /// <summary>Flush early once this many writes are waiting. 0 disables.</summary>
    public int FlushThreshold { get; set; } = 1000;
    public int MaxQueuedOperations { get; set; } = 50_000;
    public long MaxQueuedBytes { get; set; } = 64L * 1024 * 1024;
    /// <summary>File that keeps queued writes across restarts and crashes. Empty keeps them in memory only (lost on a crash).</summary>
    public string JournalPath { get; set; } = "";
    /// <summary>Force every journal entry to disk before the write is acknowledged.</summary>
    public bool JournalFsync { get; set; } = true;
    public WriteBehindErrorMode OnError { get; set; } = WriteBehindErrorMode.Skip;
    /// <summary>Retries (with back-off) for temporary database errors before a flush gives up and tries again later.</summary>
    public int MaxRetries { get; set; } = 5;
    /// <summary>Apply the namespace's waiting writes before a find or count, so reads see the caller's own writes.</summary>
    public bool FlushBeforeRead { get; set; }
    /// <summary>How long a graceful shutdown waits for the final flush.</summary>
    public int ShutdownFlushSeconds { get; set; } = 30;
}
