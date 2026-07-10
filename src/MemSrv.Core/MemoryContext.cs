namespace MemSrv.Core;

/// <summary>
/// Per-call request context: who is acting, their default namespace, the
/// namespaces they may reach, and the transport-derived session. Constructed
/// per call/session — never a process-wide mutable singleton — so the same
/// service seam serves stdio (one context from env) and HTTP (one context per
/// bearer-keyed MCP session).
/// </summary>
public sealed record MemoryContext
{
    public string AgentId { get; }
    public string DefaultNamespace { get; }
    public IReadOnlySet<string> AllowedNamespaces { get; }
    public string SessionId { get; }

    public MemoryContext(string agentId, string defaultNamespace, IEnumerable<string> allowedNamespaces, string sessionId)
    {
        AgentId = agentId;
        DefaultNamespace = defaultNamespace;
        SessionId = sessionId;
        // The default namespace is always reachable: unqualified calls land
        // there, so it must pass the allowlist even if the configured list omits it.
        AllowedNamespaces = new HashSet<string>(allowedNamespaces, StringComparer.Ordinal) { defaultNamespace };
    }

    /// <summary>Single-namespace context: default is the only allowed namespace.</summary>
    public MemoryContext(string agentId, string @namespace, string sessionId)
        : this(agentId, @namespace, [@namespace], sessionId)
    {
    }

    public bool IsNamespaceAllowed(string @namespace) => AllowedNamespaces.Contains(@namespace);

    public static MemoryContext FromOptions(MemSrvOptions options) =>
        new(options.AgentId, options.Namespace, options.AllowedNamespaces, options.SessionId);
}
