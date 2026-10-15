namespace Ldb.Core.Protocol;

/// <summary>Signed request. Payload is the raw JSON text of the operation, signed byte for byte.</summary>
public sealed record RequestEnvelope(int V, string Kid, long Ts, string Nonce, string Payload, string Sig);

/// <summary>Signed response. Error responses that are not authenticated carry no signature.</summary>
public sealed record ResponseEnvelope(int V, bool Ok, int Status, string Payload, string? Sig = null, string? Code = null);
