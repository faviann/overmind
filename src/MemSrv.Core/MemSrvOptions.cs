namespace MemSrv.Core;

public sealed class MemSrvOptions
{
    public string ConnectionString { get; set; } = "";
    public string AdminConnectionString { get; set; } = "";
    public string AgentId { get; set; } = "local-agent";
    public string Namespace { get; set; } = "memory-system";
    public string SessionId { get; set; } = "local-session";
    public string[] AllowedNamespaces { get; set; } = [];
    public string NeverStorePath { get; set; } = "config/never_store.yaml";
}
