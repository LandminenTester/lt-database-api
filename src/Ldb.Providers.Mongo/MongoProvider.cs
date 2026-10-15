using System.Collections.Concurrent;
using System.Text.Json;
using Ldb.Core;
using Ldb.Core.Config;
using Ldb.Core.Query;
using Ldb.Core.Schema;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Ldb.Providers.Mongo;

/// <summary>
/// MongoDB provider. Tables become collections named {namespace}__{table}, columns become fields, and the declared
/// types are enforced by a $jsonSchema validator. A single-column primary key is stored as _id.
/// </summary>
public sealed class MongoProvider : IDatabaseProvider
{
    const string MetaCollection = "_ldb_schema", LockCollection = "_ldb_locks", CounterCollection = "_ldb_counters";
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    readonly IMongoDatabase data, admin;
    readonly ILogger<MongoProvider> logger;
    readonly ConcurrentDictionary<string, (SchemaDefinition Schema, DateTime Loaded)> cache = new();

    public MongoProvider(DatabaseOptions options, ILogger<MongoProvider> logger)
    {
        this.logger = logger;
        var ddl = string.IsNullOrWhiteSpace(options.DdlConnectionString) ? options.DmlConnectionString : options.DdlConnectionString;
        data = new MongoClient(options.DmlConnectionString).GetDatabase(options.MongoDatabase);
        admin = ddl == options.DmlConnectionString ? data : new MongoClient(ddl).GetDatabase(options.MongoDatabase);
    }

    public string Name => "mongodb";

    // ---------------------------------------------------------------- infrastructure

