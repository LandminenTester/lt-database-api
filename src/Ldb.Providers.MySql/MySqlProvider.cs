using System.Data.Common;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Providers.Sql;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Ldb.Providers.MySql;

public sealed class MySqlProvider(DatabaseOptions options, ILogger<MySqlProvider> logger)
    : SqlProviderBase(new MySqlDialect(), options, logger)
{
    public override string Name => "mysql";

    // MySQL commits DDL implicitly. A failed sync is therefore resumable instead: the next run repairs what is missing.
    protected override bool TransactionalDdl => false;

    protected override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    protected override string MetaTableSql =>
        "CREATE TABLE IF NOT EXISTS `_ldb_schema` (ns VARCHAR(24) NOT NULL, hash VARCHAR(64) NOT NULL, definition LONGTEXT NOT NULL, updated_at VARCHAR(40) NOT NULL, PRIMARY KEY (ns)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin";

    protected override long? LastInsertId(DbCommand command) => command is MySqlCommand m && m.LastInsertedId > 0 ? m.LastInsertedId : null;

    protected override async Task LockAsync(DbConnection connection, DbTransaction? transaction, string ns, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GET_LOCK(@name, 30)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = "ldb:" + ns;
        cmd.Parameters.Add(p);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0) != 1)
            throw new ApiException(409, "sync_in_progress", "Another schema sync of this namespace is running. Try again shortly.");
    }

    protected override async Task UnlockAsync(DbConnection connection, string ns)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT RELEASE_LOCK(@name)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = "ldb:" + ns;
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync();
    }
}
