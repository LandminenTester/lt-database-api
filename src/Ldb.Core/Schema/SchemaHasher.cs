using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ldb.Core.Schema;

/// <summary>Stable fingerprint of a normalized schema. Rename hints are not part of the state, so they are ignored.</summary>
public static class SchemaHasher
{
    public static string Hash(SchemaDefinition normalized)
    {
        var canonical = JsonSerializer.Serialize(Canonicalize(normalized), Json.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Sorted copy without rename hints. This is also the form stored as "previous schema".</summary>
    public static SchemaDefinition Canonicalize(SchemaDefinition s) => new(
        [.. s.Tables.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t with
        {
            RenamedFrom = null,
            Columns = [.. t.Columns.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c with { RenamedFrom = null })],
            PrimaryKey = t.PrimaryKey ?? [],
            Indexes = [.. (t.Indexes ?? []).OrderBy(i => i.Name, StringComparer.Ordinal)],
        })]);
}
