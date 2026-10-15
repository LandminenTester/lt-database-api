using System.Text.Json;
using Ldb.Core.Schema;

namespace Ldb.Core.Query;

public sealed record SortSpec(string Field, string? Dir = "asc");

public sealed record InsertRequest(string Namespace, string Table, List<JsonElement> Rows);
public sealed record FindRequest(string Namespace, string Table, JsonElement? Filter = null, List<string>? Fields = null, List<SortSpec>? Sort = null, int? Limit = null, int? Offset = null);
public sealed record UpdateRequest(string Namespace, string Table, JsonElement? Filter = null, JsonElement? Set = null, JsonElement? Inc = null, bool All = false);
public sealed record UpsertRequest(string Namespace, string Table, JsonElement Row, List<string> Keys);
public sealed record DeleteRequest(string Namespace, string Table, JsonElement? Filter = null, bool All = false);
public sealed record CountRequest(string Namespace, string Table, JsonElement? Filter = null);
public sealed record SyncRequest(string Namespace, SchemaDefinition Schema, bool DryRun = false);
public sealed record DescribeRequest(string Namespace);

public sealed record InsertResult(int Inserted, List<object?> Ids);
public sealed record FindResult(List<Dictionary<string, object?>> Rows, bool HasMore);
public sealed record AffectedResult(long Affected);
public sealed record CountResult(long Count);
public sealed record SyncResult(string Namespace, string Hash, bool Changed, bool DryRun, string Status, List<SchemaOp> Plan);
public sealed record DescribeResult(string Namespace, string? Hash, SchemaDefinition? Schema, DateTime? UpdatedAt);
