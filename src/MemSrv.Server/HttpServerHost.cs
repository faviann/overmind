using MemSrv.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public static WebApplication Build(MemSrvOptions options, AgentKeyStore keyStore)
    {
        var builder = WebApplication.CreateBuilder();

        // AGENTS.md: never log to stdout. WebApplication's default console
        // provider writes to stdout; keep every log line (Kestrel startup
        // included) on stderr so stdout stays clean, matching the stdio host.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(keyStore);
        builder.Services.AddSingleton(_ => new NeverStoreGate(options.NeverStorePath));
        builder.Services.AddSingleton(provider =>
            new MemoryService(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));
        builder.Services.AddSingleton(provider =>
            new CaptureService(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<MemoryContextResolver>();
        // Per MCP session: identity from the bearer key, session id from transport.
        builder.Services.AddScoped(provider =>
            provider.GetRequiredService<MemoryContextResolver>().Resolve());

        builder.Services
            .AddAuthentication(BearerKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerKeyAuthenticationHandler>(
                BearerKeyAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services
            .AddMcpServer()
            // Stateful sessions are required: one MCP session = one trace session,
            // and the transport routes tool calls by the Mcp-Session-Id header.
            .WithHttpTransport(transport => transport.Stateless = false)
            .WithTools<McpMemoryTools>();

        var app = builder.Build();

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

        // Deliberately outside MCP authentication: capture credentials are a
        // separate capability and are resolved only by the capture service.
        // Read the body only after the credential has resolved, so unknown
        // credentials receive 401 before payload parsing or safety work.
        app.MapPost("/capture/v1/observations", async (HttpContext http, CaptureService capture) =>
        {
            string header = http.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(header[prefix.Length..]))
            {
                return Results.Unauthorized();
            }

            string credential = header[prefix.Length..].Trim();
            if (!await capture.IsCredentialKnownAsync(credential, http.RequestAborted))
            {
                return Results.Unauthorized();
            }

            CaptureObservationRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<CaptureObservationRequest>(
                    cancellationToken: http.RequestAborted);
                if (request is null)
                {
                    return Results.BadRequest(new { error = "A capture observation body is required." });
                }
                var receipt = await capture.ImportAsync(credential, request, http.RequestAborted);
                return receipt is null ? Results.Unauthorized() : Results.Ok(receipt);
            }
            catch (CaptureConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
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

        app.MapMcp("/mcp").RequireAuthorization();

        return app;
    }
}
