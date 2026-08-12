namespace MemSrv.Core;

public sealed class MemSrvOptions
{
    public string ConnectionString { get; set; } = "";
    public string AdminConnectionString { get; set; } = "";
    public string AgentId { get; set; } = "local-agent";
    public string Namespace { get; set; } = "memory-system";
    // Unconfigured runs get a fresh unique session id per process start —
    // options are loaded once at startup — so distinct runs never collapse
    // into one trace session. MEMSRV_SESSION_ID (or MemSrv:SessionId) still
    // wins when set.
    public string SessionId { get; set; } = $"local-{Guid.NewGuid():N}";
    public string[] AllowedNamespaces { get; set; } = [];
    public string NeverStorePath { get; set; } = "config/never_store.yaml";

    // Operator-owned exact credential values, one per line, outside the
    // tracked rule file so a real value never enters git. Absent or empty is
    // valid and is NOT a fail-closed condition; only the rule file must load.
    public string NeverStoreLiteralsPath { get; set; } = "";

    // HTTP transport (default mode). AgentKeysPath points at the
    // Ansible-provisioned bearer-key YAML; HttpUrl is the Kestrel bind address.
    public string AgentKeysPath { get; set; } = "";
    public string HttpUrl { get; set; } = "http://0.0.0.0:8080";

    // Interactive capture-console operator identity. This is deliberately
    // separate from both agent bearer keys and capture credentials.
    public CaptureConsoleOidcOptions CaptureConsoleOidc { get; set; } = new();

    // "stdio" selects the local stdio transport; anything else (default) is HTTP.
    public string Transport { get; set; } = "http";
}

public sealed class CaptureConsoleOidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public void Validate()
    {
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            || authority.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Capture-console OIDC authority must be an absolute HTTPS URI.");
        }
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("Capture-console OIDC client id is required.");
        }
        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("Capture-console OIDC client secret is required.");
        }
    }
}
