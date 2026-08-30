using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class PublicSurfaceRemovalTests : IAsyncLifetime
{
    private const string AgentKey = "mcap_ordinary-agent-key-1234567890";
    private readonly string _root = TestProcessRunner.RepoRoot;
    private string _keysPath = "";
    private Process _server = null!;
    private Task _stdoutPump = null!;
    private Task _stderrPump = null!;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        await TestDatabase.PrepareClassDatabaseAsync(
            typeof(PublicSurfaceRemovalTests), Path.Combine(_root, "migrations"));
        _keysPath = Path.Combine(Path.GetTempPath(), $"removed-surface-keys-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(_keysPath,
            $"keys:\n  - key: {AgentKey}\n    agent_id: retired-prefix-agent\n    default_namespace: memory-system\n    allowed_namespaces: [memory-system]\n");

        _server = TestProcessRunner.StartServer(new Dictionary<string, string>
        {
            ["MEMSRV_TRANSPORT"] = "http",
            ["MEMSRV_HTTP_URL"] = "http://127.0.0.1:0",
            ["MEMSRV_AGENT_KEYS_PATH"] = _keysPath,
            ["MEMSRV_CONNECTION_STRING"] = TestDatabase.RuntimeConnection,
            ["MEMSRV_CAPTURE_CONSOLE_OIDC_AUTHORITY"] = "not-an-https-authority",
            ["MEMSRV_CAPTURE_CONSOLE_OIDC_CLIENT_ID"] = "not-a-valid-client-id",
            ["MEMSRV_CAPTURE_CONSOLE_OIDC_CLIENT_SECRET"] = "not-a-valid-client-secret",
        });
        _stdoutPump = PumpAsync(_server.StandardOutput, _stdout);
        _stderrPump = PumpAsync(_server.StandardError, _stderr);
        _baseUrl = await WaitForListeningUrlAsync(_stderr);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            if (!_server.HasExited)
            {
                _server.Kill(entireProcessTree: true);
            }
            await _server.WaitForExitAsync();
            await Task.WhenAll(_stdoutPump, _stderrPump);
            _server.Dispose();
        }
        if (File.Exists(_keysPath))
        {
            File.Delete(_keysPath);
        }
    }

    [Fact]
    public async Task PackagedServerHasNoCaptureRouteFamily()
    {
        var requests = new[]
        {
            new HttpRequestMessage(HttpMethod.Post, "/capture/v1/observations"),
            new HttpRequestMessage(HttpMethod.Post, "/capture/v1/pairing-requests"),
            new HttpRequestMessage(HttpMethod.Get, $"/capture/v1/pairing-requests/{Guid.NewGuid()}"),
            new HttpRequestMessage(HttpMethod.Delete, $"/capture/v1/pairing-requests/{Guid.NewGuid()}"),
            new HttpRequestMessage(HttpMethod.Get, "/capture/console"),
            new HttpRequestMessage(HttpMethod.Get, "/capture/console/signin-oidc"),
            new HttpRequestMessage(HttpMethod.Get, "/capture/console/pair/ABCD-EFGH"),
            new HttpRequestMessage(HttpMethod.Post, "/capture/console/pair/ABCD-EFGH/approve"),
            new HttpRequestMessage(HttpMethod.Get, $"/capture/console/api/pairing/{Guid.NewGuid()}"),
            new HttpRequestMessage(HttpMethod.Post, $"/capture/console/api/pairing/{Guid.NewGuid()}/approve"),
            new HttpRequestMessage(HttpMethod.Delete, $"/capture/console/api/pairing/{Guid.NewGuid()}"),
        };
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };

        foreach (HttpRequestMessage request in requests)
        {
            using (request)
            using (HttpResponseMessage response = await client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task RetiredCaptureCommandsAreUnknownAndNotAdvertised()
    {
        string[][] commands =
        [
            ["capture"],
            ["capture", "enroll"],
            ["capture", "route-policy"],
            ["capture", "receipt"],
            ["capture", "replay"],
            ["capture", "navigate"],
        ];

        foreach (string[] command in commands)
        {
            var result = await TestProcessRunner.RunMemCtlToExitAsync(
                new Dictionary<string, string>(), command);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.DoesNotContain("memctl capture", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("memctl migrate", result.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CapturePrefixedProvisionedKeyAuthenticatesAsOrdinaryAgent()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{_baseUrl}/mcp"),
            Name = "MemSrv.Server",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {AgentKey}",
            },
        });

        await using McpClient client = await McpClient.CreateAsync(transport);
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    private static Task PumpAsync(StreamReader reader, StringBuilder sink) => Task.Run(async () =>
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (sink)
            {
                sink.AppendLine(line);
            }
        }
    });

    private async Task<string> WaitForListeningUrlAsync(StringBuilder stderr)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            string snapshot;
            lock (stderr)
            {
                snapshot = stderr.ToString();
            }
            foreach (string line in snapshot.Split('\n'))
            {
                const string marker = "Now listening on: ";
                int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    return line[(markerIndex + marker.Length)..].Trim();
                }
            }
            if (_server.HasExited)
            {
                break;
            }
            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException(
            $"Server never reported a listening address. stderr:{Environment.NewLine}{stderr}");
    }
}
