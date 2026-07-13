using Dapper;
using MemSrv.Core;
using MemSrv.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;
using System.Diagnostics;
using System.Text.Json;

namespace MemSrv.Tests;

// The shared HTTP seam harness: a fully-hosted in-process HTTP server (real
// Postgres, a test key file) exercised by real keyed agents speaking
// MCP-over-HTTP, plus memctl run as a subprocess (the operator seam). Test
// classes inherit this and assert through the public surface only: MCP tools
// for agent actions, memctl for operator-visible state. No direct database
// reads — with one sanctioned exception (docs/testing.md): mechanical tests
// where the database mechanism itself (grants, triggers, content hashes) is
// the spec'd behavior may connect directly.
public abstract class HttpSeamTestBase : IAsyncLifetime
{
    protected static string AdminConnection => TestDatabase.AdminConnection;
    protected static string RuntimeConnection => TestDatabase.RuntimeConnection;

    // agent-a reaches memory-system (default) and homelab; agent-b is confined to
    // memory-system so foreign-namespace calls can be rejected.
    protected const string AgentAKey = "agent-a-key-1234567890";
    protected const string ScopedKey = "agent-b-key-0987654321";

    protected readonly string _root = FindRepoRoot();
    protected WebApplication _app = null!;
    protected string _baseUrl = "";
    protected string _keysPath = "";

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(AdminConnection))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
            await connection.ExecuteAsync("GRANT ALL ON SCHEMA public TO overmind;");
            DatabaseMigrator.Migrate(AdminConnection, Path.Combine(_root, "migrations"), logToConsole: false);
        }

        _keysPath = Path.Combine(Path.GetTempPath(), $"memsrv-keys-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(_keysPath, KeyFileYaml());

        _app = HttpServerHost.Build(RuntimeOptions(), AgentKeyStore.Load(_keysPath));
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (File.Exists(_keysPath))
        {
            File.Delete(_keysPath);
        }
    }

    protected MemSrvOptions RuntimeOptions() => new()
    {
        ConnectionString = RuntimeConnection,
        NeverStorePath = Path.Combine(_root, "config/never_store.yaml"),
    };

    protected async Task<McpClient> ConnectAsync(string bearerKey)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{_baseUrl}/mcp"),
            Name = "MemSrv.Server",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerKey}" }
        });
        return await McpClient.CreateAsync(transport);
    }

    // --- memctl as a subprocess (the operator seam) ---

    // Runs a memctl command (the sanctioned operator seam), asserts it succeeded,
    // and returns its stdout so tests can observe operator-visible state instead of
    // reading the database directly.
    protected async Task<string> RunMemCtlAsync(params string[] args)
    {
        var (exitCode, stdout, stderr) = await RunMemCtlForResultAsync(null, args);
        Assert.True(exitCode == 0, $"memctl {string.Join(' ', args)} failed with exit {exitCode}. stdout={stdout} stderr={stderr}");
        return stdout;
    }

    // The failure-tolerant core: returns the exit code and both streams so tests
    // can assert on memctl refusals too; extraEnvironment lets a test inject
    // process environment such as a stub $EDITOR.
    protected async Task<(int ExitCode, string Stdout, string Stderr)> RunMemCtlForResultAsync(
        IReadOnlyDictionary<string, string>? extraEnvironment, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "src/MemCtl/MemCtl.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.Environment["MEMSRV_CONNECTION_STRING"] = RuntimeConnection;
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start memctl.");
        // Drain both streams concurrently so a full pipe buffer can't deadlock the
        // process, and bound the wait so a hung memctl fails the test instead of
        // hanging the suite.
        var stdoutPump = process.StandardOutput.ReadToEndAsync();
        var stderrPump = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutPump, stderrPump);
            throw new Xunit.Sdk.XunitException($"memctl {string.Join(' ', args)} did not exit within 60s.");
        }

        return (process.ExitCode, await stdoutPump, await stderrPump);
    }

    protected static async Task<JsonElement> CallToolAsync(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(toolName, arguments);
        var content = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, $"{toolName} returned MCP error: {content}");

        var json = result.StructuredContent ?? JsonDocument.Parse(((TextContentBlock)Assert.Single(result.Content)).Text).RootElement;
        return json.Clone();
    }

    protected static void AssertNext(JsonElement envelope)
    {
        Assert.True(envelope.TryGetProperty("next", out var next), "Tool response did not include next.");
        Assert.False(string.IsNullOrWhiteSpace(next.GetString()));
    }

    private static string KeyFileYaml() =>
        $"""
        keys:
          - key: {AgentAKey}
            agent_id: agent-a
            default_namespace: memory-system
            allowed_namespaces: [memory-system, homelab]
          - key: {ScopedKey}
            agent_id: agent-b
            default_namespace: memory-system
            allowed_namespaces: [memory-system]
        """;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repo root.");
    }
}
