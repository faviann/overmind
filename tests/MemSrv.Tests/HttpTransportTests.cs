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
using System.Net;
using System.Text.Json;

namespace MemSrv.Tests;

// The new seam: a fully-hosted in-process HTTP server (real Postgres, a test key
// file) exercised by real keyed agents speaking MCP-over-HTTP. Everything the
// HTTP transport adds — bearer auth, credential validation, allowlist
// enforcement, default-namespace behavior, transport-derived sessions, /healthz
// — is asserted here through the public surface only: MCP tools for agent
// actions, memctl for operator-visible state. No direct database reads.
[Collection("database")]
public sealed class HttpTransportTests : IAsyncLifetime
{
    private static string AdminConnection => TestDatabase.AdminConnection;
    private static string RuntimeConnection => TestDatabase.RuntimeConnection;

    // agent-a reaches memory-system (default) and homelab; agent-b is confined to
    // memory-system so foreign-namespace calls can be rejected.
    private const string AgentAKey = "agent-a-key-1234567890";
    private const string ScopedKey = "agent-b-key-0987654321";
    private const string UnknownKey = "not-a-real-key";

    private readonly string _root = TestProcessRunner.RepoRoot;
    private WebApplication _app = null!;
    private string _baseUrl = "";
    private string _keysPath = "";

