using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ldb.Core.Protocol;

/// <summary>
/// HMAC-SHA256 request and response signing. The bundle implements the same strings, see docs/SECURITY.md
/// and tests/Ldb.Tests/SigningTests.cs for fixed test vectors.
/// </summary>
public static class Signing
{
    public static string SignRequest(byte[] secret, string path, string kid, long ts, string nonce, string payload) =>
        Mac(secret, $"v1\n{path}\n{kid}\n{ts}\n{nonce}\n{payload}");

    public static string SignResponse(byte[] secret, string requestNonce, int status, string payload) =>
        Mac(secret, $"v1resp\n{requestNonce}\n{status}\n{payload}");

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    static string Mac(byte[] secret, string message) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(message)));
}
