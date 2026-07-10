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

    // HTTP transport (default mode). AgentKeysPath points at the
    // Ansible-provisioned bearer-key YAML; HttpUrl is the Kestrel bind address.
    public string AgentKeysPath { get; set; } = "";
    public string HttpUrl { get; set; } = "http://0.0.0.0:8080";

    // "stdio" selects the local stdio transport; anything else (default) is HTTP.
    public string Transport { get; set; } = "http";
}
