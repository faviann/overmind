namespace MemSrv.Core;

/// <summary>
/// Raised when a call names a namespace outside the request context's
/// allowlist. The message names the offending namespace so the caller learns
/// exactly what was rejected.
/// </summary>
public sealed class NamespaceForbiddenException(string @namespace, string agentId)
    : Exception($"Namespace '{@namespace}' is not in the allowlist for agent '{agentId}'.")
{
    public string Namespace { get; } = @namespace;
}
