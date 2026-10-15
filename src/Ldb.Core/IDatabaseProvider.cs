using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;

namespace Ldb.Core;

/// <summary>
/// One implementation per database engine. Requests arrive already authenticated and authorized;
/// providers still validate every table, column and value against the namespace's stored schema.
/// </summary>
public interface IDatabaseProvider
{
    string Name { get; }

    /// <summary>Creates the internal bookkeeping structures. Called once at start-up.</summary>
    Task InitializeAsync(CancellationToken ct);
    Task<bool> PingAsync(CancellationToken ct);

    Task<SyncResult> SyncAsync(string ns, SchemaDefinition desired, bool dryRun, DestructiveMode mode, CancellationToken ct);
    Task<DescribeResult> DescribeAsync(string ns, CancellationToken ct);

    Task<InsertResult> InsertAsync(InsertRequest request, CancellationToken ct);
    Task<FindResult> FindAsync(FindRequest request, int maxRows, CancellationToken ct);
    Task<AffectedResult> UpdateAsync(UpdateRequest request, CancellationToken ct);
    Task<AffectedResult> UpsertAsync(UpsertRequest request, CancellationToken ct);
    Task<AffectedResult> DeleteAsync(DeleteRequest request, CancellationToken ct);
    Task<CountResult> CountAsync(CountRequest request, CancellationToken ct);
}
