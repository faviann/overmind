using MemSrv.Core;
using MemSrv.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Net;

namespace MemSrv.Tests;

// The HTTP seam: everything the HTTP transport adds — bearer auth, credential
// validation, allowlist enforcement, default-namespace behavior,
// transport-derived sessions, /healthz — asserted through the public surface
// only (harness in HttpSeamTestBase): MCP tools for agent actions, memctl for
// operator-visible state. No direct database reads.
[Collection("database")]
public sealed class HttpTransportTests : HttpSeamTestBase
{
    private const string UnknownKey = "not-a-real-key";

    [Fact]
    public async Task KeyedHttpAgentRunsFullMemoryLifecycleOnOneArtifact()
    {
        var term = $"lifecycle-{Guid.NewGuid():N}";
        var reviewer = $"reviewer-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        // 1. log_trace — the source the proposal will cite.
        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = "http-lifecycle-trace",
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

        Assert.True(result.IsError == true);

        // Server-side enforcement: nothing reached the foreign namespace. The
        // operator seam that lists proposals in a namespace (memctl pending) shows
        // no trace of the marker — a proposal that had leaked would appear here.
        var pending = await RunMemCtlAsync("pending", "homelab");
        Assert.DoesNotContain(marker, pending);
    }

    [Fact]
    public async Task UnqualifiedCallLandsInDefaultNamespace()
    {
        var sessionId = $"http-default-ns-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["event_type"] = "note",
            ["content"] = new { ok = true }
        });
        var uuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();

        // The unqualified call carried no namespace; the operator trace seam shows
        // that event landed in the key's default namespace. (The session id here is
        // caller-supplied because log_trace still requires it — it is only used to
        // locate the event, not as evidence of transport-derived sessioning.)
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

    // Seeds an approved memory through the public surface only: log_trace →
    // propose_memory → memctl approve. Returns the memory uuid.
    private async Task<Guid> SeedApprovedMemoryAsync(McpClient client, string @namespace)
    {
        var term = $"httpseed-{Guid.NewGuid():N}";
        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["namespace"] = @namespace,
            ["session_id"] = $"seed-{Guid.NewGuid():N}",
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

    private Process StartHttpServerProcess()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "src/MemSrv.Server/MemSrv.Server.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.Environment["MEMSRV_TRANSPORT"] = "http";
        startInfo.Environment["MEMSRV_HTTP_URL"] = "http://127.0.0.1:0";
        startInfo.Environment["MEMSRV_AGENT_KEYS_PATH"] = _keysPath;
        startInfo.Environment["MEMSRV_CONNECTION_STRING"] = RuntimeConnection;

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start MemSrv.Server.");
    }

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
}
