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
        builder.Services.AddSingleton(provider => new CapturePairing(
            options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));
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

        // Pairing request creation and polling are deliberately outside every
        // existing authority. The undisplayed polling token is the sole
        // capability and can deliver the capture credential exactly once.
        app.MapPost("/capture/v1/pairing-requests", async (
            CapturePairingCreateRequest request, CapturePairing pairing, HttpContext http) =>
        {
            try
            {
                CapturePairingRequestCreated created = await pairing.CreateAsync(
                    request.MachineName,
                    request.CodexInstallationId,
                    "/capture/console/pair",
                    http.RequestAborted);
                return Results.Created($"/capture/v1/pairing-requests/{created.RequestId}", created);
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException or SafetyConfigurationException
                or SafetyScanException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/capture/v1/pairing-requests/{requestId:guid}", async (
            Guid requestId, CapturePairing pairing, HttpContext http) =>
        {
            string? token = Bearer(http);
            CapturePairingPoll? poll = token is null
                ? null
                : await pairing.PollAsync(requestId, token, http.RequestAborted);
            if (poll is null) return Results.Unauthorized();
            if (poll.Status == "gone") return Results.StatusCode(StatusCodes.Status410Gone);
            return Results.Ok(poll);
        });

        app.MapDelete("/capture/v1/pairing-requests/{requestId:guid}", async (
            Guid requestId, CapturePairing pairing, HttpContext http) =>
        {
            string? token = Bearer(http);
            if (token is null) return Results.Unauthorized();
            return await pairing.CancelAsync(requestId, token, null, http.RequestAborted)
                ? Results.NoContent()
                : Results.StatusCode(StatusCodes.Status410Gone);
        });

        // The focused console foundation intentionally exposes no business
        // operator actions yet. This focused entry route goes through the one
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

            app.MapGet("/capture/console/pair/{userCode}", async (
                string userCode, CapturePairing pairing,
                CaptureConsoleOperatorAuthorization authorization, HttpContext http) =>
            {
                CaptureConsoleOperator @operator = authorization.RequireOperator();
                CapturePairingRequestView? request = await pairing.InspectByUserCodeAsync(
                    userCode, http.RequestAborted);
                if (request is null) return Results.NotFound();
                string status = request.ExpiresAt <= DateTimeOffset.UtcNow
                    && request.Status == "pending" ? "expired" : request.Status;
                string code = System.Net.WebUtility.HtmlEncode(request.UserCode);
                string detectedMachine = System.Net.WebUtility.HtmlEncode(request.MachineName);
                string detectedInstallation = System.Net.WebUtility.HtmlEncode(
                    request.CodexInstallationId);
                string subject = System.Net.WebUtility.HtmlEncode(@operator.ProviderSubject);
                string form = status == "pending"
                    ? $"""
                       <form method="post" action="/capture/console/pair/{code}/approve">
                         <label>Label <input name="label" required></label>
                         <label>Allowed repository route patterns
                           <textarea name="allowedRepositoryPatterns" required></textarea>
                         </label>
                         <label>Special mappings (one alias=namespace per line)
                           <textarea name="specialMappings"></textarea>
                         </label>
                         <button type="submit">Approve</button>
                       </form>
                       """
                    : "";
                return Results.Content(
                    $"""
                    <main><h1>Pair Codex capture</h1>
                    <p>Operator: {subject}</p><p>Status: {System.Net.WebUtility.HtmlEncode(status)}</p>
                    <dl><dt>Detected machine</dt><dd>{detectedMachine}</dd>
                    <dt>Detected Codex installation</dt><dd>{detectedInstallation}</dd></dl>
                    {form}</main>
                    """, "text/html");
            }).RequireAuthorization(CaptureConsoleAuthentication.OperatorPolicy);

            app.MapPost("/capture/console/pair/{userCode}/approve", async (
                string userCode, CapturePairing pairing,
                CaptureConsoleOperatorAuthorization authorization, HttpContext http) =>
            {
                try
                {
                    CaptureConsoleOperator @operator = authorization.RequireOperator();
                    CapturePairingRequestView? request = await pairing.InspectByUserCodeAsync(
                        userCode, http.RequestAborted);
                    if (request is null) return Results.NotFound();
                    IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted);
                    CapturePairingApproval approval = PairingApprovalFromForm(form);
                    CapturePairingApproved result = await pairing.ApproveAsync(
                        request.RequestId, @operator.ProviderSubject, approval, http.RequestAborted);
                    string label = System.Net.WebUtility.HtmlEncode(result.Label);
                    return Results.Content(
                        $"<main><h1>Pairing approved</h1><p>{label}</p></main>",
                        "text/html");
                }
                catch (CapturePairingConflictException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    return Results.Conflict(new { error = "This Codex installation is already paired." });
                }
                catch (Exception ex) when (ex is ArgumentException
                    or InvalidOperationException or SafetyConfigurationException
                    or SafetyScanException)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).DisableAntiforgery()
              .RequireAuthorization(CaptureConsoleAuthentication.OperatorPolicy);

            app.MapGet("/capture/console/api/pairing/{requestId:guid}", async (
                Guid requestId, CapturePairing pairing,
                CaptureConsoleOperatorAuthorization authorization, HttpContext http) =>
            {
                _ = authorization.RequireOperator();
                CapturePairingRequestView? request = await pairing.InspectAsync(
                    requestId, http.RequestAborted);
                return request is null ? Results.NotFound() : Results.Ok(request);
            }).RequireAuthorization(CaptureConsoleAuthentication.OperatorPolicy);

            app.MapPost("/capture/console/api/pairing/{requestId:guid}/approve", async (
                Guid requestId, CapturePairingApproval approval, CapturePairing pairing,
                CaptureConsoleOperatorAuthorization authorization, HttpContext http) =>
            {
                try
                {
                    CaptureConsoleOperator @operator = authorization.RequireOperator();
                    CapturePairingApproved result = await pairing.ApproveAsync(
                        requestId, @operator.ProviderSubject, approval, http.RequestAborted);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (CapturePairingConflictException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    return Results.Conflict(new { error = "This Codex installation is already paired." });
                }
                catch (Exception ex) when (ex is ArgumentException
                    or InvalidOperationException or SafetyConfigurationException
                    or SafetyScanException)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequireAuthorization(CaptureConsoleAuthentication.OperatorPolicy);

            app.MapDelete("/capture/console/api/pairing/{requestId:guid}", async (
                Guid requestId, CapturePairing pairing,
                CaptureConsoleOperatorAuthorization authorization, HttpContext http) =>
            {
                CaptureConsoleOperator @operator = authorization.RequireOperator();
                return await pairing.CancelAsync(
                    requestId, null, @operator.ProviderSubject, http.RequestAborted)
                    ? Results.NoContent()
                    : Results.StatusCode(StatusCodes.Status410Gone);
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

    private static string? Bearer(HttpContext http)
    {
        string header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(header[prefix.Length..])
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static CapturePairingApproval PairingApprovalFromForm(IFormCollection form)
    {
        string[] patterns = form["allowedRepositoryPatterns"].ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CaptureSpecialNamespace[] mappings = form["specialMappings"].ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value =>
            {
                string[] parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException(
                        "Special mappings must use one alias=namespace pair per line.");
                return new CaptureSpecialNamespace(parts[0], parts[1]);
            }).ToArray();
        return new CapturePairingApproval(form["label"].ToString(), patterns, mappings);
    }

    private sealed record CapturePairingCreateRequest(
        string MachineName, string CodexInstallationId);
}
