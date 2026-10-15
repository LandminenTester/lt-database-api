using System.Data.Common;
using Ldb.Core.Config;
using Ldb.Providers.Sql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ldb.Providers.Postgres;

public sealed class PostgresProvider(DatabaseOptions options, ILogger<PostgresProvider> logger)
    : SqlProviderBase(new PostgresDialect(), options, logger)
{
    public override string Name => "postgres";

    // PostgreSQL can roll DDL back, so a failed sync leaves nothing half-applied.
    protected override bool TransactionalDdl => true;

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    protected override string MetaTableSql =>
        "CREATE TABLE IF NOT EXISTS \"_ldb_schema\" (ns VARCHAR(24) NOT NULL PRIMARY KEY, hash VARCHAR(64) NOT NULL, definition TEXT NOT NULL, updated_at VARCHAR(40) NOT NULL)";

    protected override async Task LockAsync(DbConnection connection, DbTransaction? transaction, string ns, CancellationToken ct)
    {
        // Held until the transaction ends, so two syncs of one namespace never run at the same time.
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@ns))";
        var p = cmd.CreateParameter();
        p.ParameterName = "@ns";
        p.Value = "ldb:" + ns;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
