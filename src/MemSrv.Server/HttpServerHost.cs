using MemSrv.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text.Json;

namespace MemSrv.Server;

/// <summary>
/// Builds the ASP.NET Core host for HTTP mode: bearer-authenticated streamable
/// MCP at <c>/mcp</c> and an unauthenticated <c>/healthz</c> gated on a database
/// ping. Callers own the bind address and lifetime, so the same builder serves
/// both production (<c>0.0.0.0:8080</c>) and in-process tests (loopback).
/// </summary>
public static class HttpServerHost
{
    // A denial-of-service guard for the disabled tracer route, deliberately
    // three orders of magnitude below the versioned 128 MiB scanner
    // observation budget: an unauthenticated client must not be able to make
    // the server allocate a scanner-sized buffer. See
    // docs/capture-safety-budgets.md, "Why the transport cap is below the
    // scanner limit".
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static WebApplication Build(MemSrvOptions options, AgentKeyStore keyStore)
    {
        bool captureConsoleEnabled = options.CaptureConsoleOidc.ValidateAndIsEnabled();
        var builder = WebApplication.CreateBuilder();

        // AGENTS.md: never log to stdout. WebApplication's default console
        // provider writes to stdout; keep every log line (Kestrel startup
        // included) on stderr so stdout stays clean, matching the stdio host.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(keyStore);
        builder.Services.AddSingleton(_ => new NeverStoreGate(options.NeverStorePath, options.NeverStoreLiteralsPath));
        builder.Services.AddSingleton(provider =>
            new MemoryService(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));
        builder.Services.AddSingleton(_ => new CaptureAuthority(options.ConnectionString));
        builder.Services.AddSingleton(provider =>
            new CaptureIngestion(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));

        builder.Services.AddHttpContextAccessor();
        builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            // Traefik owns TLS. Trust exactly one forwarded scheme hop so OIDC
            // generates the external HTTPS callback without changing routing.
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });
        builder.Services.AddScoped<CaptureConsoleOperatorAuthorization>();
        builder.Services.AddSingleton<MemoryContextResolver>();
        // Per MCP session: identity from the bearer key, session id from transport.
        builder.Services.AddScoped(provider =>
            provider.GetRequiredService<MemoryContextResolver>().Resolve());

        AuthenticationBuilder authentication = builder.Services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BearerKeyAuthenticationHandler>(
                BearerKeyAuthenticationHandler.SchemeName, _ => { });
        if (captureConsoleEnabled)
        {
            authentication.AddCookie(CaptureConsoleAuthentication.CookieScheme, cookie =>
            {
                cookie.ForwardChallenge = CaptureConsoleAuthentication.OidcScheme;
                cookie.Cookie.Name = "__Secure-MemSrv-CaptureConsole";
                cookie.Cookie.Path = "/capture/console";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.ExpireTimeSpan = CaptureConsoleAuthentication.SessionLifetime;
                cookie.SlidingExpiration = false;
            })
            .AddOpenIdConnect(CaptureConsoleAuthentication.OidcScheme, oidc =>
            {
                oidc.Authority = options.CaptureConsoleOidc.Authority;
                oidc.ClientId = options.CaptureConsoleOidc.ClientId;
                oidc.ClientSecret = options.CaptureConsoleOidc.ClientSecret;
                oidc.CallbackPath = "/capture/console/signin-oidc";
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.MapInboundClaims = false;
                oidc.SaveTokens = false;
                oidc.SignInScheme = CaptureConsoleAuthentication.CookieScheme;
                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "sub",
                };
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
            });
        }
        builder.Services.AddAuthorization(authorization =>
        {
            if (captureConsoleEnabled)
            {
                authorization.AddPolicy(CaptureConsoleAuthentication.OperatorPolicy, policy =>
                {
                    policy.AuthenticationSchemes.Add(CaptureConsoleAuthentication.CookieScheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim("sub");
                });
            }
            authorization.AddPolicy(CaptureConsoleAuthentication.McpPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add(BearerKeyAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
        });

        builder.Services
            .AddMcpServer()
            // Stateful sessions are required: one MCP session = one trace session,
            // and the transport routes tool calls by the Mcp-Session-Id header.
            .WithHttpTransport(transport => transport.Stateless = false)
            .WithTools<McpMemoryTools>();

        var app = builder.Build();

        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();

        // Unauthenticated: compose healthchecks and monitoring must see real DB
        // outages, so a 200 requires SELECT 1 to answer within ~2s.
        app.MapGet("/healthz", async (MemoryService memory, HttpContext http) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            bool healthy = await memory.PingAsync(timeout.Token);
            return healthy ? Results.Ok("ok") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        // The focused console foundation intentionally exposes no business
        // operator actions yet. Even this entry action goes through the one
        // identity seam that derives the provider subject from the OIDC cookie.
        if (captureConsoleEnabled)
        {
            app.MapGet("/capture/console", (CaptureConsoleOperatorAuthorization authorization) =>
            {
                CaptureConsoleOperator @operator = authorization.RequireOperator();
                string subject = System.Net.WebUtility.HtmlEncode(@operator.ProviderSubject);
                return Results.Content(
                    $"<main><h1>Capture console</h1><p>Operator: {subject}</p></main>",
                    "text/html");
            }).RequireAuthorization(CaptureConsoleAuthentication.OperatorPolicy);
        }

        // Deliberately outside MCP authentication: capture credentials are a
        // separate capability resolved only by CaptureAuthority. Capture
        // authority resolves first, so an unknown credential receives 401
        // before the body is read, parsed, or scanned; ingestion then receives
        // the one authenticated binding context rather than the raw credential.
        app.MapPost("/capture/v1/observations", async (
            HttpContext http, CaptureAuthority authority, CaptureIngestion ingestion) =>
        {
            string header = http.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(header[prefix.Length..]))
            {
                return Results.Unauthorized();
            }

            string credential = header[prefix.Length..].Trim();
            var binding = await authority.ResolveAsync(credential, http.RequestAborted);
            if (binding is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                byte[]? body = await ReadCaptureBodyAsync(
                    http.Request, http.RequestAborted);
                if (body is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                var request = JsonSerializer.Deserialize<CaptureObservationRequest>(
                    body, JsonOptions);
                if (request is null)
                {
                    return Results.BadRequest(new { error = "A capture observation body is required." });
                }
                // Locator variants are validated here, at the wire seam; past
                // this point only the closed internal representation exists.
                var command = CaptureObservationCommand.FromRequest(request);
                var receipt = await ingestion.ImportAsync(binding, command, http.RequestAborted);
                return Results.Ok(receipt);
            }
            catch (CaptureConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message, reason = ex.Reason });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SafetyConfigurationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, outcome = ex.Outcome });
            }
            catch (SafetyScanException ex)
            {
                return Results.BadRequest(new { error = ex.Message, outcome = ex.Outcome });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.BadRequest(new { error = "Capture event identities must be unique." });
            }
        });

        app.MapMcp("/mcp").RequireAuthorization(CaptureConsoleAuthentication.McpPolicy);

        return app;
    }

    private static async Task<byte[]?> ReadCaptureBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > CaptureFidelityPolicy.ProductionTransportBytes)
        {
            return null;
        }

        byte[] buffer = new byte[CaptureFidelityPolicy.ProductionTransportBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await request.Body.ReadAsync(
                buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return buffer[..length];
            }
            length += read;
        }
        return null;
    }
}