    async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (MongoBulkWriteException ex) { logger.LogError("MongoDB bulk write error {Category}", ex.WriteErrors.FirstOrDefault()?.Category); throw MapWrite(ex.WriteErrors.FirstOrDefault()); }
        catch (MongoWriteException ex) { logger.LogError("MongoDB write error {Category}", ex.WriteError?.Category); throw MapWrite(ex.WriteError); }
        catch (MongoCommandException ex) { logger.LogError("MongoDB command error code={Code}", ex.Code); throw ex.Code == 121 ? InvalidDocument() : new ApiException(500, "database_error", "The database could not complete the operation."); }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            logger.LogError("MongoDB error {Type}", ex.GetType().Name);
            throw new ApiException(ex is TimeoutException ? 504 : 500, ex is TimeoutException ? "database_timeout" : "database_error", "The database could not complete the operation.");
        }
    }

    static ApiException MapWrite(WriteError? e) => e?.Category switch
    {
        ServerErrorCategory.DuplicateKey => new ApiException(409, "constraint_violation", "A unique value already exists."),
        _ when e?.Code == 121 => InvalidDocument(),
        _ => new ApiException(500, "database_error", "The database could not complete the operation."),
    };

    static ApiException InvalidDocument() => new(400, "invalid_value", "The document does not fit the table definition.");

    public async Task InitializeAsync(CancellationToken ct)
    {
        await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try { await data.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct); return true; }
        catch (Exception ex) when (ex is MongoException or TimeoutException) { logger.LogWarning("MongoDB ping failed: {Type}", ex.GetType().Name); return false; }
    }

    // ---------------------------------------------------------------- stored schema

    async Task<BsonDocument?> LoadStoredAsync(IMongoDatabase db, string ns, CancellationToken ct) =>
        await db.GetCollection<BsonDocument>(MetaCollection).Find(new BsonDocument("_id", ns)).FirstOrDefaultAsync(ct);

    async Task<SchemaDefinition> GetSchemaAsync(string ns, CancellationToken ct)
    {
        if (cache.TryGetValue(ns, out var hit) && DateTime.UtcNow - hit.Loaded < CacheTtl) return hit.Schema;
        var doc = await LoadStoredAsync(data, ns, ct) ?? throw new ApiException(404, "namespace_unknown", "This namespace has no schema yet. Run a schema sync first.");
        var schema = Json.Parse<SchemaDefinition>(doc["definition"].AsString);
        cache[ns] = (schema, DateTime.UtcNow);
        return schema;
    }

    public async Task<DescribeResult> DescribeAsync(string ns, CancellationToken ct) => await Guard(async () =>
    {
        ns = Names.Namespace(ns);
        var doc = await LoadStoredAsync(data, ns, ct);
        if (doc is null) return new DescribeResult(ns, null, null, null);
        return new DescribeResult(ns, doc["hash"].AsString, Json.Parse<SchemaDefinition>(doc["definition"].AsString), DateTime.Parse(doc["updated_at"].AsString, null, System.Globalization.DateTimeStyles.RoundtripKind));
    });

    // ---------------------------------------------------------------- helpers

    static string FieldOf(TableDefinition t, string column) => t.PrimaryKey is { Count: 1 } pk && pk[0] == column ? "_id" : column;
    static bool HasIdKey(TableDefinition t) => t.PrimaryKey is { Count: 1 };

    static ColumnDefinition Column(TableDefinition t, string name) =>
        t.Columns.FirstOrDefault(c => c.Name == name) ?? throw new ApiException(400, "unknown_column", $"Table '{t.Name}' has no column '{name}'.");

    static TableDefinition Table(SchemaDefinition s, string name) =>
        s.Tables.FirstOrDefault(t => t.Name == name) ?? throw new ApiException(404, "unknown_table", $"The namespace has no table '{name}'.");

    async Task<(TableDefinition Table, IMongoCollection<BsonDocument> Collection, string Physical)> ResolveAsync(string ns, string table, CancellationToken ct)
    {
        ns = Names.Namespace(ns); table = Names.Table(table);
        var t = Table(await GetSchemaAsync(ns, ct), table);
        var phys = Names.Physical(ns, table);
        return (t, data.GetCollection<BsonDocument>(phys), phys);
    }

    static BsonDocument Validator(TableDefinition t)
    {
        static BsonValue Types(ColumnDefinition c)
        {
            string[] types = c.Type switch
            {
                ColumnTypes.Int32 or ColumnTypes.Int64 => ["int", "long"],
                ColumnTypes.Float64 => ["double", "int", "long", "decimal"],
                ColumnTypes.Decimal => ["decimal"],
                ColumnTypes.Bool => ["bool"],
                ColumnTypes.String or ColumnTypes.Text or ColumnTypes.Uuid => ["string"],
                ColumnTypes.DateTime or ColumnTypes.Date => ["date"],
                ColumnTypes.Bytes => ["binData"],
                _ => [],
            };
            if (c.Nullable && types.Length > 0) types = [.. types, "null"];
            return new BsonArray(types);
        }

        var props = new BsonDocument("_id", new BsonDocument());
        var required = new BsonArray();
        foreach (var c in t.Columns)
        {
            var spec = new BsonDocument();
            var types = Types(c);
            if (types.AsBsonArray.Count > 0) spec["bsonType"] = types;
            if (c.Type == ColumnTypes.String && c.Length is { } max) spec["maxLength"] = max;
            props[FieldOf(t, c.Name)] = spec;
            if (!c.Nullable && c.Default is null && !c.AutoIncrement && FieldOf(t, c.Name) != "_id") required.Add(c.Name);
        }
        // Fields parked by Quarantine mode (_dropped_*) stay legal; anything else that is not declared is refused.
        var schema = new BsonDocument { { "bsonType", "object" }, { "properties", props }, { "patternProperties", new BsonDocument("^_dropped_", new BsonDocument()) }, { "additionalProperties", false } };
        if (required.Count > 0) schema["required"] = required;
        return new BsonDocument("$jsonSchema", schema);
    }

    Task ApplyValidatorAsync(string phys, TableDefinition t, CancellationToken ct) =>
        admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", phys }, { "validator", Validator(t) }, { "validationLevel", "moderate" }, { "validationAction", "error" } }, cancellationToken: ct);

    async Task SetValidationLevelAsync(string phys, string level, CancellationToken ct)
    {
        if (!await CollectionExistsAsync(phys, ct)) return;
        await admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", phys }, { "validationLevel", level } }, cancellationToken: ct);
    }

    async Task<bool> CollectionExistsAsync(string phys, CancellationToken ct)
    {
        var cursor = await admin.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", phys) }, ct);
        return (await cursor.ToListAsync(ct)).Count > 0;
    }

    async Task<HashSet<string>> IndexNamesAsync(IMongoCollection<BsonDocument> col, CancellationToken ct)
    {
        var cursor = await col.Indexes.ListAsync(ct);
        return (await cursor.ToListAsync(ct)).Select(d => d["name"].AsString).ToHashSet();
    }

    // ---------------------------------------------------------------- schema sync

    public async Task<SyncResult> SyncAsync(string ns, SchemaDefinition desired, bool dryRun, DestructiveMode mode, CancellationToken ct) => await Guard(async () =>
    {
        ns = Names.Namespace(ns);
        var hash = SchemaHasher.Hash(desired);
        var locked = false;
        try
        {
            if (!dryRun) { await LockAsync(ns, ct); locked = true; }
            var storedDoc = await LoadStoredAsync(admin, ns, ct);
            var stored = storedDoc is null ? null : Json.Parse<SchemaDefinition>(storedDoc["definition"].AsString);
            var ops = SchemaDiffer.Diff(stored, desired);
            await AddRepairOpsAsync(ns, desired, ops, ct);
            ops = [.. ops.OrderBy(o => (int)o.Kind)];

            if (ops.Count == 0 && storedDoc is not null && storedDoc["hash"].AsString == hash) return new SyncResult(ns, hash, false, dryRun, "unchanged", []);
            var destructive = ops.Count(o => o.Destructive);
            if (mode == DestructiveMode.Deny && destructive > 0)
            {
                if (dryRun) return new SyncResult(ns, hash, true, true, "denied", ops);
                throw new ApiException(409, "destructive_denied", $"The change contains {destructive} destructive operation(s) and this server denies them. Run a dry run to see the plan.");
            }
            if (dryRun) return new SyncResult(ns, hash, true, true, "planned", ops);

            // Data steps (renames, back-fills) move documents through states the final validator would refuse,
            // so validation is switched off while they run and the final validators are installed afterwards.
            foreach (var t in stored?.Tables ?? []) await SetValidationLevelAsync(Names.Physical(ns, t.Name), "off", ct);
            try
            {
                foreach (var op in ops) await ApplyAsync(ns, op, mode, ct);
                foreach (var t in desired.Tables) await ApplyValidatorAsync(Names.Physical(ns, t.Name), t, ct);
            }
            catch
            {
                // Best effort: put the previous rules back so a failed sync does not leave collections unvalidated.
                foreach (var t in stored?.Tables ?? [])
                {
                    try { if (await CollectionExistsAsync(Names.Physical(ns, t.Name), CancellationToken.None)) await ApplyValidatorAsync(Names.Physical(ns, t.Name), t, CancellationToken.None); }
                    catch (MongoException) { logger.LogWarning("Could not restore the validator of a collection after a failed sync"); }
                }
                throw;
            }

            var doc = new BsonDocument { { "_id", ns }, { "hash", hash }, { "definition", Json.Write(SchemaHasher.Canonicalize(desired)) }, { "updated_at", DateTime.UtcNow.ToString("O") } };
            await admin.GetCollection<BsonDocument>(MetaCollection).ReplaceOneAsync(new BsonDocument("_id", ns), doc, new ReplaceOptions { IsUpsert = true }, ct);
            cache.TryRemove(ns, out _);
            return new SyncResult(ns, hash, true, false, "applied", ops);
        }
        finally
        {
            if (locked) await admin.GetCollection<BsonDocument>(LockCollection).DeleteOneAsync(new BsonDocument("_id", ns), CancellationToken.None);
        }
    });

    /// <summary>MongoDB has no transactional DDL, so a lock document with an expiry keeps two syncs of one namespace apart.</summary>
    async Task LockAsync(string ns, CancellationToken ct)
    {
        var locks = admin.GetCollection<BsonDocument>(LockCollection);
        var giveUp = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await locks.InsertOneAsync(new BsonDocument { { "_id", ns }, { "expires", DateTime.UtcNow.AddMinutes(2) } }, cancellationToken: ct);
                return;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                await locks.DeleteOneAsync(new BsonDocument { { "_id", ns }, { "expires", new BsonDocument("$lt", DateTime.UtcNow) } }, ct);
            }
            if (DateTime.UtcNow > giveUp) throw new ApiException(409, "sync_in_progress", "Another schema sync of this namespace is running. Try again shortly.");
            await Task.Delay(150, ct);
        }
    }

    async Task AddRepairOpsAsync(string ns, SchemaDefinition desired, List<SchemaOp> ops, CancellationToken ct)
    {
        foreach (var t in desired.Tables)
        {
            if (ops.Any(o => (o.Kind == SchemaOpKind.CreateTable && o.Table == t.Name) || (o.Kind == SchemaOpKind.RenameTable && o.NewName == t.Name))) continue;
            var phys = Names.Physical(ns, t.Name);
            if (!await CollectionExistsAsync(phys, ct))
            {
                ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateTable, Table = t.Name, TableDef = t, Note = "repair: collection is missing" });
                foreach (var ix in t.Indexes ?? []) ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = t.Name, Index = ix.Name, IndexDef = ix, Note = "repair" });
                continue;
            }
            var existing = await IndexNamesAsync(admin.GetCollection<BsonDocument>(phys), ct);
            foreach (var ix in (t.Indexes ?? []).Where(x => !existing.Contains(Names.IndexName(phys, x.Name))))
                if (!ops.Any(o => o.Kind == SchemaOpKind.CreateIndex && o.Index == ix.Name && o.Table == t.Name))
                    ops.Add(new SchemaOp { Kind = SchemaOpKind.CreateIndex, Table = t.Name, Index = ix.Name, IndexDef = ix, Note = "repair: index is missing" });
        }
    }

    async Task ApplyAsync(string ns, SchemaOp op, DestructiveMode mode, CancellationToken ct)
    {
        var phys = Names.Physical(ns, op.Table);
        switch (op.Kind)
        {
            case SchemaOpKind.RenameTable:
                var to = Names.Physical(ns, op.NewName!);
                if (await CollectionExistsAsync(phys, ct) && !await CollectionExistsAsync(to, ct)) await admin.RenameCollectionAsync(phys, to, cancellationToken: ct);
                break;
            case SchemaOpKind.RenameColumn:
                await admin.GetCollection<BsonDocument>(phys).UpdateManyAsync(new BsonDocument(op.Column!, new BsonDocument("$exists", true)),
                    new BsonDocument("$rename", new BsonDocument(op.Column!, op.NewName!)), cancellationToken: ct);
                break;
            case SchemaOpKind.DropIndex:
                var col = admin.GetCollection<BsonDocument>(phys);
                var ixName = Names.IndexName(phys, op.Index!);
                if ((await IndexNamesAsync(col, ct)).Contains(ixName)) await col.Indexes.DropOneAsync(ixName, ct);
                break;
            case SchemaOpKind.AddColumn:
                if (op.ColumnDef!.Default is { } def)
                {
                    var field = op.ColumnDef.Name;
                    await admin.GetCollection<BsonDocument>(phys).UpdateManyAsync(new BsonDocument(field, new BsonDocument("$exists", false)),
                        new BsonDocument("$set", new BsonDocument(field, DefaultValue(op.ColumnDef, def))), cancellationToken: ct);
                }
                break;
            case SchemaOpKind.AlterColumn:
                if (op.OldColumnDef!.AutoIncrement != op.ColumnDef!.AutoIncrement)
                    throw new ApiException(409, "unsupported_change", $"Column '{op.Table}.{op.ColumnDef.Name}': switching auto-increment on or off is not supported.");
                break; // the validator is refreshed once at the end; stored documents are not converted
            case SchemaOpKind.CreateTable:
                if (!await CollectionExistsAsync(phys, ct))
                {
                    await admin.CreateCollectionAsync(phys, new CreateCollectionOptions<BsonDocument>
                    {
                        Validator = new BsonDocumentFilterDefinition<BsonDocument>(Validator(op.TableDef!)),
                        ValidationAction = DocumentValidationAction.Error,
                        ValidationLevel = DocumentValidationLevel.Moderate,
                    }, ct);
                }
                if (op.TableDef!.PrimaryKey is { Count: > 1 } pk)
                {
                    var keys = new BsonDocument(); foreach (var k in pk) keys[k] = 1;
                    await admin.GetCollection<BsonDocument>(phys).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions { Name = Names.IndexName(phys, "pk"), Unique = true }), cancellationToken: ct);
                }
                break;
            case SchemaOpKind.CreateIndex:
                var target = admin.GetCollection<BsonDocument>(phys);
                var name = Names.IndexName(phys, op.Index!);
                if ((await IndexNamesAsync(target, ct)).Contains(name)) break;
                var ixKeys = new BsonDocument();
                foreach (var cn in op.IndexDef!.Columns) ixKeys[cn] = 1;
                await target.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ixKeys, new CreateIndexOptions { Name = name, Unique = op.IndexDef.Unique }), cancellationToken: ct);
                break;
            case SchemaOpKind.DropColumn:
                if (mode == DestructiveMode.Quarantine)
                {
                    var parked = Park(op.Column!);
                    await admin.GetCollection<BsonDocument>(phys).UpdateManyAsync(new BsonDocument(op.Column!, new BsonDocument("$exists", true)),
                        new BsonDocument("$rename", new BsonDocument(op.Column!, parked)), cancellationToken: ct);
                    op.Note = $"quarantined as {parked}";
                }
                else
                {
                    await admin.GetCollection<BsonDocument>(phys).UpdateManyAsync(new BsonDocument(), new BsonDocument("$unset", new BsonDocument(op.Column!, "")), cancellationToken: ct);
                }
                break;
            case SchemaOpKind.DropTable:
                if (mode == DestructiveMode.Quarantine)
                {
                    if (!await CollectionExistsAsync(phys, ct)) break;
                    var parked = Park(phys);
                    await admin.RenameCollectionAsync(phys, parked, cancellationToken: ct);
                    op.Note = $"quarantined as {parked}";
                }
                else await admin.DropCollectionAsync(phys, ct);
                break;
        }
    }

    static string Park(string name) => $"_dropped_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{name}";

    static BsonValue DefaultValue(ColumnDefinition c, JsonElement d) =>
        c.Type == ColumnTypes.DateTime && d.ValueKind == JsonValueKind.String && d.GetString() == "now" ? new BsonDateTime(DateTime.UtcNow) : MongoValues.ToBson(c, d);

    // ---------------------------------------------------------------- filters

    BsonDocument Where(FilterNode? node, TableDefinition t) => node is null ? new BsonDocument() : Build(node, t);

    BsonDocument Build(FilterNode node, TableDefinition t) => node switch
    {
        AndNode a => new BsonDocument("$and", new BsonArray(a.Children.Select(c => Build(c, t)))),
        OrNode o => new BsonDocument("$or", new BsonArray(o.Children.Select(c => Build(c, t)))),
        NotNode n => new BsonDocument("$nor", new BsonArray { Build(n.Child, t) }),
        CompareNode c => Compare(c, t),
        _ => throw new ApiException(400, "invalid_filter", "Unsupported filter node."),
    };

    BsonDocument Compare(CompareNode c, TableDefinition t)
    {
        var col = Column(t, c.Field);
        if (col.Type == ColumnTypes.Json) throw new ApiException(400, "invalid_filter", "json columns cannot be filtered.");
        var field = FieldOf(t, col.Name);
        switch (c.Op)
        {
            case FilterOps.IsNull:
                return c.Value.ValueKind == JsonValueKind.True ? new BsonDocument(field, BsonNull.Value) : new BsonDocument(field, new BsonDocument("$ne", BsonNull.Value));
            case FilterOps.In or FilterOps.Nin:
                var list = new BsonArray(c.Value.EnumerateArray().Select(i =>
                {
                    var v = MongoValues.ToBson(col, i);
                    return v.IsBsonNull ? throw new ApiException(400, "invalid_filter", "Lists cannot contain null. Use isNull.") : v;
                }));
                return new BsonDocument(field, new BsonDocument(c.Op == FilterOps.In ? "$in" : "$nin", list));
            case FilterOps.StartsWith or FilterOps.Contains:
                if (col.Type is not (ColumnTypes.String or ColumnTypes.Text)) throw new ApiException(400, "invalid_filter", $"'{c.Op}' only works on text columns.");
                var text = MongoValues.EscapeRegex(c.Value.GetString()!);
                return new BsonDocument(field, new BsonDocument("$regex", c.Op == FilterOps.StartsWith ? "^" + text : text));
        }
        var value = MongoValues.ToBson(col, c.Value);
        if (value.IsBsonNull)
        {
            if (c.Op == FilterOps.Eq) return new BsonDocument(field, BsonNull.Value);
            if (c.Op == FilterOps.Ne) return new BsonDocument(field, new BsonDocument("$ne", BsonNull.Value));
            throw new ApiException(400, "invalid_filter", $"'{c.Op}' cannot compare with null.");
        }
        return c.Op switch
        {
            FilterOps.Eq => new BsonDocument(field, value),
            FilterOps.Ne => new BsonDocument(field, new BsonDocument("$ne", value)),
            FilterOps.Gt => new BsonDocument(field, new BsonDocument("$gt", value)),
            FilterOps.Gte => new BsonDocument(field, new BsonDocument("$gte", value)),
            FilterOps.Lt => new BsonDocument(field, new BsonDocument("$lt", value)),
            FilterOps.Lte => new BsonDocument(field, new BsonDocument("$lte", value)),
            _ => throw new ApiException(400, "invalid_filter", "Unsupported operator."),
        };
    }

    // ---------------------------------------------------------------- data

    public async Task<InsertResult> InsertAsync(InsertRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, col, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        if (r.Rows is null || r.Rows.Count == 0) throw new ApiException(400, "invalid_request", "Provide at least one row.");
        var docs = new List<BsonDocument>();
        var ids = new List<object?>();
        foreach (var row in r.Rows)
        {
            var (doc, id) = await BuildDocumentAsync(t, phys, row, ct);
            docs.Add(doc); ids.Add(id);
        }
        await col.InsertManyAsync(docs, new InsertManyOptions { IsOrdered = true }, ct);
        return new InsertResult(docs.Count, ids);
    });

    async Task<(BsonDocument Doc, object? Id)> BuildDocumentAsync(TableDefinition t, string phys, JsonElement row, CancellationToken ct)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new ApiException(400, "invalid_request", "Each row must be an object.");
        var doc = new BsonDocument();
        foreach (var prop in row.EnumerateObject())
        {
            var c = Column(t, prop.Name);
            var field = FieldOf(t, c.Name);
            if (doc.Contains(field)) throw new ApiException(400, "invalid_request", $"Column '{c.Name}' appears twice.");
            doc[field] = MongoValues.ToBson(c, prop.Value);
        }
        foreach (var c in t.Columns)
        {
            var field = FieldOf(t, c.Name);
            if (doc.Contains(field)) continue;
            if (c.AutoIncrement) doc[field] = await NextIdAsync(phys, c, ct);
            else if (c.Default is { } d) doc[field] = DefaultValue(c, d);
            else if (!c.Nullable) throw new ApiException(400, "missing_value", $"Column '{c.Name}' needs a value.");
        }
        if (!doc.Contains("_id")) doc["_id"] = ObjectId.GenerateNewId();
        var single = HasIdKey(t) ? t.Columns.First(c => c.Name == t.PrimaryKey![0]) : null;
        object? id = single is not null ? MongoValues.ToJson(single, doc["_id"]) : doc["_id"].ToString();
        return (doc, id);
    }

    async Task<BsonValue> NextIdAsync(string phys, ColumnDefinition c, CancellationToken ct)
    {
        var counters = data.GetCollection<BsonDocument>(CounterCollection);
        var after = await counters.FindOneAndUpdateAsync(new BsonDocument("_id", phys), new BsonDocument("$inc", new BsonDocument("seq", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        var seq = after["seq"].ToInt64();
        return c.Type == ColumnTypes.Int32 ? new BsonInt32((int)seq) : new BsonInt64(seq);
    }

    public async Task<FindResult> FindAsync(FindRequest r, int maxRows, CancellationToken ct) => await Guard(async () =>
    {
        var (t, col, _) = await ResolveAsync(r.Namespace, r.Table, ct);
        var fields = r.Fields is { Count: > 0 } ? r.Fields.Distinct().Select(f => Column(t, f)).ToList() : t.Columns;
        var limit = Math.Clamp(r.Limit ?? maxRows, 1, maxRows);
        var offset = r.Offset ?? 0;
        if (offset is < 0 or > 1_000_000) throw new ApiException(400, "invalid_request", "Offset is out of range.");

        var sort = new BsonDocument();
        foreach (var s in r.Sort ?? [])
        {
            var c = Column(t, s.Field);
            var dir = (s.Dir ?? "asc").ToLowerInvariant();
            if (dir is not ("asc" or "desc")) throw new ApiException(400, "invalid_sort", "Sort direction must be asc or desc.");
            if (c.Type == ColumnTypes.Json) throw new ApiException(400, "invalid_sort", "json columns cannot be sorted.");
            sort[FieldOf(t, c.Name)] = dir == "asc" ? 1 : -1;
        }
        if (sort.ElementCount == 0) sort["_id"] = 1;

        var projection = new BsonDocument("_id", fields.Any(f => FieldOf(t, f.Name) == "_id") ? 1 : 0);
        foreach (var f in fields) projection[FieldOf(t, f.Name)] = 1;

        var docs = await col.Find(Where(FilterParser.Parse(r.Filter), t)).Project<BsonDocument>(projection).Sort(sort).Skip(offset).Limit(limit + 1).ToListAsync(ct);
        var rows = docs.Select(d =>
        {
            var row = new Dictionary<string, object?>(fields.Count);
            foreach (var f in fields) row[f.Name] = d.TryGetValue(FieldOf(t, f.Name), out var v) ? MongoValues.ToJson(f, v) : null;
            return row;
        }).ToList();
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new FindResult(rows, more);
    });

    public async Task<AffectedResult> UpdateAsync(UpdateRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, col, _) = await ResolveAsync(r.Namespace, r.Table, ct);
        var filter = FilterParser.Parse(r.Filter);
        if (filter is null && !r.All) throw new ApiException(400, "filter_required", "An update needs a filter, or all=true where the key allows it.");
        var update = new BsonDocument();
        if (r.Set is { ValueKind: JsonValueKind.Object } set)
        {
            var doc = new BsonDocument();
            foreach (var prop in set.EnumerateObject()) { var c = Writable(t, prop.Name); doc[c.Name] = MongoValues.ToBson(c, prop.Value); }
            if (doc.ElementCount > 0) update["$set"] = doc;
        }
        if (r.Inc is { ValueKind: JsonValueKind.Object } inc)
        {
            var doc = new BsonDocument();
            foreach (var prop in inc.EnumerateObject())
            {
                var c = Writable(t, prop.Name);
                if (c.Type is not (ColumnTypes.Int32 or ColumnTypes.Int64 or ColumnTypes.Decimal or ColumnTypes.Float64))
                    throw new ApiException(400, "invalid_request", $"Column '{c.Name}' is not numeric, so it cannot be incremented.");
                var v = MongoValues.ToBson(c, prop.Value);
                doc[c.Name] = v.IsBsonNull ? throw new ApiException(400, "invalid_value", "Increments cannot be null.") : v;
            }
            if (doc.ElementCount > 0) update["$inc"] = doc;
        }
        if (update.ElementCount == 0) throw new ApiException(400, "invalid_request", "Provide 'set' or 'inc' with at least one column.");
        var result = await col.UpdateManyAsync(Where(filter, t), update, cancellationToken: ct);
        return new AffectedResult(result.MatchedCount);
    });

    static ColumnDefinition Writable(TableDefinition t, string name)
    {
        var c = Column(t, name);
        if (t.PrimaryKey?.Contains(c.Name) == true) throw new ApiException(400, "invalid_request", $"Primary key column '{c.Name}' cannot be changed.");
        return c;
    }

    public async Task<AffectedResult> UpsertAsync(UpsertRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, col, phys) = await ResolveAsync(r.Namespace, r.Table, ct);
        if (r.Keys is null || r.Keys.Count == 0) throw new ApiException(400, "invalid_request", "Provide the key columns of the upsert.");
        var keys = r.Keys.Distinct().ToList();
        foreach (var k in keys) Column(t, k);
        var isKey = (t.PrimaryKey ?? []).ToHashSet().SetEquals(keys) || (t.Indexes ?? []).Any(i => i.Unique && i.Columns.ToHashSet().SetEquals(keys));
        if (!isKey) throw new ApiException(400, "invalid_request", "The keys must match the primary key or a unique index exactly.");
        if (r.Row.ValueKind != JsonValueKind.Object) throw new ApiException(400, "invalid_request", "The row must be an object.");

        var filter = new BsonDocument(); var set = new BsonDocument();
        var given = new HashSet<string>();
        foreach (var prop in r.Row.EnumerateObject())
        {
            var c = Column(t, prop.Name);
            if (!given.Add(c.Name)) throw new ApiException(400, "invalid_request", $"Column '{c.Name}' appears twice.");
            var value = MongoValues.ToBson(c, prop.Value);
            if (keys.Contains(c.Name)) filter[FieldOf(t, c.Name)] = value;
            else if (t.PrimaryKey?.Contains(c.Name) != true) set[c.Name] = value;
        }
        foreach (var k in keys) if (!given.Contains(k)) throw new ApiException(400, "missing_value", $"The row needs a value for key column '{k}'.");

        // Existing document: change it. New key: insert a complete document (defaults, generated ids, required checks).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (set.ElementCount > 0)
            {
                if ((await col.UpdateOneAsync(filter, new BsonDocument("$set", set), cancellationToken: ct)).MatchedCount > 0) return new AffectedResult(1);
            }
            else if (await col.CountDocumentsAsync(filter, new CountOptions { Limit = 1 }, ct) > 0) return new AffectedResult(1);

            var (doc, _) = await BuildDocumentAsync(t, phys, r.Row, ct);
            try { await col.InsertOneAsync(doc, cancellationToken: ct); return new AffectedResult(1); }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey && attempt == 0) { /* lost a race: update instead */ }
        }
        return new AffectedResult(1);
    });

    public async Task<AffectedResult> DeleteAsync(DeleteRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, col, _) = await ResolveAsync(r.Namespace, r.Table, ct);
        var filter = FilterParser.Parse(r.Filter);
        if (filter is null && !r.All) throw new ApiException(400, "filter_required", "A delete needs a filter, or all=true where the key allows it.");
        var result = await col.DeleteManyAsync(Where(filter, t), ct);
        return new AffectedResult(result.DeletedCount);
    });

    public async Task<CountResult> CountAsync(CountRequest r, CancellationToken ct) => await Guard(async () =>
    {
        var (t, col, _) = await ResolveAsync(r.Namespace, r.Table, ct);
        return new CountResult(await col.CountDocumentsAsync(Where(FilterParser.Parse(r.Filter), t), cancellationToken: ct));
    });
}
