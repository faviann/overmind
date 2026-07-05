namespace MemSrv.Core;

public sealed record MemoryContext(string AgentId, string Namespace, string SessionId)
{
    public static MemoryContext FromOptions(MemSrvOptions options) =>
        new(options.AgentId, options.Namespace, options.SessionId);
}
