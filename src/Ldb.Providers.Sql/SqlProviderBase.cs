using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;
using Microsoft.Extensions.Logging;

namespace Ldb.Providers.Sql;

/// <summary>
/// Everything the SQL engines have in common: schema sync, data access, error mapping. Engines only supply a dialect,
/// connections and a lock. Reads and writes use the data connection; structure changes use the (more privileged) DDL connection.
/// </summary>
public abstract class SqlProviderBase(SqlDialect dialect, DatabaseOptions options, ILogger logger) : IDatabaseProvider
{
    const string MetaTable = "_ldb_schema";
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    readonly SqlQueryBuilder queries = new(dialect);
    readonly ConcurrentDictionary<string, (SchemaDefinition? Schema, string? Hash, DateTime? Updated, DateTime Loaded)> cache = new();

    public abstract string Name { get; }
    protected SqlDialect Dialect => dialect;
    protected abstract DbConnection CreateConnection(string connectionString);
    protected abstract string MetaTableSql { get; }
    protected virtual bool TransactionalDdl => false;
    protected abstract Task LockAsync(DbConnection connection, DbTransaction? transaction, string ns, CancellationToken ct);
    protected virtual Task UnlockAsync(DbConnection connection, string ns) => Task.CompletedTask;
    protected virtual long? LastInsertId(DbCommand command) => null;

    string DmlConnectionString => options.DmlConnectionString;
    string DdlConnectionString => string.IsNullOrWhiteSpace(options.DdlConnectionString) ? options.DmlConnectionString : options.DdlConnectionString;

    // ---------------------------------------------------------------- infrastructure

