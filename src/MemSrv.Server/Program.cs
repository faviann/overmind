using MemSrv.Core;
using MemSrv.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = Configuration.Load(Directory.GetCurrentDirectory());

// stdio for local agents (unchanged); HTTP by default for LAN agents.
bool stdio = args.Contains("--stdio") ||
    string.Equals(options.Transport, "stdio", StringComparison.OrdinalIgnoreCase);

if (stdio)
{
    await RunStdioAsync(options);
}
else
{
    var keyStore = AgentKeyStore.Load(options.AgentKeysPath);
    var app = HttpServerHost.Build(options, keyStore);
    await app.RunAsync(options.HttpUrl);
}

static async Task RunStdioAsync(MemSrvOptions options)
{
    var builder = Host.CreateApplicationBuilder();

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(MemoryContext.FromOptions(options));
    builder.Services.AddSingleton(_ => new NeverStoreGate(options.NeverStorePath));
    builder.Services.AddSingleton(provider =>
        new MemoryService(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));

    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<McpMemoryTools>();

    // stdio speaks JSON-RPC on stdout; keep every log line on stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(console =>
    {
        console.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    await builder.Build().RunAsync();
}
