using MemSrv.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(keyStore);
        builder.Services.AddSingleton(_ => new NeverStoreGate(options.NeverStorePath));
        builder.Services.AddSingleton(provider =>
            new MemoryService(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));

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

        app.MapMcp("/mcp").RequireAuthorization();

        return app;
    }
}
