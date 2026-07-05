using MemSrv.Core;
using MemSrv.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var options = Configuration.Load(Directory.GetCurrentDirectory());

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(MemoryContext.FromOptions(options));
builder.Services.AddSingleton(_ => new NeverStoreGate(options.NeverStorePath));
builder.Services.AddSingleton(provider => new MemoryService(options.ConnectionString, provider.GetRequiredService<NeverStoreGate>()));

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<McpMemoryTools>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole(console =>
{
    console.LogToStandardErrorThreshold = LogLevel.Trace;
});

await builder.Build().RunAsync();
