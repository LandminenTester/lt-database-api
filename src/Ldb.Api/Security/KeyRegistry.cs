using System.Net;
using System.Text;
using Ldb.Core.Config;

namespace Ldb.Api.Security;

/// <summary>An API key with its resolved secrets and limits.</summary>
public sealed class ApiKey
{
    public required string KeyId { get; init; }
    public required IReadOnlyList<byte[]> Secrets { get; init; }
    public required IReadOnlyList<string> Namespaces { get; init; }
    public required IReadOnlySet<string> Operations { get; init; }
    public required IReadOnlyList<IPNetwork> Networks { get; init; }
    public required int RequestsPerMinute { get; init; }
    public required bool AllowUnfilteredWrites { get; init; }

    public bool AllowsNamespace(string ns) => Namespaces.Contains("*") || Namespaces.Contains(ns);
    public bool AllowsOperation(string op) => Operations.Count == 0 || Operations.Contains(op);
    public bool AllowsAddress(IPAddress? ip)
    {
        if (Networks.Count == 0) return true;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return Networks.Any(n => n.Contains(ip));
    }
}

public sealed class KeyRegistry
{
    public const int MinSecretBytes = 32;
    readonly Dictionary<string, ApiKey> keys = new(StringComparer.Ordinal);

    // Used to spend the same time on unknown key ids, so timing does not reveal which ids exist.
    public static readonly byte[] DummySecret = Encoding.UTF8.GetBytes(new string('x', MinSecretBytes));

    public KeyRegistry(SecurityOptions options)
    {
        foreach (var k in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(k.KeyId) || k.KeyId.Length > 64 || k.KeyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                throw new InvalidOperationException("Every API key needs a KeyId of at most 64 characters (letters, digits, '-', '_', '.').");
            if (keys.ContainsKey(k.KeyId)) throw new InvalidOperationException($"API key '{k.KeyId}' is configured twice.");
            var secrets = new List<byte[]> { SecretResolver.Resolve(k.Current, $"{k.KeyId}.Current") };
            if (!string.IsNullOrWhiteSpace(k.Next)) secrets.Add(SecretResolver.Resolve(k.Next, $"{k.KeyId}.Next"));
            if (k.AllowedNamespaces.Count == 0) throw new InvalidOperationException($"API key '{k.KeyId}' needs at least one allowed namespace (or \"*\").");
            keys[k.KeyId] = new ApiKey
            {
                KeyId = k.KeyId,
                Secrets = secrets,
                Namespaces = k.AllowedNamespaces,
                Operations = k.AllowedOperations.ToHashSet(StringComparer.Ordinal),
                Networks = [.. k.AllowedCidrs.Select(c => IPNetwork.TryParse(c, out var n) ? n : throw new InvalidOperationException($"API key '{k.KeyId}': '{c}' is not a valid CIDR range."))],
                RequestsPerMinute = Math.Max(1, k.RequestsPerMinute),
                AllowUnfilteredWrites = k.AllowUnfilteredWrites,
            };
        }
        if (keys.Count == 0) throw new InvalidOperationException("No API key is configured. Add at least one entry to Ldb:Security:Keys.");
    }

    public ApiKey? Find(string kid) => kid is not null && keys.TryGetValue(kid, out var k) ? k : null;
    public int Count => keys.Count;
}

/// <summary>Secrets are never read from plain configuration values. Use env:NAME or file:/path (Docker and Kubernetes secrets are files).</summary>
public static class SecretResolver
{
    public static byte[] Resolve(string reference, string label)
    {
        string value;
        if (reference.StartsWith("env:", StringComparison.Ordinal))
            value = Environment.GetEnvironmentVariable(reference[4..]) ?? throw new InvalidOperationException($"Secret {label}: environment variable '{reference[4..]}' is not set.");
        else if (reference.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = reference[5..];
            value = File.Exists(path) ? File.ReadAllText(path) : throw new InvalidOperationException($"Secret {label}: file '{path}' does not exist.");
        }
        else throw new InvalidOperationException($"Secret {label} must be a reference like env:NAME or file:/path/to/secret. Plain values are refused.");

        value = value.Trim();
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < KeyRegistry.MinSecretBytes)
            throw new InvalidOperationException($"Secret {label} is too short: use at least {KeyRegistry.MinSecretBytes} random characters (for example 'openssl rand -base64 48').");
        return bytes;
    }
}
