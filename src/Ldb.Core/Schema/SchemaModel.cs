using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ldb.Core.Schema;

/// <summary>Portable column types. Every provider maps them to its native types, see docs/SCHEMA.md.</summary>
public static class ColumnTypes
{
    public const string Int32 = "int32", Int64 = "int64", Decimal = "decimal", Float64 = "float64", Bool = "bool",
        String = "string", Text = "text", Json = "json", DateTime = "datetime", Date = "date", Uuid = "uuid", Bytes = "bytes";

    public static readonly HashSet<string> All =
        [Int32, Int64, Decimal, Float64, Bool, String, Text, Json, DateTime, Date, Uuid, Bytes];
}

public sealed record SchemaDefinition(List<TableDefinition> Tables);

public sealed record TableDefinition(
    string Name,
    List<ColumnDefinition> Columns,
    List<string>? PrimaryKey = null,
    List<IndexDefinition>? Indexes = null,
    string? RenamedFrom = null);

public sealed record ColumnDefinition(
    string Name,
    string Type,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    bool Nullable = false,
    JsonElement? Default = null,
    bool Unique = false,
    bool AutoIncrement = false,
    string? RenamedFrom = null);

public sealed record IndexDefinition(string Name, List<string> Columns, bool Unique = false);

public enum SchemaOpKind { RenameTable, RenameColumn, DropIndex, AddColumn, AlterColumn, CreateTable, CreateIndex, DropColumn, DropTable }

public sealed class SchemaOp
{
    public SchemaOpKind Kind { get; init; }
    public string Table { get; init; } = "";
    public string? Column { get; init; }
    public string? NewName { get; init; }
    public string? Index { get; init; }
    public bool Destructive { get; init; }
    public string? Note { get; set; }

    [JsonIgnore] public TableDefinition? TableDef { get; init; }
    [JsonIgnore] public ColumnDefinition? ColumnDef { get; init; }
    [JsonIgnore] public ColumnDefinition? OldColumnDef { get; init; }
    [JsonIgnore] public IndexDefinition? IndexDef { get; init; }
}
