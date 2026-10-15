namespace Ldb.Core.Config;

public enum DestructiveMode { Apply, Quarantine, Deny }

public sealed class LdbOptions
{
    public const string Section = "Ldb";
    public DatabaseOptions Database { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public SchemaOptions Schema { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
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
