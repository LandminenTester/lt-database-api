using System.Text.RegularExpressions;

namespace Ldb.Core;

/// <summary>
/// Identifier rules. Only lower-case words joined by single underscores are accepted, so a double underscore
/// can safely separate namespace and table in physical names, and identifiers can never carry quotes or operators.
/// </summary>
public static partial class Names
{
    public const int MaxNamespace = 24, MaxTable = 36, MaxColumn = 48;

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value, int max) => value is not null && value.Length <= max && Pattern().IsMatch(value);

    public static string Namespace(string? value) => Require(value, MaxNamespace, "namespace");
    public static string Table(string? value) => Require(value, MaxTable, "table");
    public static string Column(string? value) => Require(value, MaxColumn, "column");

    static string Require(string? value, int max, string what) =>
        IsValid(value, max) ? value! : throw new ApiException(400, "invalid_identifier", $"The {what} name is not valid. Use lower-case letters, digits and single underscores (max {max}).");

    public static string Physical(string ns, string table) => $"{ns}__{table}";

    /// <summary>Physical index name. It carries the table name because some engines keep index names in one namespace.</summary>
    public static string IndexName(string physicalTable, string index)
    {
        var name = $"ix_{physicalTable}_{index}";
        if (name.Length <= 60) return name;
        return "ix_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..24].ToLowerInvariant();
    }
}
