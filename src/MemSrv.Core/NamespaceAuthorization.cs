namespace MemSrv.Core;

internal static class NamespaceAuthorization
{
    // The namespace-authorization seam. Every path that reaches a namespace —
    // qualified writes, unqualified defaults, cross-namespace search, and reads
    // by uuid — is gated by AuthorizeNamespace against the context's allowlist,
    // so request identity meets namespace access in one service-layer function.
    // That single policy point is what keeps the north-star RLS retrofit cheap.
    internal static string ResolveNamespace(MemoryContext context, string? requested)
    {
        var @namespace = requested ?? context.DefaultNamespace;
        AuthorizeNamespace(context, @namespace);
        return @namespace;
    }

    internal static void AuthorizeNamespace(MemoryContext context, string @namespace)
    {
        if (!context.IsNamespaceAllowed(@namespace))
        {
            throw new NamespaceForbiddenException(@namespace, context.AgentId);
        }
    }
}
