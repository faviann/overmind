using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;

namespace MemSrv.Server;

public static class CaptureConsoleAuthentication
{
    public const string CookieScheme = "CaptureConsoleCookie";
    public const string OidcScheme = "CaptureConsoleOidc";
    public const string OperatorPolicy = "CaptureConsoleOperator";
    public const string McpPolicy = "McpAgentBearer";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
}

public sealed record CaptureConsoleOperator(string ProviderSubject);

/// <summary>
/// The single server-owned authorization seam for capture-console actions.
/// Operator identity is derived only from the OIDC-authenticated principal;
/// action callers never provide it as input.
/// </summary>
public sealed class CaptureConsoleOperatorAuthorization(IHttpContextAccessor contexts)
{
    public CaptureConsoleOperator RequireOperator()
    {
        ClaimsPrincipal? principal = contexts.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("An authenticated console operator is required.");
        }

        string? subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new UnauthorizedAccessException("The OIDC provider subject is required.");
        }

        return new CaptureConsoleOperator(subject);
    }
}
