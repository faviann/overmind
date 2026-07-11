using System.Security.Claims;
using System.Text.Encodings.Web;
using MemSrv.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemSrv.Server;

/// <summary>
/// Authenticates each request against the static bearer-key store. A missing or
/// unknown key fails authentication so <c>RequireAuthorization</c> rejects it
/// with 401 before any MCP tool runs — the server stays the only door. A
/// resolved key becomes a principal carrying the agent identity, default
/// namespace, and namespace allowlist that <see cref="MemoryContextResolver"/>
/// turns into the per-session request context.
/// </summary>
public sealed class BearerKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AgentBearer";

    // Claim types the resolver reads back off the authenticated principal.
    public const string AgentIdClaim = ClaimTypes.NameIdentifier;
    public const string DefaultNamespaceClaim = "memsrv:default_namespace";
    public const string AllowedNamespaceClaim = "memsrv:allowed_namespace";

    private const string BearerPrefix = "Bearer ";

    private readonly AgentKeyStore _keys;

    public BearerKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AgentKeyStore keys)
        : base(options, logger, encoder)
    {
        _keys = keys;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string presentedKey = header[BearerPrefix.Length..].Trim();
        if (presentedKey.Length == 0 || !_keys.TryResolve(presentedKey, out AgentKey agentKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown bearer key."));
        }

        // Defense in depth: the loader rejects malformed entries, but the store
        // can also be constructed directly, so never mint an identity-less
        // principal (which would authenticate into agent_id "" / namespace "").
        string? invalid = AgentKeyStore.ValidationError(
            agentKey.Key, agentKey.AgentId, agentKey.DefaultNamespace, agentKey.AllowedNamespaces);
        if (invalid is not null)
        {
            return Task.FromResult(AuthenticateResult.Fail($"Bearer key is not usable: {invalid}."));
        }

        var claims = new List<Claim>
        {
            new(AgentIdClaim, agentKey.AgentId),
            new(DefaultNamespaceClaim, agentKey.DefaultNamespace),
        };
        claims.AddRange(agentKey.AllowedNamespaces.Select(ns => new Claim(AllowedNamespaceClaim, ns)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