    async Task<DbConnection> OpenAsync(bool ddl, CancellationToken ct)
    {
        var c = CreateConnection(ddl ? DdlConnectionString : DmlConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    DbCommand Command(DbConnection c, string sql, SqlParams? ps = null, DbTransaction? tx = null)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps?.Items ?? [])
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    async Task<int> ExecAsync(DbConnection c, DbTransaction? tx, string sql, CancellationToken ct, SqlParams? ps = null)
    {
        await using var cmd = Command(c, sql, ps, tx);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    protected async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (DbException ex)
        {
            // Messages of driver exceptions can contain row values, so only the type and SQLSTATE are logged.
            logger.LogError("Database error {Type} sqlstate={State}", ex.GetType().Name, ex.SqlState);
            throw Map(ex);
        }
    }

    static ApiException Map(DbException ex)
    {
        var state = ex.SqlState ?? "";
        if (state.StartsWith("23", StringComparison.Ordinal)) return new ApiException(409, "constraint_violation", "A constraint rejected the operation (duplicate value, missing value or broken reference).");
        if (state.StartsWith("22", StringComparison.Ordinal)) return new ApiException(400, "invalid_value", "The database rejected a value.");
        if (state is "40001" or "40P01") return new ApiException(409, "conflict_retry", "The operation conflicted with another one. Try again.");
        if (ex.InnerException is TimeoutException) return new ApiException(504, "database_timeout", "The database did not answer in time.");
        return new ApiException(500, "database_error", "The database could not complete the operation.");
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(true, ct);
        await ExecAsync(c, null, MetaTableSql, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var c = await OpenAsync(false, ct);
            await using var cmd = Command(c, "SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning("Database ping failed: {Type}", ex.GetType().Name);
            return false;
        }
    }

    // ---------------------------------------------------------------- stored schema

    async Task<(SchemaDefinition? Schema, string? Hash, DateTime? Updated)> LoadStoredAsync(DbConnection c, DbTransaction? tx, string ns, CancellationToken ct)
    {
        var ps = new SqlParams();
        var sql = $"SELECT hash, definition, updated_at FROM {dialect.Quote(MetaTable)} WHERE ns = {ps.Add(ns)}";
        await using var cmd = Command(c, sql, ps, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null, null);
        var schema = Json.Parse<SchemaDefinition>(r.GetString(1));
        DateTime.TryParse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind, out var updated);
        return (schema, r.GetString(0), updated);
    }

    async Task SaveStoredAsync(DbConnection c, DbTransaction? tx, string ns, string hash, SchemaDefinition schema, CancellationToken ct)
    {
        var ps = new SqlParams();
        var values = new[] { ps.Add(ns), ps.Add(hash), ps.Add(Json.Write(SchemaHasher.Canonicalize(schema))), ps.Add(DateTime.UtcNow.ToString("O")) };
        var cols = new[] { "ns", "hash", "definition", "updated_at" };
        var sql = dialect.UpsertSql(MetaTable, cols, values, ["ns"], ["hash", "definition", "updated_at"]);
        await ExecAsync(c, tx, sql, ct, ps);
    }

    async Task<SchemaDefinition> GetSchemaAsync(string ns, CancellationToken ct)
    {
        if (cache.TryGetValue(ns, out var hit) && DateTime.UtcNow - hit.Loaded < CacheTtl && hit.Schema is not null) return hit.Schema;
        await using var c = await OpenAsync(false, ct);
        var stored = await LoadStoredAsync(c, null, ns, ct);
        if (stored.Schema is null) throw new ApiException(404, "namespace_unknown", "This namespace has no schema yet. Run a schema sync first.");
        cache[ns] = (stored.Schema, stored.Hash, stored.Updated, DateTime.UtcNow);
        return stored.Schema;
    }

    static TableDefinition GetTable(SchemaDefinition schema, string table) =>
        schema.Tables.FirstOrDefault(t => t.Name == table) ?? throw new ApiException(404, "unknown_table", $"The namespace has no table '{table}'.");

    public async Task<DescribeResult> DescribeAsync(string ns, CancellationToken ct) => await Guard(async () =>
    {
        ns = Names.Namespace(ns);
        await using var c = await OpenAsync(false, ct);
        var stored = await LoadStoredAsync(c, null, ns, ct);
        return new DescribeResult(ns, stored.Hash, stored.Schema, stored.Updated);
    });

    // ---------------------------------------------------------------- schema sync

    public async Task<SyncResult> SyncAsync(string ns, SchemaDefinition desired, bool dryRun, DestructiveMode mode, CancellationToken ct) => await Guard(async () =>
    {
        ns = Names.Namespace(ns);
        var hash = SchemaHasher.Hash(desired);
        await using var conn = await OpenAsync(true, ct);
        await using var tx = TransactionalDdl && !dryRun ? await conn.BeginTransactionAsync(ct) : null;
        var locked = false;
        try
        {
            if (!dryRun) { await LockAsync(conn, tx, ns, ct); locked = true; }
            var stored = await LoadStoredAsync(conn, tx, ns, ct);
            var ops = SchemaDiffer.Diff(stored.Schema, desired);
            await AddRepairOpsAsync(conn, tx, ns, desired, ops, ct);
            ops = [.. ops.OrderBy(o => (int)o.Kind)];

            if (ops.Count == 0 && stored.Hash == hash) return new SyncResult(ns, hash, false, dryRun, "unchanged", []);

            var destructive = ops.Where(o => o.Destructive).ToList();
            if (mode == DestructiveMode.Deny && destructive.Count > 0)
            {
                if (dryRun) return new SyncResult(ns, hash, true, true, "denied", ops);
                throw new ApiException(409, "destructive_denied", $"The change contains {destructive.Count} destructive operation(s) and this server denies them. Run a dry run to see the plan.");
            }
            if (dryRun) return new SyncResult(ns, hash, true, true, "planned", ops);

            foreach (var op in ops) await ApplyAsync(conn, tx, ns, op, mode, ct);
            await SaveStoredAsync(conn, tx, ns, hash, desired, ct);
            if (tx is not null) await tx.CommitAsync(ct);
            cache.TryRemove(ns, out _);
            return new SyncResult(ns, hash, true, false, "applied", ops);
        }
        finally
        {
            if (locked) await UnlockAsync(conn, ns);
        }
    });

    /// <summary>Compares the stored plan with reality: objects that should exist but were removed or never created are added to the plan.</summary>
    async Task AddRepairOpsAsync(DbConnection c, DbTransaction? tx, string ns, SchemaDefinition desired, List<SchemaOp> ops, CancellationToken ct)
    {
        foreach (var t in desired.Tables)
        {
            if (ops.Any(o => (o.Kind == SchemaOpKind.CreateTable && o.Table == t.Name) || (o.Kind == SchemaOpKind.RenameTable && o.NewName == t.Name))) continue;
            var phys = Names.Physical(ns, t.Name);
            if (!await ExistsAsync(c, tx, dialect.TableExistsSql, phys, ct))
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateTable, Table = t.Name, TableDef = t, Note = "repair: table is missing" });
                foreach (var ix in t.Indexes ?? []) ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = t.Name, Index = ix.Name, IndexDef = ix, Note = "repair" });
                continue;
            }
            var columns = await NamesAsync(c, tx, dialect.ColumnNamesSql, phys, ct);
            foreach (var col in t.Columns.Where(x => !columns.Contains(x.Name)))
            {
                if (ops.Any(o => (o.Kind == SchemaOpKind.AddColumn && o.Column == col.Name && o.Table == t.Name) || (o.Kind == SchemaOpKind.RenameColumn && o.NewName == col.Name && o.Table == t.Name))) continue;
                ops.Add(new SchemaOp { Kind = SchemaOpKind.AddColumn, Table = t.Name, Column = col.Name, ColumnDef = col, Note = "repair: column is missing" });
            }
            var indexes = await NamesAsync(c, tx, dialect.IndexNamesSql, phys, ct);
            foreach (var ix in (t.Indexes ?? []).Where(x => !indexes.Contains(SqlDialect.IndexName(phys, x.Name))))
            {
                if (ops.Any(o => o.Kind == SchemaOpKind.CreateIndex && o.Index == ix.Name && o.Table == t.Name)) continue;
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = t.Name, Index = ix.Name, IndexDef = ix, Note = "repair: index is missing" });
            }
        }
    }

    async Task<bool> ExistsAsync(DbConnection c, DbTransaction? tx, string sql, string table, CancellationToken ct)
    {
        var ps = new SqlParams();
        ps.Add(table);
        await using var cmd = Command(c, sql, ps, tx);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    async Task<HashSet<string>> NamesAsync(DbConnection c, DbTransaction? tx, string sql, string table, CancellationToken ct)
    {
        var ps = new SqlParams();
        ps.Add(table);
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = Command(c, sql, ps, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    async Task ApplyAsync(DbConnection c, DbTransaction? tx, string ns, SchemaOp op, DestructiveMode mode, CancellationToken ct)
    {
        var phys = Names.Physical(ns, op.Table);
        var q = dialect.Quote;
        switch (op.Kind)
        {
            case SchemaOpKind.RenameTable:
                var to = Names.Physical(ns, op.NewName!);
                if (!await ExistsAsync(c, tx, dialect.TableExistsSql, phys, ct) && await ExistsAsync(c, tx, dialect.TableExistsSql, to, ct)) break;
                await ExecAsync(c, tx, dialect.RenameTableSql(phys, to), ct);
                break;
            case SchemaOpKind.RenameColumn:
                var cols = await NamesAsync(c, tx, dialect.ColumnNamesSql, phys, ct);
                if (!cols.Contains(op.Column!) && cols.Contains(op.NewName!)) break;
                await ExecAsync(c, tx, dialect.RenameColumnSql(phys, op.Column!, op.NewName!), ct);
                break;
            case SchemaOpKind.DropIndex:
                var ixName = SqlDialect.IndexName(phys, op.Index!);
                if ((await NamesAsync(c, tx, dialect.IndexNamesSql, phys, ct)).Contains(ixName)) await ExecAsync(c, tx, dialect.DropIndexSql(phys, ixName), ct);
                break;
            case SchemaOpKind.AddColumn:
                if ((await NamesAsync(c, tx, dialect.ColumnNamesSql, phys, ct)).Contains(op.Column!)) break;
                foreach (var sql in dialect.AddColumnSql(phys, op.ColumnDef!)) await ExecAsync(c, tx, sql, ct);
                break;
            case SchemaOpKind.AlterColumn:
                await AlterAsync(c, tx, phys, op, ct);
                break;
            case SchemaOpKind.CreateTable:
                if (await ExistsAsync(c, tx, dialect.TableExistsSql, phys, ct)) break;
                await ExecAsync(c, tx, dialect.CreateTableSql(phys, op.TableDef!), ct);
                break;
            case SchemaOpKind.CreateIndex:
                var newIx = SqlDialect.IndexName(phys, op.Index!);
                if ((await NamesAsync(c, tx, dialect.IndexNamesSql, phys, ct)).Contains(newIx)) break;
                await ExecAsync(c, tx, dialect.CreateIndexSql(phys, newIx, op.IndexDef!), ct);
                break;
            case SchemaOpKind.DropColumn:
                if (!(await NamesAsync(c, tx, dialect.ColumnNamesSql, phys, ct)).Contains(op.Column!)) break;
                if (mode == DestructiveMode.Quarantine)
                {
                    var parked = Park(op.Column!, 63);
                    await ExecAsync(c, tx, dialect.RenameColumnSql(phys, op.Column!, parked), ct);
                    if (op.OldColumnDef is { Nullable: false } old) await ExecAsync(c, tx, dialect.RelaxNullabilitySql(phys, old with { Name = parked }), ct);
                    op.Note = $"quarantined as {parked}";
                }
                else await ExecAsync(c, tx, $"ALTER TABLE {q(phys)} DROP COLUMN {q(op.Column!)}", ct);
                break;
            case SchemaOpKind.DropTable:
                if (mode == DestructiveMode.Quarantine)
                {
                    if (!await ExistsAsync(c, tx, dialect.TableExistsSql, phys, ct)) break;
                    var parked = Park(phys, 63);
                    await ExecAsync(c, tx, dialect.RenameTableSql(phys, parked), ct);
                    op.Note = $"quarantined as {parked}";
                }
                else await ExecAsync(c, tx, dialect.DropTableSql(phys), ct);
                break;
        }
    }

    static string Park(string name, int max)
    {
        var prefix = $"_dropped_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_";
        var full = prefix + name;
        return full.Length <= max ? full : full[..max];
    }

    async Task AlterAsync(DbConnection c, DbTransaction? tx, string phys, SchemaOp op, CancellationToken ct)
    {
        var old = op.OldColumnDef!; var nw = op.ColumnDef!;
        if (old.AutoIncrement != nw.AutoIncrement)
            throw new ApiException(409, "unsupported_change", $"Column '{op.Table}.{nw.Name}': switching auto-increment on or off is not supported.");
        if (old.Type == ColumnTypes.String && nw.Type == ColumnTypes.String && nw.Length < old.Length)
        {
            var ps = new SqlParams();
            await using var cmd = Command(c, $"SELECT COUNT(*) FROM {dialect.Quote(phys)} WHERE CHAR_LENGTH({dialect.Quote(nw.Name)}) > {ps.Add(nw.Length!.Value)}", ps, tx);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0)
                throw new ApiException(409, "data_does_not_fit", $"Column '{op.Table}.{nw.Name}' holds values longer than {nw.Length}. Shorten them first.");
        }
        foreach (var sql in dialect.AlterColumnSql(phys, old, nw)) await ExecAsync(c, tx, sql, ct);
    }

    // ---------------------------------------------------------------- data

    async Task<(TableDefinition Table, string Physical)> ResolveAsync(string ns, string table, CancellationToken ct)
    {
        ns = Names.Namespace(ns); table = Names.Table(table);
        var schema = await GetSchemaAsync(ns, ct);
        return (GetTable(schema, table), Names.Physical(ns, table));
    }

    public async Task<InsertResult> InsertAsync(InsertRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        if (r.Rows is null || r.Rows.Count == 0) throw new ApiException(400, "invalid_request", "Provide at least one row.");
        await using var c = await OpenAsync(false, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var ids = new List<object?>();
        foreach (var row in r.Rows) ids.Add(await InsertRowAsync(c, tx, t, phys, row, ct));
        await tx.CommitAsync(ct);
        return new InsertResult(r.Rows.Count, ids);
    });

    async Task<object?> InsertRowAsync(DbConnection c, DbTransaction tx, TableDefinition t, string phys, JsonElement row, CancellationToken ct)
    {
        var (cols, values) = RowValues(t, row, requireAll: true);
        var ps = new SqlParams();
        var exprs = cols.Select(col => dialect.ParamExpr(col, ps.Add(values[col.Name]))).ToList();
        var single = t.PrimaryKey is { Count: 1 } pk ? t.Columns.First(x => x.Name == pk[0]) : null;
        var sql = cols.Count == 0
            ? dialect.EmptyInsertSql(phys)
            : $"INSERT INTO {dialect.Quote(phys)} ({string.Join(", ", cols.Select(x => dialect.Quote(x.Name)))}) VALUES ({string.Join(", ", exprs)})";
        var returning = single is null ? "" : dialect.ReturningSql(single.Name);
        await using var cmd = Command(c, sql + returning, ps, tx);
        if (returning.Length > 0) return SqlValues.ToJson(single!, await cmd.ExecuteScalarAsync(ct));
        await cmd.ExecuteNonQueryAsync(ct);
        if (single is null) return null;
        if (values.TryGetValue(single.Name, out var given)) return SqlValues.ToJson(single, given);
        return LastInsertId(cmd);
    }

    (List<ColumnDefinition> Columns, Dictionary<string, object?> Values) RowValues(TableDefinition t, JsonElement row, bool requireAll)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new ApiException(400, "invalid_request", "Each row must be an object.");
        var cols = new List<ColumnDefinition>();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in row.EnumerateObject())
        {
            var col = SqlQueryBuilder.Column(t, prop.Name);
            if (values.ContainsKey(col.Name)) throw new ApiException(400, "invalid_request", $"Column '{col.Name}' appears twice.");
            cols.Add(col);
            values[col.Name] = SqlValues.FromJson(col, prop.Value, dialect);
        }
        if (requireAll)
            foreach (var col in t.Columns.Where(x => !x.Nullable && x.Default is null && !x.AutoIncrement && !values.ContainsKey(x.Name)))
                throw new ApiException(400, "missing_value", $"Column '{col.Name}' needs a value.");
        return (cols, values);
    }

    public async Task<FindResult> FindAsync(FindRequest r, int maxRows, CancellationToken ct) => await Guard(async () =>
    {
        var (t, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        var fields = r.Fields is { Count: > 0 } ? r.Fields.Distinct().Select(f => SqlQueryBuilder.Column(t, f)).ToList() : t.Columns;
        var limit = Math.Clamp(r.Limit ?? maxRows, 1, maxRows);
        var offset = r.Offset ?? 0;
        if (offset is < 0 or > 1_000_000) throw new ApiException(400, "invalid_request", "Offset is out of range.");
        var ps = new SqlParams();
        var where = queries.Where(FilterParser.Parse(r.Filter), t, ps);
        var sql = new StringBuilder($"SELECT {string.Join(", ", fields.Select(f => dialect.Quote(f.Name)))} FROM {dialect.Quote(phys)}");
        if (where is not null) sql.Append(" WHERE ").Append(where);
        sql.Append(queries.OrderBy(t, r.Sort)).Append($" LIMIT {limit + 1} OFFSET {offset}");
        await using var c = await OpenAsync(false, ct);
        await using var cmd = Command(c, sql.ToString(), ps);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(fields.Count);
            for (var i = 0; i < fields.Count; i++) row[fields[i].Name] = SqlValues.ToJson(fields[i], reader.GetValue(i));
            rows.Add(row);
        }
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new FindResult(rows, more);
    });

    public async Task<AffectedResult> UpdateAsync(UpdateRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        var filter = FilterParser.Parse(r.Filter);
        if (filter is null && !r.All) throw new ApiException(400, "filter_required", "An update needs a filter, or all=true where the key allows it.");
        var ps = new SqlParams();
        var sets = new List<string>();
        if (r.Set is { ValueKind: JsonValueKind.Object } set)
            foreach (var prop in set.EnumerateObject())
            {
                var col = WritableColumn(t, prop.Name);
                sets.Add($"{dialect.Quote(col.Name)} = {dialect.ParamExpr(col, ps.Add(SqlValues.FromJson(col, prop.Value, dialect)))}");
            }
        if (r.Inc is { ValueKind: JsonValueKind.Object } inc)
            foreach (var prop in inc.EnumerateObject())
            {
                var col = WritableColumn(t, prop.Name);
                if (col.Type is not (ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64))
                    throw new ApiException(400, "invalid_request", $"Column '{col.Name}' is not numeric, so it cannot be incremented.");
                var v = SqlValues.FromJson(col, prop.Value, dialect) ?? throw new ApiException(400, "invalid_value", "Increments cannot be null.");
                sets.Add($"{dialect.Quote(col.Name)} = {dialect.Quote(col.Name)} + {ps.Add(v)}");
            }
        if (sets.Count == 0) throw new ApiException(400, "invalid_request", "Provide 'set' or 'inc' with at least one column.");
        var where = queries.Where(filter, t, ps);
        var sql = $"UPDATE {dialect.Quote(phys)} SET {string.Join(", ", sets)}" + (where is null ? "" : " WHERE " + where);
        await using var c = await OpenAsync(false, ct);
        return new AffectedResult(await ExecAsync(c, null, sql, ct, ps));
    });

    static ColumnDefinition WritableColumn(TableDefinition t, string name)
    {
        var col = SqlQueryBuilder.Column(t, name);
        if (t.PrimaryKey?.Contains(col.Name) == true) throw new ApiException(400, "invalid_request", $"Primary key column '{col.Name}' cannot be changed.");
        return col;
    }

    public async Task<AffectedResult> UpsertAsync(UpsertRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        if (r.Keys is null || r.Keys.Count == 0) throw new ApiException(400, "invalid_request", "Provide the key columns of the upsert.");
        var keys = r.Keys.Distinct().ToList();
        foreach (var k in keys) SqlQueryBuilder.Column(t, k);
        var isKey = (t.PrimaryKey ?? []).ToHashSet().SetEquals(keys) || (t.Indexes ?? []).Any(i => i.Unique && i.Columns.ToHashSet().SetEquals(keys));
        if (!isKey) throw new ApiException(400, "invalid_request", "The keys must match the primary key or a unique index exactly.");
        var (cols, values) = RowValues(t, r.Row, requireAll: true);
        foreach (var k in keys) if (!values.ContainsKey(k)) throw new ApiException(400, "missing_value", $"The row needs a value for key column '{k}'.");
        var ps = new SqlParams();
        var exprs = cols.Select(col => dialect.ParamExpr(col, ps.Add(values[col.Name]))).ToList();
        var update = cols.Select(x => x.Name).Where(n => !keys.Contains(n) && t.PrimaryKey?.Contains(n) != true).ToList();
        var sql = dialect.UpsertSql(phys, [.. cols.Select(x => x.Name)], exprs, keys, update);
        await using var c = await OpenAsync(false, ct);
        await ExecAsync(c, null, sql, ct, ps);
        return new AffectedResult(1);
    });

    public async Task<AffectedResult> DeleteAsync(DeleteRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        var filter = FilterParser.Parse(r.Filter);
        if (filter is null && !r.All) throw new ApiException(400, "filter_required", "A delete needs a filter, or all=true where the key allows it.");
        var ps = new SqlParams();
        var where = queries.Where(filter, t, ps);
        var sql = $"DELETE FROM {dialect.Quote(phys)}" + (where is null ? "" : " WHERE " + where);
        await using var c = await OpenAsync(false, ct);
        return new AffectedResult(await ExecAsync(c, null, sql, ct, ps));
    });

    public async Task<CountResult> CountAsync(CountRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        var ps = new SqlParams();
        var where = queries.Where(FilterParser.Parse(r.Filter), t, ps);
        var sql = $"SELECT COUNT(*) FROM {dialect.Quote(phys)}" + (where is null ? "" : " WHERE " + where);
        await using var c = await OpenAsync(false, ct);
        await using var cmd = Command(c, sql, ps);
        return new CountResult(Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)));
    });
}
