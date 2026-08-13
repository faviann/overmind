using System.Text.Json;
using System.Net;
using CaptureAdapters;

namespace MemSrv.Tests;

public sealed class CapturePackagingTests
{
    [Fact]
    public async Task Codex01470PublicHookLoaderAcceptsEveryBoundedWakeCommand()
    {
        string package = Path.Combine(
            TestProcessRunner.RepoRoot, "packages/codex-capture-hooks/0.147.0");
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(package, "manifest.json")));
        Assert.Equal("0.147.0", manifest.RootElement.GetProperty("codexCliVersion").GetString());
        Assert.Equal(
            "http://127.0.0.1:43191/wake",
            manifest.RootElement.GetProperty("wakeUrl").GetString());
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(package, "hooks.json")));

        Assert.Equal(["hooks"], document.RootElement.EnumerateObject().Select(item => item.Name));
        string[] expected =
        [
            "SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse",
            "PermissionRequest", "PostToolUse", "PreCompact", "PostCompact",
            "SubagentStart", "SubagentStop", "Stop"
        ];
        JsonElement hooks = document.RootElement.GetProperty("hooks");
        Assert.Equal(expected.Order(), hooks.EnumerateObject().Select(item => item.Name).Order());
        foreach (JsonProperty entry in hooks.EnumerateObject())
        {
            JsonElement command = entry.Value[0].GetProperty("hooks")[0];
            Assert.Equal("command", command.GetProperty("type").GetString());
            Assert.False(command.TryGetProperty("async", out _));
            Assert.Equal(1, command.GetProperty("timeout").GetInt32());
            Assert.Equal(
                "\"$HOME/.local/bin/overmind-codex-wake-0.147.0\"",
                command.GetProperty("command").GetString());
        }
        string root = Path.Combine(Path.GetTempPath(), $"codex-hooks-{Guid.NewGuid():N}");
        string home = Path.Combine(root, "home");
        string codexHome = Path.Combine(home, ".codex");
        Directory.CreateDirectory(codexHome);
        try
        {
            var install = await TestProcessRunner.RunCommandToExitAsync(
                Path.Combine(package, "install.sh"),
                [codexHome],
                "",
                TimeSpan.Zero,
                new Dictionary<string, string>
                {
                    ["HOME"] = home,
                    ["CODEX_HOME"] = codexHome
                },
                TimeSpan.FromSeconds(5),
                "Codex hook installer");
            Assert.Equal(0, install.ExitCode);
            Assert.Empty(install.Stdout);
            Assert.DoesNotContain("requires codex-cli", install.Stderr, StringComparison.Ordinal);
            Assert.Equal(
                ["home/.codex/hooks.json", "home/.local/bin/overmind-codex-wake-0.147.0"],
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(root, path)).Order());

            string initialize = JsonSerializer.Serialize(new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new { name = "overmind-hook-acceptance", version = "1" },
                    capabilities = new { experimentalApi = true }
                }
            });
            string initialized = "{\"method\":\"initialized\",\"params\":{}}";
            string list = JsonSerializer.Serialize(new
            {
                method = "hooks/list",
                id = 2,
                @params = new { cwds = new[] { TestProcessRunner.RepoRoot } }
            });
            var loaded = await TestProcessRunner.RunCommandToExitAsync(
                "codex",
                ["app-server", "--stdio"],
                $"{initialize}\n{initialized}\n{list}\n",
                TimeSpan.FromMilliseconds(500),
                new Dictionary<string, string>
                {
                    ["HOME"] = home,
                    ["CODEX_HOME"] = codexHome
                },
                TimeSpan.FromSeconds(5),
                "Codex 0.147.0 public hook loader");
            Assert.Equal(0, loaded.ExitCode);
            Assert.DoesNotContain("unsupported", loaded.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("skipping", loaded.Stderr, StringComparison.OrdinalIgnoreCase);
            JsonElement response = loaded.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Single(line => line.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 2)
                .GetProperty("result").GetProperty("data")[0];
            Assert.Empty(response.GetProperty("warnings").EnumerateArray());
            Assert.Empty(response.GetProperty("errors").EnumerateArray());
            Assert.Equal(
                expected.Select(ToCodexEventName).Order(),
                response.GetProperty("hooks").EnumerateArray()
                    .Select(hook => hook.GetProperty("eventName").GetString()).Order());
            foreach (JsonElement hook in response.GetProperty("hooks").EnumerateArray())
            {
                Assert.Equal("command", hook.GetProperty("handlerType").GetString());
                Assert.Equal(1, hook.GetProperty("timeoutSec").GetInt32());
                Assert.True(hook.GetProperty("enabled").GetBoolean());
                Assert.Equal(
                    "\"$HOME/.local/bin/overmind-codex-wake-0.147.0\"",
                    hook.GetProperty("command").GetString());
            }

            var upgrade = await TestProcessRunner.RunCommandToExitAsync(
                Path.Combine(package, "upgrade.sh"),
                [codexHome],
                "",
                TimeSpan.Zero,
                new Dictionary<string, string>
                {
                    ["HOME"] = home,
                    ["CODEX_HOME"] = codexHome
                },
                TimeSpan.FromSeconds(5),
                "same-version Codex hook upgrade");
            Assert.Equal(0, upgrade.ExitCode);
            Assert.Empty(upgrade.Stdout);
            Assert.DoesNotContain("requires codex-cli", upgrade.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CodexHookInstallerRefusesExistingUnrelatedHooksWithoutChangingThem()
    {
        string package = Path.Combine(
            TestProcessRunner.RepoRoot, "packages/codex-capture-hooks/0.147.0");
        string root = Path.Combine(Path.GetTempPath(), $"codex-hooks-refusal-{Guid.NewGuid():N}");
        string home = Path.Combine(root, "home");
        string codexHome = Path.Combine(home, ".codex");
        Directory.CreateDirectory(codexHome);
        string hooks = Path.Combine(codexHome, "hooks.json");
        const string unrelated = "{\"hooks\":{\"Stop\":[]}}\n";
        await File.WriteAllTextAsync(hooks, unrelated);
        try
        {
            var install = await TestProcessRunner.RunCommandToExitAsync(
                Path.Combine(package, "install.sh"),
                [codexHome],
                "",
                TimeSpan.Zero,
                new Dictionary<string, string>
                {
                    ["HOME"] = home,
                    ["CODEX_HOME"] = codexHome
                },
                TimeSpan.FromSeconds(5),
                "Codex hook installer refusal");
            Assert.Equal(2, install.ExitCode);
            Assert.Empty(install.Stdout);
            Assert.Contains("already exists", install.Stderr, StringComparison.Ordinal);
            Assert.Equal(unrelated, await File.ReadAllTextAsync(hooks));
            Assert.False(Directory.Exists(Path.Combine(home, ".local")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ToCodexEventName(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    [Fact]
    public async Task PackagedWakeCommandDiscardsInputSendsNoPayloadAndNeverFailsCodex()
    {
        string command = Path.Combine(
            TestProcessRunner.RepoRoot,
            "packages/codex-capture-hooks/0.147.0/overmind-codex-wake-0.147.0");
        string contents = await File.ReadAllTextAsync(command);
        Assert.Contains("curl --disable ", contents, StringComparison.Ordinal);
        Assert.Contains("--noproxy '*'", contents, StringComparison.Ordinal);
        Assert.Contains("--max-time 0.25", contents, StringComparison.Ordinal);
        await using FileStream portLock = await AcquireFixedWakePortLockAsync();
        using var listener = await ListenOnFixedWakePortAsync();
        System.Net.IPAddress nonLoopback = Assert.IsType<System.Net.IPAddress>(
            DiscoverNonLoopbackIpv4Address());
        using var proxy = new System.Net.Sockets.TcpListener(nonLoopback, 0);
        proxy.Start();
        int proxyPort = ((System.Net.IPEndPoint)proxy.LocalEndpoint).Port;
        Task<System.Net.Sockets.TcpClient> accepted = listener.AcceptTcpClientAsync();
        Task<System.Net.Sockets.TcpClient> proxied = proxy.AcceptTcpClientAsync();
        string curlHome = Path.Combine(
            Path.GetTempPath(), $"hostile-curl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(curlHome);
        await File.WriteAllTextAsync(
            Path.Combine(curlHome, ".curlrc"),
            $"url = \"http://{nonLoopback}:{proxyPort}/from-curlrc\"\n" +
            $"proxy = \"http://{nonLoopback}:{proxyPort}\"\n" +
            "request = PUT\n" +
            "data = operator-config-payload\n");

        try
        {
            string proxyUrl = $"http://{nonLoopback}:{proxyPort}";
            Task<(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed)> execution =
                TestProcessRunner.RunCommandToExitAsync(
                    command,
                    [],
                    "private hook payload",
                    TimeSpan.Zero,
                    new Dictionary<string, string>
                    {
                        ["HOME"] = curlHome,
                        ["CURL_HOME"] = curlHome,
                        ["http_proxy"] = proxyUrl,
                        ["HTTP_PROXY"] = proxyUrl,
                        ["all_proxy"] = proxyUrl,
                        ["ALL_PROXY"] = proxyUrl,
                        ["no_proxy"] = "",
                        ["NO_PROXY"] = ""
                    },
                    TimeSpan.FromSeconds(2),
                    "packaged Codex wake command");
            using System.Net.Sockets.TcpClient client =
                await accepted.WaitAsync(TimeSpan.FromSeconds(2));
            using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
            string request = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) ?? "";
            Assert.Equal("POST /wake HTTP/1.1", request);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                Assert.DoesNotContain("Content-Length", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Transfer-Encoding", line, StringComparison.OrdinalIgnoreCase);
            }
            byte[] response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
            await client.GetStream().WriteAsync(response);
            var result = await execution;
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Elapsed < TimeSpan.FromMilliseconds(750), result.Elapsed.ToString());
            Assert.Empty(result.Stdout);
            Assert.Empty(result.Stderr);
            await Task.Delay(100);
            Assert.False(proxied.IsCompleted, "The wake command contacted a non-loopback proxy.");
            Assert.False(listener.Pending(), "The wake command sent more than one loopback request.");
        }
        finally
        {
            listener.Stop();
            proxy.Stop();
            Directory.Delete(curlHome, recursive: true);
        }

        var absent = await TestProcessRunner.RunCommandToExitAsync(
            command,
            "",
            TimeSpan.FromSeconds(2),
            "packaged Codex wake command without listener");
        Assert.Equal(0, absent.ExitCode);
        Assert.True(absent.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Empty(absent.Stdout);
        Assert.Empty(absent.Stderr);
    }

    private static async Task<FileStream> AcquireFixedWakePortLockAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "overmind-capture-wake-43191.lock");
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<System.Net.Sockets.TcpListener> ListenOnFixedWakePortAsync()
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback, 43191);
            try
            {
                listener.Start();
                return listener;
            }
            catch (System.Net.Sockets.SocketException) when (DateTime.UtcNow < deadline)
            {
                listener.Stop();
                await Task.Delay(50);
            }
        }
    }

    private static async Task WaitForFixedWakePortAsync()
    {
        using var listener = await ListenOnFixedWakePortAsync();
        listener.Stop();
    }

    [Fact]
    public async Task PackagedLoopbackWakeStartsCatchUpBeforeLongScheduleExpires()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-wake-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "3600000";
        await using FileStream portLock = await AcquireFixedWakePortLockAsync();
        await WaitForFixedWakePortAsync();
        using CaptureTracerProcess process = TestProcessRunner.StartCaptureTracer(environment);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(
                new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromMilliseconds(300)
            };
            HttpResponseMessage? readiness = null;
            var readinessDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (readiness is null
                && readinessDeadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                try
                {
                    readiness = await client.PostAsync(
                        "http://127.0.0.1:43191/wake", content: null);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    await Task.Delay(100);
                }
            }
            Assert.True(
                readiness is not null,
                $"The packaged wake endpoint was not ready within " +
                $"{readinessDeadline.Elapsed.TotalSeconds:0.0}s.");
            Assert.Equal(System.Net.HttpStatusCode.NoContent, readiness.StatusCode);
            readiness.Dispose();

            System.Net.IPAddress? nonLoopback = DiscoverNonLoopbackIpv4Address();
            if (nonLoopback is not null)
            {
                Exception? nonLoopbackFailure = await Record.ExceptionAsync(() =>
                    client.PostAsync($"http://{nonLoopback}:43191/wake", content: null));
                Assert.True(
                    nonLoopbackFailure is HttpRequestException or TaskCanceledException,
                    $"The wake endpoint was reachable through non-loopback address " +
                    $"{nonLoopback}.");
            }

            string first = Path.Combine(sessions, "2026", "08", "11", "rollout-same.jsonl");
            string second = Path.Combine(sessions, "2026", "08", "12", "rollout-same.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            await File.WriteAllTextAsync(first, Transcript("first"));
            await File.WriteAllTextAsync(second, Transcript("second"));

            using HttpResponseMessage response = await client.PostAsync(
                "http://127.0.0.1:43191/wake", content: null);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
            string diagnostic = await process.StandardError.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(2)) ?? "";
            Assert.Contains("capture_cycle_failed", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ShippedComposeAllowsCredentiallessPairingAndDocumentsOptionalCredential()
    {
        string compose = File.ReadAllText(Path.Combine(
            TestProcessRunner.RepoRoot, "compose.capture.yaml"));
        string example = File.ReadAllText(Path.Combine(
            TestProcessRunner.RepoRoot, ".env.capture.example"));

        Assert.Contains(
            "OVERMIND_CAPTURE_CREDENTIAL: ${OVERMIND_CAPTURE_CREDENTIAL:-}", compose);
        Assert.DoesNotContain("OVERMIND_CAPTURE_CREDENTIAL:?", compose);
        Assert.Contains("# OVERMIND_CAPTURE_CREDENTIAL=mcap_", example);
        Assert.DoesNotContain("OVERMIND_CAPTURE_CREDENTIAL=<", example);
    }

    [Fact]
    public async Task MissingCredentialUsesPairingAndPersistsDeliveredCredentialMode0600()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-pairing-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string deliveredCredential = $"mcap_{Guid.NewGuid():N}";
        Task server = Task.Run(async () =>
        {
            HttpListenerContext create = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/pairing-requests", create.Request.Url!.AbsolutePath);
            await JsonSerializer.SerializeAsync(create.Response.OutputStream, new
            {
                requestId = Guid.NewGuid(),
                verificationUri = "https://console.invalid/capture/console/pair/DEADBEEF",
                userCode = "DEADBEEF",
                pollingToken = "private-polling-token",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            });
            create.Response.StatusCode = 201;
            create.Response.Close();
            HttpListenerContext poll = await listener.GetContextAsync();
            Assert.Equal("Bearer private-polling-token", poll.Request.Headers["Authorization"]);
            await JsonSerializer.SerializeAsync(poll.Response.OutputStream, new
            {
                status = "approved", credential = deliveredCredential
            });
            poll.Response.StatusCode = 200;
            poll.Response.Close();
        });
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment.Remove("OVERMIND_CAPTURE_CREDENTIAL");
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_pairing_completed\"");
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(result.Stdout);
            Assert.Contains("https://console.invalid/capture/console/pair/DEADBEEF", result.Stderr);
            Assert.DoesNotContain("private-polling-token", result.Stderr);
            Assert.DoesNotContain(deliveredCredential, result.Stderr);
            string path = Path.Combine(state, "capture-credential");
            Assert.Equal(deliveredCredential, await File.ReadAllTextAsync(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerPollsCreatedRequestUntilServerReportsApprovedAfterOriginalExpiry()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-expired-poll-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string deliveredCredential = $"mcap_{Guid.NewGuid():N}";
        Task server = Task.Run(async () =>
        {
            HttpListenerContext create = await listener.GetContextAsync();
            await JsonSerializer.SerializeAsync(create.Response.OutputStream, new
            {
                requestId = Guid.NewGuid(),
                verificationUri = "https://console.invalid/capture/console/pair/DEADBEEF",
                userCode = "DEADBEEF",
                pollingToken = "private-polling-token",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            create.Response.StatusCode = 201;
            create.Response.Close();
            HttpListenerContext poll = await listener.GetContextAsync();
            await JsonSerializer.SerializeAsync(poll.Response.OutputStream, new
            {
                status = "approved", credential = deliveredCredential
            });
            poll.Response.StatusCode = 200;
            poll.Response.Close();
        });
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment.Remove("OVERMIND_CAPTURE_CREDENTIAL");
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_pairing_completed\"");
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(result.Stdout);
            Assert.Equal(deliveredCredential,
                await File.ReadAllTextAsync(Path.Combine(state, "capture-credential")));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    private static int FreePort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        return ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
    }

    [Fact]
    public async Task PackagedLoopbackWakeAcceptsCaseInsensitiveZeroLengthHeadersOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-wake-headers-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string baseline = Path.Combine(sessions, "2026", "08", "11", "rollout-baseline.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        await File.WriteAllTextAsync(baseline, Transcript("startup-baseline"));
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "3600000";
        await using FileStream portLock = await AcquireFixedWakePortLockAsync();
        await WaitForFixedWakePortAsync();
        using CaptureTracerProcess process = TestProcessRunner.StartCaptureTracer(environment);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            DateTime readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (true)
            {
                try
                {
                    Assert.Equal(
                        "HTTP/1.1 404 Not Found",
                        await SendWakeRequestAsync("GET /wake HTTP/1.1\r\n\r\n"));
                    break;
                }
                catch (System.Net.Sockets.SocketException) when (DateTime.UtcNow < readyDeadline)
                {
                    await Task.Delay(100);
                }
            }
            await WaitForCapturedStreamCountAsync(state, 1);

            string first = Path.Combine(sessions, "2026", "08", "12", "rollout-lowercase.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            await File.WriteAllTextAsync(first, Transcript("lowercase-content-length"));

            string[] refused =
            [
                "POST /wake HTTP/1.1\r\nContent-Length: nope\r\n\r\n",
                "POST /wake HTTP/1.1\r\nContent-Length: 1\r\n\r\n",
                "POST /wake HTTP/1.1\r\nContent-Length: 0\r\ncontent-length: 0\r\n\r\n"
            ];
            foreach (string request in refused)
            {
                Assert.Equal("HTTP/1.1 404 Not Found", await SendWakeRequestAsync(request));
            }
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            Assert.Single((await new FileCaptureRuntimeState(state).ReadAsync()).Streams);

            Assert.Equal(
                "HTTP/1.1 204 No Content",
                await SendWakeRequestAsync(
                    "POST /wake HTTP/1.1\r\ncontent-length: 0\r\n\r\n"));
            await WaitForCapturedStreamCountAsync(state, 2);

            string second = Path.Combine(sessions, "2026", "08", "12", "rollout-no-space.jsonl");
            await File.WriteAllTextAsync(second, Transcript("no-space-content-length"));
            Assert.Equal(
                "HTTP/1.1 204 No Content",
                await SendWakeRequestAsync(
                    "POST /wake HTTP/1.1\r\nContent-Length:0\r\n\r\n"));
            await WaitForCapturedStreamCountAsync(state, 3);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> SendWakeRequestAsync(string request)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, 43191);
        System.Net.Sockets.NetworkStream stream = client.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) ?? "";
    }

    private static async Task WaitForCapturedStreamCountAsync(string state, int count)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            CaptureRuntimeSnapshot snapshot = await new FileCaptureRuntimeState(state).ReadAsync();
            if (snapshot.Streams.Count == count)
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"The wake did not capture {count} stream(s) within five seconds.");
    }

    [Fact]
    public async Task WakeBindCollisionLeavesScheduledScanningAuthoritative()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-wake-disabled-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        await using FileStream portLock = await AcquireFixedWakePortLockAsync();
        using var collision = await ListenOnFixedWakePortAsync();
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "25";
        using CaptureTracerProcess process = TestProcessRunner.StartCaptureTracer(environment);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            string transcript = Path.Combine(sessions, "2026", "08", "12", "rollout-scheduled.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
            await File.WriteAllTextAsync(transcript, Transcript("scheduled-without-wake"));

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            CaptureRuntimeSnapshot? snapshot = null;
            while ((snapshot is null || snapshot.Streams.Count == 0)
                && DateTime.UtcNow < deadline)
            {
                snapshot = await new FileCaptureRuntimeState(state).ReadAsync();
                await Task.Delay(25);
            }
            Assert.Single(Assert.IsType<CaptureRuntimeSnapshot>(snapshot).Streams);
            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Contains("capture_wake_unavailable", await stderr, StringComparison.Ordinal);
            collision.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateObservedSourceStreamFailsBeforeClaimOrDelivery()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-source-stream-process-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        string first = Path.Combine(sessions, "2026", "08", "11", "rollout-alpha.jsonl");
        string second = Path.Combine(sessions, "2026", "08", "12", "rollout-beta.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        Directory.CreateDirectory(archive);
        const string privateSession = "same-private-session";
        await File.WriteAllTextAsync(first, Transcript(privateSession));
        await File.WriteAllTextAsync(second, Transcript(privateSession));
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Dictionary<string, string> environment = ProductionEnvironment(
            root, sessions, archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            Assert.False(listener.Pending());
            Assert.False(File.Exists(Path.Combine(state, "capture-state.json")));
            AssertContentFreeJsonDiagnostics(
                result.Stderr,
                "invalid_source_or_receipt",
                root,
                privateSession,
                Path.GetFileName(first),
                Path.GetFileName(second));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateCurrentIdentityIsReportedAsContentFreeJsonAndDoesNotEscape()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-duplicate-process-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string first = Path.Combine(sessions, "2026", "08", "11", "rollout-private.jsonl");
        string second = Path.Combine(sessions, "2026", "08", "12", "rollout-private.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(first, Transcript("local-session-first"));
        await File.WriteAllTextAsync(second, Transcript("local-session-second"));

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                ProductionEnvironment(root, sessions, archive),
                "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            AssertContentFreeJsonDiagnostics(
                result.Stderr,
                "invalid_source_or_receipt",
                root,
                "local-session-first",
                "local-session-second");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{not-json", "invalid_json")]
    [InlineData("{\"contractVersion\":2,\"streams\":[]}", "invalid_source_or_receipt")]
    [InlineData("{\"contractVersion\":1,\"streams\":[{}]}", "invalid_source_or_receipt")]
    public async Task InvalidDurableStateIsReportedAsContentFreeJsonAndDoesNotEscape(
        string stateContents,
        string expectedReason)
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-invalid-state-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "capture-state.json"), stateContents);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                ProductionEnvironment(root, sessions, archive),
                "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            AssertContentFreeJsonDiagnostics(result.Stderr, expectedReason, root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(DuplicatePackagedRuntimeStateProperties))]
    public async Task DuplicateDurableStatePropertiesFailContentFreeBeforePackagedCapture(
        string stateContents)
    {
        const string privateValue = "private-packaged-duplicate-value";
        const string privateApi = "https://private-api.invalid/capture";
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-duplicate-state-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "capture-state.json"), stateContents);
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment["OVERMIND_CAPTURE_URL"] = privateApi;

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            AssertContentFreeJsonDiagnostics(
                result.Stderr,
                "invalid_source_or_receipt",
                root,
                privateValue,
                privateApi);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static IEnumerable<object[]> DuplicatePackagedRuntimeStateProperties()
    {
        const string snapshot = """
            {"contractVersion":1,"streams":[{"sourceStream":"private-packaged-duplicate-value","transcriptIdentity":"transcript","verifiedPrefix":{"byteLength":2,"sha256":"prefix"},"enqueuedThrough":1,"queue":[{"sourceStream":"private-packaged-duplicate-value","sourcePosition":1,"deterministicLocatorEvidence":{"transcriptIdentity":"transcript","sourcePosition":1,"byteOffset":1,"byteLength":1,"recordSha256":"record","prefixEvidence":{"byteLength":2,"sha256":"prefix"}},"redactedSafeCandidate":"{}","outcome":{"contractVersion":1,"captureHealth":"healthy","captureFidelity":"complete","counters":[]}}],"lastServerReceipt":{"sourcePosition":0,"locatorIdentity":"receipt","status":"new","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5","sourceStreamUuid":"a4d86f4c-e045-4761-929b-eec9e5959f95"},"canonicalSourceStreamUuid":"a4d86f4c-e045-4761-929b-eec9e5959f95"}]}
            """;

        yield return [snapshot.Replace("\"streams\":[", "\"streams\":[],\"streams\":[", StringComparison.Ordinal)];
        yield return [snapshot.Replace("\"queue\":[", "\"queue\":[],\"queue\":[", StringComparison.Ordinal)];
        yield return [snapshot.Replace("\"sourcePosition\":1,\"byteOffset\"", "\"sourcePosition\":1,\"sourcePosition\":1,\"byteOffset\"", StringComparison.Ordinal)];
        yield return [snapshot.Replace("\"captureHealth\":\"healthy\"", "\"captureHealth\":\"healthy\",\"captureHealth\":\"healthy\"", StringComparison.Ordinal)];
        yield return [snapshot.Replace("\"status\":\"new\"", "\"status\":\"new\",\"status\":\"new\"", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(CorruptSnapshotRelationships))]
    public async Task ContradictoryDurableStateFailsContentFreeBeforePackagedCapture(
        string stateContents)
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-contradictory-state-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "capture-state.json"), stateContents);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                ProductionEnvironment(root, sessions, archive),
                "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            AssertContentFreeJsonDiagnostics(
                result.Stderr,
                "invalid_source_or_receipt",
                root,
                "private-stream",
                "private-transcript",
                "receipt-private-locator");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static IEnumerable<object[]> CorruptSnapshotRelationships()
    {
        const string stream = """
            {"sourceStream":"private-stream","transcriptIdentity":"private-transcript","verifiedPrefix":{"byteLength":2,"sha256":"prefix-1"},"enqueuedThrough":1,"queue":[{"sourceStream":"private-stream","sourcePosition":1,"deterministicLocatorEvidence":{"transcriptIdentity":"private-transcript","sourcePosition":1,"byteOffset":1,"byteLength":1,"recordSha256":"record-1","prefixEvidence":{"byteLength":2,"sha256":"prefix-1"}},"redactedSafeCandidate":"{}"}],"lastServerReceipt":{"sourcePosition":0,"locatorIdentity":"receipt-private-locator","status":"new","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5","sourceStreamUuid":"a4d86f4c-e045-4761-929b-eec9e5959f95"},"canonicalSourceStreamUuid":"a4d86f4c-e045-4761-929b-eec9e5959f95"}
            """;
        static string Snapshot(string streams) =>
            $"{{\"contractVersion\":1,\"streams\":[{streams}]}}";

        yield return [Snapshot(stream + "," + stream)];
        yield return [Snapshot(stream.Replace(
            "\"enqueuedThrough\":1", "\"enqueuedThrough\":0", StringComparison.Ordinal))];
        yield return [Snapshot(stream.Replace(
            "\"sourcePosition\":0,\"locatorIdentity\"",
            "\"sourcePosition\":-1,\"locatorIdentity\"",
            StringComparison.Ordinal))];
        yield return [Snapshot(stream.Replace(
            "\"sourcePosition\":0,", "", StringComparison.Ordinal))];
        yield return [Snapshot(stream.Replace(
            "\"status\":\"new\"", "\"status\":\"unknown\"", StringComparison.Ordinal))];
        yield return [Snapshot(stream.Replace(
            "b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5",
            "00000000-0000-0000-0000-000000000000",
            StringComparison.Ordinal))];
        yield return [Snapshot(stream.Replace(
            "\"canonicalSourceStreamUuid\":\"a4d86f4c-e045-4761-929b-eec9e5959f95\"",
            "\"canonicalSourceStreamUuid\":\"646daf38-73d9-4c9e-8a84-13e1fc5667f2\"",
            StringComparison.Ordinal))];
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"transcriptIdentity\":\"transcript\",\"sourcePosition\":0,\"byteOffset\":-1,\"byteLength\":1,\"recordSha256\":\"record\",\"prefixEvidence\":{\"byteLength\":0,\"sha256\":\"prefix\"}}")]
    [InlineData("{\"transcriptIdentity\":\"transcript\",\"sourcePosition\":0,\"byteOffset\":9223372036854775807,\"byteLength\":1,\"recordSha256\":\"record\",\"prefixEvidence\":{\"byteLength\":1,\"sha256\":\"prefix\"}}")]
    [InlineData("{\"transcriptIdentity\":\"transcript\",\"sourcePosition\":0,\"byteOffset\":0,\"byteLength\":1,\"recordSha256\":\"record\",\"prefixEvidence\":null}")]
    public async Task CorruptNestedDurableStateFailsContentFreeBeforeDelivery(
        string locatorEvidence)
    {
        const string privateContent = "private-corrupt-state-content";
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-corrupt-nested-process-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(state);
        string durable = """
            {"contractVersion":1,"streams":[{"sourceStream":"stream","transcriptIdentity":"transcript","verifiedPrefix":null,"enqueuedThrough":0,"queue":[{"sourceStream":"stream","sourcePosition":0,"deterministicLocatorEvidence":LOCATOR_EVIDENCE,"redactedSafeCandidate":"PRIVATE_CONTENT","outcome":{"contractVersion":1,"captureHealth":"healthy","captureFidelity":"complete","counters":[]}}],"lastServerReceipt":null,"canonicalSourceStreamUuid":null}]}
            """
            .Replace("LOCATOR_EVIDENCE", locatorEvidence, StringComparison.Ordinal)
            .Replace("PRIVATE_CONTENT", privateContent, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(state, "capture-state.json"), durable);
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Dictionary<string, string> environment = ProductionEnvironment(
            root, sessions, archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            Assert.False(listener.Pending());
            AssertContentFreeJsonDiagnostics(
                result.Stderr,
                "invalid_source_or_receipt",
                root,
                privateContent,
                "transcript",
                "stream");
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedRuntimeRetriesOnlyItsQueuedStreamAfterCodexArchivesIt()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-production-archive-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "12");
        string archive = Path.Combine(root, "archived_sessions");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string active = Path.Combine(sessions, "rollout-responsible.jsonl");
        string archived = Path.Combine(archive, Path.GetFileName(active));
        await File.WriteAllTextAsync(active, Transcript("responsible-session"));

        var environment = new Dictionary<string, string>
        {
            ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
            ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
            ["OVERMIND_CODEX_SESSIONS_ROOT"] = Path.Combine(root, "sessions"),
            ["OVERMIND_CODEX_ARCHIVE_ROOT"] = archive,
            ["OVERMIND_CAPTURE_STATE_DIR"] = state,
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "1",
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
        };

        try
        {
            _ = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            CaptureRuntimeStreamState queued = Assert.Single(
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams);
            Assert.Equal(2, queued.Queue.Count);

            File.Move(active, archived);
            await File.WriteAllTextAsync(
                Path.Combine(archive, "rollout-unrelated.jsonl"),
                Transcript("unrelated-session"));

            _ = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            CaptureRuntimeStreamState retried = Assert.Single(
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams);
            Assert.Equal(queued.SourceStream, retried.SourceStream);
            Assert.Equal(queued.TranscriptIdentity, retried.TranscriptIdentity);
            Assert.Equal(2, retried.Queue.Count);
            Assert.Null(retried.Stop);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedRuntimeDoesNotClaimSameBasenameArchiveWithDifferentSourceIdentity()
    {
        const string replacementContent = "private replacement archive content";
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-replaced-archive-process-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "12");
        string archive = Path.Combine(root, "archived_sessions");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string active = Path.Combine(sessions, "rollout-responsible.jsonl");
        string archived = Path.Combine(archive, Path.GetFileName(active));
        await File.WriteAllTextAsync(active, Transcript("authorized-session"));
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);

        try
        {
            _ = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            CaptureRuntimeStreamState authorized = Assert.Single(
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams);
            Assert.Equal(2, authorized.Queue.Count);

            File.Delete(active);
            await File.WriteAllTextAsync(
                archived,
                Transcript("replacement-session").Replace(
                    "public evidence", replacementContent, StringComparison.Ordinal));

            using (var replacementScan = TestProcessRunner.StartCaptureTracer(environment))
            {
                Task<string> stdout = replacementScan.StandardOutput.ReadToEndAsync();
                Task<string> stderr = replacementScan.StandardError.ReadToEndAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                Assert.False(replacementScan.HasExited);
                replacementScan.Kill(entireProcessTree: true);
                await replacementScan.WaitForExitAsync();
                Assert.Empty(await stdout);
                Assert.Empty(await stderr);
            }

            CaptureRuntimeStreamState retained = Assert.Single(
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams);
            Assert.Equal(authorized.SourceStream, retained.SourceStream);
            Assert.Equal(authorized.TranscriptIdentity, retained.TranscriptIdentity);
            Assert.Equal(2, retained.Queue.Count);
            Assert.Null(retained.Stop);
            Assert.DoesNotContain(
                replacementContent,
                await File.ReadAllTextAsync(Path.Combine(state, "capture-state.json")),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedSchedulerPreservesOwnerWhenCurrentBasenameIsReused()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-reused-current-process-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "12");
        string archive = Path.Combine(root, "archived_sessions");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string active = Path.Combine(sessions, "rollout-reused.jsonl");
        string archived = Path.Combine(archive, Path.GetFileName(active));
        string original = Transcript("session-a");
        await File.WriteAllTextAsync(active, original);
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);

        try
        {
            _ = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            CaptureRuntimeStreamState owner = Assert.Single(
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams);

            await File.WriteAllTextAsync(active, Transcript("session-b"));
            using (var replacementScan = TestProcessRunner.StartCaptureTracer(environment))
            {
                Task<string> stdout = replacementScan.StandardOutput.ReadToEndAsync();
                Task<string> stderr = replacementScan.StandardError.ReadToEndAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                Assert.False(replacementScan.HasExited);
                replacementScan.Kill(entireProcessTree: true);
                await replacementScan.WaitForExitAsync();
                Assert.Empty(await stdout);
                Assert.Empty(await stderr);
            }

            CaptureRuntimeSnapshot afterReplacement =
                await new FileCaptureRuntimeState(state).ReadAsync();
            Assert.Equal(
                JsonSerializer.Serialize(owner),
                JsonSerializer.Serialize(Assert.Single(afterReplacement.Streams)));

            await File.WriteAllTextAsync(archived, original);
            await File.WriteAllTextAsync(
                Path.Combine(sessions, "rollout-distinct.jsonl"),
                Transcript("session-c"));
            using (var convergenceScan = TestProcessRunner.StartCaptureTracer(environment))
            {
                Task<string> stdout = convergenceScan.StandardOutput.ReadToEndAsync();
                Task<string> stderr = convergenceScan.StandardError.ReadToEndAsync();
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while ((await new FileCaptureRuntimeState(state).ReadAsync()).Streams.Count < 2
                    && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }
                convergenceScan.Kill(entireProcessTree: true);
                await convergenceScan.WaitForExitAsync();
                Assert.Empty(await stdout);
                string diagnostics = await stderr;
                Assert.True(
                    (await new FileCaptureRuntimeState(state).ReadAsync()).Streams.Count >= 2,
                    diagnostics);
            }

            CaptureRuntimeStreamState[] converged =
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams.ToArray();
            Assert.Equal(2, converged.Length);
            Assert.Equal(
                2,
                converged.Select(stream => stream.TranscriptIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.Contains(converged, stream => stream.SourceStream == owner.SourceStream);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionRuntimeStartsWithoutSyntheticGateAndKeepsStdoutEmpty()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-production-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(archive);
        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
                    ["OVERMIND_CODEX_SESSIONS_ROOT"] = root,
                    ["OVERMIND_CODEX_ARCHIVE_ROOT"] = archive,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = Path.Combine(root, "state"),
                    ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "1",
                    ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
                });
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(process.HasExited);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Empty(await stderr);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSessionsRootIsContentFreeAndRetriedWithoutStoppingRuntime()
    {
        string parent = Path.Combine(
            Path.GetTempPath(), $"capture-missing-root-{Guid.NewGuid():N}");
        string missingRoot = Path.Combine(parent, "private-sessions-name");
        string state = Path.Combine(parent, "state");
        Directory.CreateDirectory(parent);
        string archive = Path.Combine(parent, "archive");
        Directory.CreateDirectory(archive);
        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
                    ["OVERMIND_CODEX_SESSIONS_ROOT"] = missingRoot,
                    ["OVERMIND_CODEX_ARCHIVE_ROOT"] = archive,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = state,
                    ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "25",
                    ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
                });
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(process.HasExited);
            Directory.CreateDirectory(Path.Combine(missingRoot, "2026", "08", "12"));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(process.HasExited);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            string diagnostics = await stderr;
            Assert.DoesNotContain(missingRoot, diagnostics, StringComparison.Ordinal);
            JsonElement[] lines = diagnostics.Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.Contains(lines, line =>
                line.GetProperty("event").GetString() == "capture_cycle_failed"
                && line.GetProperty("reason").GetString() == "transcript_root_unavailable");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string Transcript(string sessionId) =>
        JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new { id = sessionId, session_id = sessionId }
        }) + "\n" + JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text = "public evidence" } }
            }
        }) + "\n";

    private static System.Net.IPAddress? DiscoverNonLoopbackIpv4Address()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .FirstOrDefault(address =>
                    address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !System.Net.IPAddress.IsLoopback(address));
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ProductionEnvironment(
        string root,
        string sessions,
        string archive) => new()
    {
        ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
        ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
        ["OVERMIND_CODEX_SESSIONS_ROOT"] = sessions,
        ["OVERMIND_CODEX_ARCHIVE_ROOT"] = archive,
        ["OVERMIND_CAPTURE_STATE_DIR"] = Path.Combine(root, "state"),
        ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "1",
        ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
    };

    private static void AssertContentFreeJsonDiagnostics(
        string stderr,
        string expectedReason,
        params string[] forbidden)
    {
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", stderr, StringComparison.Ordinal);
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, stderr, StringComparison.Ordinal);
        }
        JsonElement[] diagnostics = stderr.Split(
                Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.GetProperty("event").GetString() == "capture_cycle_failed"
            && diagnostic.GetProperty("reason").GetString() == expectedReason);
    }
}