    public async Task InitializeAsync()
    {
        await TestDatabase.PrepareClassDatabaseAsync(
            typeof(HttpTransportTests), Path.Combine(_root, "migrations"));

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

    [Fact]
    public async Task KeyedHttpAgentRunsFullMemoryLifecycleOnOneArtifact()
    {
        var term = $"lifecycle-{Guid.NewGuid():N}";
        var reviewer = $"reviewer-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        // 1. log_trace — the source the proposal will cite.
        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = "http lifecycle" }
        });
        AssertNext(trace);
        var traceUuid = trace.GetProperty("data").GetProperty("traceUuid").GetGuid();

        // 2. propose_memory — the one artifact the rest of the flow carries.
        var proposed = await CallToolAsync(client, "propose_memory", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "decision",
            ["content"] = $"http lifecycle memory {term}",
            ["source_type"] = "trace",
            ["source_id"] = traceUuid.ToString()
        });
        AssertNext(proposed);
        Assert.Equal("proposed", proposed.GetProperty("data").GetProperty("status").GetString());
        var memoryUuid = proposed.GetProperty("data").GetProperty("uuid").GetGuid();

        // 3. memctl approve — the operator gate that makes it retrievable
        //    (search only returns approved rows, so without this the artifact
        //    cannot legitimately traverse the flow).
        await RunMemCtlAsync("approve", memoryUuid.ToString(), "--by", reviewer);

        // 4. search_memory — finds THAT artifact by its unique term.
        var search = await CallToolAsync(client, "search_memory", new Dictionary<string, object?>
        {
            ["query"] = term,
            ["limit"] = 5
        });
        AssertNext(search);
        Assert.Contains(search.GetProperty("data").EnumerateArray(),
            r => r.GetProperty("uuid").GetGuid() == memoryUuid);

        // 5. get_by_id — fetches the same artifact.
        var fetched = await CallToolAsync(client, "get_by_id", new Dictionary<string, object?>
        {
            ["uuid"] = memoryUuid
        });
        AssertNext(fetched);
        Assert.Equal(memoryUuid, fetched.GetProperty("data").GetProperty("uuid").GetGuid());
    }

    [Fact]
    public async Task LogTraceWithoutSessionIdLandsInTransportSessionAndEchoesIt()
    {
        await using var client = await ConnectAsync(AgentAKey);

        // The schema takes no session_id: session identity is server-derived
        // from the MCP transport, the same rule as agent_id and namespace.
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["event_type"] = "note",
            ["content"] = new { ok = true }
        });

        var transportSession = client.SessionId!;
        Assert.False(string.IsNullOrEmpty(transportSession));
        Assert.Equal(transportSession, logged.GetProperty("data").GetProperty("sessionId").GetString());

        // The event is observable under the transport session through the
        // operator seam.
        var uuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();
        var trace = await RunMemCtlAsync("trace", transportSession);
        Assert.Contains(uuid.ToString(), trace);
    }

    [Fact]
    public async Task LegacySessionIdArgumentIsIgnoredNotRejected()
    {
        var legacySession = $"legacy-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        // Compatibility: existing clients still send session_id (it used to be
        // required). The call succeeds, the argument is dropped, the event
        // lands in the transport session, and the response echoes the
        // substitution.
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = legacySession,
            ["event_type"] = "note",
            ["content"] = new { ok = true }
        });

        var transportSession = client.SessionId!;
        var echoed = logged.GetProperty("data").GetProperty("sessionId").GetString();
        Assert.Equal(transportSession, echoed);
        Assert.NotEqual(legacySession, echoed);

        // The event is under the transport session, and nothing exists under
        // the supplied legacy id.
        var uuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();
        var transportTrace = await RunMemCtlAsync("trace", transportSession);
        Assert.Contains(uuid.ToString(), transportTrace);
        var legacyTrace = await RunMemCtlAsync("trace", legacySession);
        Assert.DoesNotContain(uuid.ToString(), legacyTrace);
    }

    [Fact]
    public async Task LogTraceAndGetByIdShareOneSessionWithServerGeneratedEvents()
    {
        await using var client = await ConnectAsync(AgentAKey);
        var seed = await SeedApprovedMemoryAsync(client, "memory-system");

        // The agent's own event and the server-generated memory_consumed event
        // from get_by_id must be joinable under one trace session without any
        // caller coordination.
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = "about to consult memory" }
        });
        await CallToolAsync(client, "get_by_id", new Dictionary<string, object?> { ["uuid"] = seed });

        var sessionId = logged.GetProperty("data").GetProperty("sessionId").GetString()!;
        Assert.Equal(client.SessionId, sessionId);

        var trace = await RunMemCtlAsync("trace", sessionId);
        Assert.Contains(logged.GetProperty("data").GetProperty("traceUuid").GetGuid().ToString(), trace);
        Assert.Contains("memory_consumed", trace);
        var consumed = await RunMemCtlAsync("consumed", sessionId);
        Assert.Contains(seed.ToString(), consumed);
    }

    [Fact]
    public async Task GetByIdAutoLogsConsumedUnderTransportSession()
    {
        await using var client = await ConnectAsync(AgentAKey);
        var seed = await SeedApprovedMemoryAsync(client, "memory-system");

        await CallToolAsync(client, "get_by_id", new Dictionary<string, object?> { ["uuid"] = seed });

        // get_by_id takes no session_id; the transport session id is assigned by
        // the SDK and never supplied by the agent to any tool. Observing a
        // memory_consumed event under that session — through the memctl operator
        // seam — proves the auto-log is transport-derived with zero agent
        // cooperation. (This session id comes from the transport, not a
        // caller-controlled log_trace argument.)
        var sessionId = client.SessionId!;
        Assert.False(string.IsNullOrEmpty(sessionId));

        var trace = await RunMemCtlAsync("trace", sessionId);
        Assert.Contains("memory_consumed", trace);

        // Tie the consumed event to the specific seed via the consumed seam.
        var consumed = await RunMemCtlAsync("consumed", sessionId);
        Assert.Contains(seed.ToString(), consumed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UnknownKey)]
    public async Task MissingOrUnknownBearerKeyIsRejectedWith401(string? bearerKey)
    {
        using var response = await SendRawMcpAsync(_baseUrl, bearerKey);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("blank agent id", "", "memory-system", new[] { "memory-system" })]
    [InlineData("default outside allowlist", "agent-x", "memory-system", new[] { "homelab" })]
    public async Task IncompleteCredentialIsRejectedAtTheAuthBoundary(
        string _, string agentId, string defaultNamespace, string[] allowedNamespaces)
    {
        // A key that resolves but is not a complete, coherent credential must not
        // authenticate (no identity-less principal, no default outside its own
        // allowlist). The store is built directly — bypassing the loader, which
        // would itself throw — to prove the auth boundary is an independent gate.
        const string presentKey = "present-key-incomplete-identity";
        var store = new AgentKeyStore(new[]
        {
            new AgentKey(presentKey, agentId, defaultNamespace, allowedNamespaces)
        });

        var app = HttpServerHost.Build(RuntimeOptions(), store);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var response = await SendRawMcpAsync(url, presentKey);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
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

        // Read through the SDK result: a tool execution error (IsError), not a
        // JSON-RPC protocol error — a protocol error would have thrown from
        // CallToolAsync instead of returning a result.
        Assert.True(result.IsError == true);

        // The error text must name the exact rejected namespace so the agent
        // can correct itself, without leaking credential material.
        var errorText = string.Join(Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("'homelab'", errorText);
        Assert.DoesNotContain(ScopedKey, errorText);
        Assert.DoesNotContain("Password", errorText);

        // Server-side enforcement: nothing reached the foreign namespace. The
        // operator seam that lists proposals in a namespace (memctl pending) shows
        // no trace of the marker — a proposal that had leaked would appear here.
        var pending = await RunMemCtlAsync("pending", "homelab");
        Assert.DoesNotContain(marker, pending);
    }

    [Fact]
    public async Task RetrieveTraceReturnsFullRecordOnTheWireAndLogsTraceConsumed()
    {
        var marker = $"retrieve-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        var seedMemory = await SeedApprovedMemoryAsync(client, "memory-system");
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = $"wire-shape evidence {marker}" },
            ["refs"] = new[] { seedMemory }
        });
        var traceUuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();

        var retrieved = await CallToolAsync(client, "retrieve_trace", new Dictionary<string, object?>
        {
            ["trace_uuid"] = traceUuid
        });

        // The full trace record, camelCase on the wire, with content as real
        // JSON (not a double-encoded string) and the timestamp as createdAt.
        AssertNext(retrieved);
        Assert.Contains("refs", retrieved.GetProperty("next").GetString());
        var record = retrieved.GetProperty("data");
        Assert.Equal(traceUuid, record.GetProperty("traceUuid").GetGuid());
        Assert.Equal(client.SessionId, record.GetProperty("sessionId").GetString());
        Assert.Equal("agent-a", record.GetProperty("agentId").GetString());
        Assert.Equal("memory-system", record.GetProperty("namespace").GetString());
        Assert.Equal("assistant_msg", record.GetProperty("eventType").GetString());
        Assert.Equal(JsonValueKind.Object, record.GetProperty("content").ValueKind);
        Assert.Equal($"wire-shape evidence {marker}", record.GetProperty("content").GetProperty("text").GetString());
        Assert.Equal(seedMemory, Assert.Single(record.GetProperty("refs").EnumerateArray()).GetGuid());
        Assert.True(record.TryGetProperty("createdAt", out var createdAt));
        Assert.True(createdAt.GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        // The read is audited under the transport session, observable through
        // the operator seam.
        var trace = await RunMemCtlAsync("trace", client.SessionId!);
        var consumedLine = trace.Split('\n').FirstOrDefault(line => line.Contains("trace_consumed"));
        Assert.NotNull(consumedLine);
        Assert.Contains($"refs={traceUuid}", consumedLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrieveTraceForbiddenNamespaceDetailsCrossTheMcpBoundary()
    {
        await using var writer = await ConnectAsync(AgentAKey);
        var logged = await CallToolAsync(writer, "log_trace", new Dictionary<string, object?>
        {
            ["namespace"] = "homelab",
            ["event_type"] = "note",
            ["content"] = new { text = "homelab-only trace" }
        });
        var traceUuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();

        // agent-b's allowlist is memory-system only: the read fails as a tool
        // execution error naming the exact rejected namespace, the same shape
        // as get_by_id (per #25), without leaking credentials.
        await using var outsider = await ConnectAsync(ScopedKey);
        var result = await outsider.CallToolAsync("retrieve_trace", new Dictionary<string, object?>
        {
            ["trace_uuid"] = traceUuid
        });

        Assert.True(result.IsError == true, "retrieving a trace outside the allowlist must be rejected");
        var errorText = string.Join(Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("'homelab'", errorText);
        Assert.DoesNotContain(ScopedKey, errorText);
    }

    [Fact]
    public async Task RetrieveTraceUnknownUuidFailsAcrossTheMcpBoundary()
    {
        await using var client = await ConnectAsync(AgentAKey);
        var unknown = Guid.NewGuid();

        var result = await client.CallToolAsync("retrieve_trace", new Dictionary<string, object?>
        {
            ["trace_uuid"] = unknown
        });

        // The current boundary contract, matching get_by_id's not-found: a
        // tool execution error whose text the SDK masks generically ("An error
        // occurred invoking 'retrieve_trace'") — plain not-found detail does
        // not cross the boundary, unlike namespace-forbidden (which Relay
        // deliberately converts to McpException).
        Assert.True(result.IsError == true, "an unknown trace uuid must fail as not-found");
        var errorText = string.Join(Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("retrieve_trace", errorText);
    }

    [Fact]
    public async Task UnqualifiedCallLandsInDefaultNamespace()
    {
        await using var client = await ConnectAsync(AgentAKey);

        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["event_type"] = "note",
            ["content"] = new { ok = true }
        });
        var uuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();

        // The unqualified call carried no namespace; the operator trace seam shows
        // that event landed in the key's default namespace. The event is located
        // through the session id the server echoed, which must be the transport
        // session.
        var sessionId = logged.GetProperty("data").GetProperty("sessionId").GetString()!;
        Assert.Equal(client.SessionId, sessionId);
        var trace = await RunMemCtlAsync("trace", sessionId);
        var eventLine = trace.Split('\n').FirstOrDefault(line => line.Contains(uuid.ToString()));
        Assert.NotNull(eventLine);
        Assert.Contains("ns=memory-system", eventLine);
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

    [Fact]
    public async Task HttpServerStartupDoesNotWriteToStdout()
    {
        // AGENTS.md: never log to stdout. In HTTP mode the host logs "Now listening
        // on ..." at startup. Non-vacuous by construction: both streams are pumped
        // into buffers, so the startup log MUST show up somewhere. A pass requires
        // it on stderr AND nothing on stdout; a regression that logs to stdout puts
        // "Now listening" in the stdout buffer and fails fast (no blocking read).
        using var process = StartHttpServerProcess();
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var outPump = PumpAsync(process.StandardOutput, stdout);
        var errPump = PumpAsync(process.StandardError, stderr);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline
                && !Contains(stderr, "Now listening")
                && !Contains(stdout, "Now listening")) // regression: fail fast, don't wait the deadline
            {
                await Task.Delay(200);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // let any stray stdout flush
        }
        finally
        {
            await StopProcessAsync(process);
            await Task.WhenAll(outPump, errPump);
        }

        Assert.True(Contains(stderr, "Now listening"),
            $"HTTP host never reported startup on stderr; stdout was:{Environment.NewLine}{Snapshot(stdout)}");
        Assert.True(Snapshot(stdout).Length == 0,
            $"HTTP host wrote to stdout:{Environment.NewLine}{Snapshot(stdout)}");
    }

    private MemSrvOptions RuntimeOptions() => new()
    {
        ConnectionString = RuntimeConnection,
        NeverStorePath = Path.Combine(_root, "config/never_store.yaml"),
    };

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

    // Seeds an approved memory through the public surface only: log_trace →
    // propose_memory → memctl approve. Returns the memory uuid.
    private async Task<Guid> SeedApprovedMemoryAsync(McpClient client, string @namespace)
    {
        var term = $"httpseed-{Guid.NewGuid():N}";
        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["namespace"] = @namespace,
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = "seed" }
        });
        var traceUuid = trace.GetProperty("data").GetProperty("traceUuid").GetGuid();

        var proposed = await CallToolAsync(client, "propose_memory", new Dictionary<string, object?>
        {
            ["namespace"] = @namespace,
            ["type"] = "fact",
            ["content"] = $"Seeded memory {term}",
            ["source_type"] = "trace",
            ["source_id"] = traceUuid.ToString()
        });
        var uuid = proposed.GetProperty("data").GetProperty("uuid").GetGuid();
        await RunMemCtlAsync("approve", uuid.ToString(), "--by", $"seed-operator-{Guid.NewGuid():N}");
        return uuid;
    }

    private static async Task<HttpResponseMessage> SendRawMcpAsync(string baseUrl, string? bearerKey)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/mcp")
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
        return await http.SendAsync(request);
    }

    // --- memctl as a subprocess (the operator seam) ---

    // Runs a memctl command (the sanctioned operator seam), asserts it succeeded,
    // and returns its stdout so tests can observe operator-visible state instead of
    // reading the database directly.
    private static Task<string> RunMemCtlAsync(params string[] args) =>
        TestProcessRunner.RunMemCtlAsync(RuntimeConnection, null, args);

    private Process StartHttpServerProcess() =>
        TestProcessRunner.StartServer(new Dictionary<string, string>
        {
            ["MEMSRV_TRANSPORT"] = "http",
            ["MEMSRV_HTTP_URL"] = "http://127.0.0.1:0",
            ["MEMSRV_AGENT_KEYS_PATH"] = _keysPath,
            ["MEMSRV_CONNECTION_STRING"] = RuntimeConnection,
        });

    private static async Task StopProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync();
    }

    private static Task PumpAsync(StreamReader reader, System.Text.StringBuilder sink) => Task.Run(async () =>
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (sink) { sink.AppendLine(line); }
        }
    });

    private static bool Contains(System.Text.StringBuilder buffer, string value)
    {
        lock (buffer) { return buffer.ToString().Contains(value, StringComparison.Ordinal); }
    }

    private static string Snapshot(System.Text.StringBuilder buffer)
    {
        lock (buffer) { return buffer.ToString(); }
    }

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
}
