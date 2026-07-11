using System.Security.Claims;
using MemSrv.Core;
using Microsoft.AspNetCore.Http;

namespace MemSrv.Server;

/// <summary>
/// Builds the per-request <see cref="MemoryContext"/> for HTTP mode: the agent
/// identity and namespace allowlist come from the bearer-key principal, and the
/// session id is transport-derived — the MCP protocol session id the SDK routes
/// on (<c>Mcp-Session-Id</c>). One MCP session therefore maps to one trace
/// session with zero agent cooperation, so server-side auto-logging
/// (e.g. <c>memory_consumed</c>) always joins against the caller's session.
/// </summary>
public sealed class MemoryContextResolver(IHttpContextAccessor accessor)
{
    private const string SessionIdHeader = "Mcp-Session-Id";

    public MemoryContext Resolve()
    {
        HttpContext http = accessor.HttpContext
            ?? throw new InvalidOperationException("No active HTTP request; per-session context is unavailable.");

        ClaimsPrincipal user = http.User;
        string? agentId = user.FindFirstValue(BearerKeyAuthenticationHandler.AgentIdClaim);
        string? defaultNamespace = user.FindFirstValue(BearerKeyAuthenticationHandler.DefaultNamespaceClaim);
        // Empty, not just null: an identity-less principal must never reach a tool.
        if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(defaultNamespace))
        {
            throw new InvalidOperationException("Request is not authenticated with a bearer-key principal.");
        }

        string[] allowedNamespaces = user
            .FindAll(BearerKeyAuthenticationHandler.AllowedNamespaceClaim)
            .Select(claim => claim.Value)
            .ToArray();

        string sessionId = http.Request.Headers[SessionIdHeader].ToString();
        if (string.IsNullOrEmpty(sessionId))
        {
            // In stateful streamable-HTTP mode the SDK itself routes tool calls by
            // this header, so its absence means a request that never should have
            // reached a tool. Fail loudly rather than silently mis-scope a session.
            throw new InvalidOperationException("Request carries no Mcp-Session-Id; transport session is unavailable.");
        }

        return new MemoryContext(agentId, defaultNamespace, allowedNamespaces, sessionId);
    }
}
