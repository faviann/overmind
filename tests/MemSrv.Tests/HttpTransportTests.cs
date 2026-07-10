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
using System.Net;
using System.Text.Json;

namespace MemSrv.Tests;

// The new seam: a fully-hosted in-process HTTP server (real Postgres, a test key
// file) exercised by real keyed agents speaking MCP-over-HTTP. Everything the
// HTTP transport adds — bearer auth, allowlist enforcement, default-namespace
// behavior, transport-derived sessions, /healthz — is asserted here.
[Collection("database")]
public sealed class HttpTransportTests : IAsyncLifetime
{
    private const string AdminConnection = "Host=127.0.0.1;Port=55432;Database=memory_test;Username=overmind;Password=overmind_dev";
    private const string RuntimeConnection = "Host=127.0.0.1;Port=55432;Database=memory_test;Username=memsrv;Password=memsrv_dev";

    // agent-a reaches memory-system (default) and homelab; agent-b is confined to
    // memory-system so foreign-namespace calls can be rejected.
    private const string AgentAKey = "agent-a-key-1234567890";
    private const string ScopedKey = "agent-b-key-0987654321";
    private const string UnknownKey = "not-a-real-key";

    private readonly string _root = FindRepoRoot();
    private WebApplication _app = null!;
    private string _baseUrl = "";
    private string _keysPath = "";

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(AdminConnection))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
            await connection.ExecuteAsync("GRANT ALL ON SCHEMA public TO overmind;");
            DatabaseMigrator.Migrate(AdminConnection, Path.Combine(_root, "migrations"), logToConsole: false);
            await connection.ExecuteAsync("ALTER ROLE memsrv LOGIN PASSWORD 'memsrv_dev';");
        }

        _keysPath = Path.Combine(Path.GetTempPath(), $"memsrv-keys-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(_keysPath, KeyFileYaml());

        var options = new MemSrvOptions
        {
            ConnectionString = RuntimeConnection,
            NeverStorePath = Path.Combine(_root, "config/never_store.yaml"),
        };
        _app = HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath));
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

    [Fact]
    public async Task KeyedHttpAgentRoundTripsAllFourTools()
    {
        var (seed, term) = await SeedApprovedMemoryAsync("memory-system");

        await using var client = await ConnectAsync(AgentAKey);

        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = "http-roundtrip-trace",
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = "http round trip" }
        });
        AssertNext(trace);

        var proposed = await CallToolAsync(client, "propose_memory", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "decision",
            ["content"] = $"http proposal {Guid.NewGuid():N}",
            ["source_type"] = "trace",
            ["source_id"] = trace.GetProperty("data").GetProperty("traceUuid").GetGuid().ToString()
        });
        Assert.Equal("proposed", proposed.GetProperty("data").GetProperty("status").GetString());
        AssertNext(proposed);

        var search = await CallToolAsync(client, "search_memory", new Dictionary<string, object?>
        {
            ["query"] = term,
            ["limit"] = 5
        });
        AssertNext(search);
        Assert.Contains(search.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == seed);

        var fetched = await CallToolAsync(client, "get_by_id", new Dictionary<string, object?>
        {
            ["uuid"] = seed
        });
        AssertNext(fetched);
        Assert.Equal(seed, fetched.GetProperty("data").GetProperty("uuid").GetGuid());
    }

    [Fact]
    public async Task GetByIdAutoLogsConsumedUnderTransportSession()
    {
        var (seed, _) = await SeedApprovedMemoryAsync("memory-system");

        await using var client = await ConnectAsync(AgentAKey);
        await CallToolAsync(client, "get_by_id", new Dictionary<string, object?> { ["uuid"] = seed });

        // The agent never sent a session id for get_by_id; the consumed event is
        // logged under the transport-derived MCP session id, joinable with zero
        // agent cooperation.
        var sessionId = client.SessionId!;
        Assert.False(string.IsNullOrEmpty(sessionId));
        var traceRows = await Service().TraceAsync(sessionId);
        Assert.Contains(traceRows, row => row.EventType == "memory_consumed" && row.Refs?.Contains(seed) == true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UnknownKey)]
    public async Task MissingOrUnknownBearerKeyIsRejectedWith401(string? bearerKey)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (bearerKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearerKey}");
        }

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ForeignNamespaceCallIsRejectedAndNothingLands()
    {
        var marker = $"foreign-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(ScopedKey);

        // agent-b's allowlist is memory-system only; homelab is foreign.
        var result = await client.CallToolAsync("propose_memory", new Dictionary<string, object?>
        {
            ["namespace"] = "homelab",
            ["type"] = "fact",
            ["content"] = $"should never land in a foreign namespace {marker}",
            ["source_type"] = "human",
            ["source_id"] = "test-source"
        });

        Assert.True(result.IsError == true);

        // Server-side enforcement: nothing reached the foreign namespace. This is
        // a mechanical DB-level read-back (permitted by docs/testing.md for
        // namespace-isolation checks), reading as an agent allowed in homelab.
        var homelabReader = new MemoryContext("seeder", "homelab", new[] { "homelab" }, $"verify-{Guid.NewGuid():N}");
        var found = await Service().SearchMemoryAsync(homelabReader, marker, namespaces: new[] { "homelab" });
        Assert.Empty(found.Data);
    }

    [Fact]
    public async Task UnqualifiedCallLandsInDefaultNamespace()
    {
        var sessionId = $"http-default-ns-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["event_type"] = "note",
            ["content"] = new { ok = true }
        });

        var uuid = trace.GetProperty("data").GetProperty("traceUuid").GetGuid();
        var row = Assert.Single(await Service().TraceAsync(sessionId), r => r.TraceUuid == uuid);
        Assert.Equal("memory-system", row.Namespace);
    }

    [Fact]
    public async Task HealthzReturns200WithDbUpAndNeedsNoAuth()
    {
        using var http = new HttpClient();

        using var response = await http.GetAsync($"{_baseUrl}/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthzReturnsNon200WhenDbUnreachable()
    {
        // A throwaway host pointed at a dead database: /healthz must reflect the
        // real outage, not just process liveness.
        var deadOptions = new MemSrvOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Password=nope;Timeout=1",
            NeverStorePath = Path.Combine(_root, "config/never_store.yaml")
        };
        var deadApp = HttpServerHost.Build(deadOptions, AgentKeyStore.Load(_keysPath));
        deadApp.Urls.Add("http://127.0.0.1:0");
        await deadApp.StartAsync();
        try
        {
            var url = deadApp.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient();

            using var response = await http.GetAsync($"{url}/healthz");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await deadApp.StopAsync();
            await deadApp.DisposeAsync();
        }
    }

    private async Task<McpClient> ConnectAsync(string bearerKey)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{_baseUrl}/mcp"),
            Name = "MemSrv.Server",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerKey}" }
        });
        return await McpClient.CreateAsync(transport);
    }

    private async Task<(Guid Uuid, string Term)> SeedApprovedMemoryAsync(string @namespace)
    {
        var term = $"httpseed-{Guid.NewGuid():N}";
        var service = Service();
        var context = new MemoryContext("seeder", @namespace, new[] { @namespace }, $"seed-{Guid.NewGuid():N}");
        var proposed = await service.ProposeMemoryAsync(context, @namespace, "fact", $"Seeded memory {term}", "human", "test-source");
        await service.ApproveAsync(proposed.Data.Uuid, "test-operator");
        return (proposed.Data.Uuid, term);
    }

    private MemoryService Service() =>
        new(RuntimeConnection, new NeverStoreGate(Path.Combine(_root, "config/never_store.yaml")));

    private static async Task<JsonElement> CallToolAsync(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(toolName, arguments);
        var content = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, $"{toolName} returned MCP error: {content}");

        var json = result.StructuredContent ?? JsonDocument.Parse(((TextContentBlock)Assert.Single(result.Content)).Text).RootElement;
        return json.Clone();
    }

    private static void AssertNext(JsonElement envelope)
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
