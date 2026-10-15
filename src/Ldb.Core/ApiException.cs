namespace Ldb.Core;

/// <summary>An error that is safe to show to the caller. Never put internal details (connection strings, SQL, stack traces) in the message.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
