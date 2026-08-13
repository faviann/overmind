using System.Text.Json;
using System.Net;
using CaptureAdapters;

namespace MemSrv.Tests;

public sealed class CapturePackagingTests
{
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
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext poll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", poll.Request.Url!.AbsolutePath);
            await RespondJsonAsync(poll, new
            {
                paused = false,
                instructions = Array.Empty<object>()
            });
        });
        Dictionary<string, string> environment = ProductionEnvironment(
            root, sessions, archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            await server.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(result.Stdout);
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

    [Fact]
    public async Task PackagedTracerPersistsPauseBeforeAcknowledgingAndDoesNotScan()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-instruction-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        string first = Path.Combine(sessions, "2026", "08", "13", "rollout-first.jsonl");
        string second = Path.Combine(sessions, "2026", "08", "13", "rollout-second.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(first, Transcript("same-private-session"));
        await File.WriteAllTextAsync(second, Transcript("same-private-session"));
        Guid instructionId = Guid.NewGuid();
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext poll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", poll.Request.Url!.AbsolutePath);
            Assert.StartsWith("Bearer mcap_", poll.Request.Headers["Authorization"]);
            await JsonSerializer.SerializeAsync(poll.Response.OutputStream, new
            {
                paused = true,
                instructions = new[]
                {
                    new { instructionId, operation = "pause", createdAt = DateTimeOffset.UtcNow }
                }
            });
            poll.Response.StatusCode = 200;
            poll.Response.Close();

            HttpListenerContext acknowledgement = await listener.GetContextAsync();
            Assert.Equal(
                $"/capture/v1/instructions/{instructionId}/acknowledge",
                acknowledgement.Request.Url!.AbsolutePath);
            Assert.Equal(0, acknowledgement.Request.ContentLength64);
            await JsonSerializer.SerializeAsync(acknowledgement.Response.OutputStream, new
            {
                instructionId,
                acknowledgedAt = DateTimeOffset.UtcNow
            });
            acknowledgement.Response.StatusCode = 200;
            acknowledgement.Response.Close();
        });
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_policy_paused\"");
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(result.Stdout);
            Assert.DoesNotContain("capture_cycle_failed", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("same-private-session", result.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "state", "capture-state.json")));
            JsonElement state = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(root, "state", "capture-instruction-policy.json"))).RootElement;
            Assert.True(state.GetProperty("paused").GetBoolean());
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerAcknowledgesScanOnlyAfterCompletedTranscriptDelivery()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-scan-instruction-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-scan.jsonl"), Transcript("scan-session"));
        Guid instructionId = Guid.NewGuid();
        Guid sourceStreamUuid = Guid.NewGuid();
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext poll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", poll.Request.Url!.AbsolutePath);
            await RespondJsonAsync(poll, new
            {
                paused = false,
                instructions = new[]
                {
                    new { instructionId, operation = "scan", createdAt = DateTimeOffset.UtcNow }
                }
            });
            for (int delivered = 0; delivered < 2; delivered++)
            {
                HttpListenerContext observation = await listener.GetContextAsync();
                Assert.Equal("/capture/v1/observations", observation.Request.Url!.AbsolutePath);
                await RespondWithObservationReceiptAsync(observation, sourceStreamUuid);
            }
            HttpListenerContext acknowledgement = await listener.GetContextAsync();
            Assert.Equal(
                $"/capture/v1/instructions/{instructionId}/acknowledge",
                acknowledgement.Request.Url!.AbsolutePath);
            await RespondJsonAsync(acknowledgement, new
            {
                instructionId, acknowledgedAt = DateTimeOffset.UtcNow
            });
        });
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Contains("capture_delivery_accepted", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerRetriesStableScanInstructionAfterObservationFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-scan-retry-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-scan-retry.jsonl"), Transcript("scan-retry-session"));
        Guid instructionId = Guid.NewGuid();
        Guid sourceStreamUuid = Guid.NewGuid();
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext firstPoll = await listener.GetContextAsync();
            await RespondJsonAsync(firstPoll, new
            {
                paused = false,
                instructions = new[]
                {
                    new { instructionId, operation = "scan", createdAt = DateTimeOffset.UtcNow }
                }
            });
            HttpListenerContext failedObservation = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/observations", failedObservation.Request.Url!.AbsolutePath);
            failedObservation.Response.StatusCode = 503;
            failedObservation.Response.Close();

            HttpListenerContext secondPoll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", secondPoll.Request.Url!.AbsolutePath);
            await RespondJsonAsync(secondPoll, new
            {
                paused = false,
                instructions = new[]
                {
                    new { instructionId, operation = "scan", createdAt = DateTimeOffset.UtcNow }
                }
            });
            for (int delivered = 0; delivered < 2; delivered++)
            {
                HttpListenerContext observation = await listener.GetContextAsync();
                Assert.Equal("/capture/v1/observations", observation.Request.Url!.AbsolutePath);
                await RespondWithObservationReceiptAsync(observation, sourceStreamUuid);
            }
            HttpListenerContext acknowledgement = await listener.GetContextAsync();
            Assert.Equal(
                $"/capture/v1/instructions/{instructionId}/acknowledge",
                acknowledgement.Request.Url!.AbsolutePath);
            await RespondJsonAsync(acknowledgement, new
            {
                instructionId, acknowledgedAt = DateTimeOffset.UtcNow
            });
        });
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Contains("capture_delivery_accepted", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerRetriesStableScanInstructionAfterIdentityFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-identity-retry-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        string transcript = Path.Combine(sessions, "rollout-identity-retry.jsonl");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"session_meta","payload":{"session_id":"identity-retry-session","id":"identity-retry-thread","source":"cli","thread_source":"subagent"}}""" + "\n");
        Guid instructionId = Guid.NewGuid();
        Guid sourceStreamUuid = Guid.NewGuid();
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext firstPoll = await listener.GetContextAsync();
            await RespondJsonAsync(firstPoll, new
            {
                paused = false,
                instructions = new[]
                {
                    new { instructionId, operation = "scan", createdAt = DateTimeOffset.UtcNow }
                }
            });

            HttpListenerContext secondPoll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", secondPoll.Request.Url!.AbsolutePath);
            await File.WriteAllTextAsync(transcript, Transcript("identity-retry-session"));
            await RespondJsonAsync(secondPoll, new
            {
                paused = false,
                instructions = new[]
                {
                    new { instructionId, operation = "scan", createdAt = DateTimeOffset.UtcNow }
                }
            });
            for (int delivered = 0; delivered < 2; delivered++)
            {
                HttpListenerContext observation = await listener.GetContextAsync();
                Assert.Equal("/capture/v1/observations", observation.Request.Url!.AbsolutePath);
                await RespondWithObservationReceiptAsync(observation, sourceStreamUuid);
            }
            HttpListenerContext acknowledgement = await listener.GetContextAsync();
            Assert.Equal(
                $"/capture/v1/instructions/{instructionId}/acknowledge",
                acknowledgement.Request.Url!.AbsolutePath);
            await RespondJsonAsync(acknowledgement, new
            {
                instructionId, acknowledgedAt = DateTimeOffset.UtcNow
            });
        });
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Contains("capture_delivery_accepted", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedTracerRejectsInconclusiveSuccessfulAcknowledgement(
        bool malformed)
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-ack-response-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions");
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Guid instructionId = Guid.NewGuid();
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext poll = await listener.GetContextAsync();
            await RespondJsonAsync(poll, new
            {
                paused = true,
                instructions = new[]
                {
                    new { instructionId, operation = "pause", createdAt = DateTimeOffset.UtcNow }
                }
            });
            HttpListenerContext acknowledgement = await listener.GetContextAsync();
            Assert.Equal(
                $"/capture/v1/instructions/{instructionId}/acknowledge",
                acknowledgement.Request.Url!.AbsolutePath);
            await RespondJsonAsync(
                acknowledgement,
                malformed
                    ? new { }
                    : new
                    {
                        instructionId = Guid.NewGuid(),
                        acknowledgedAt = DateTimeOffset.UtcNow
                    });

            HttpListenerContext nextPoll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", nextPoll.Request.Url!.AbsolutePath);
            await RespondJsonAsync(nextPoll, new
            {
                paused = false,
                instructions = Array.Empty<object>()
            });
        });
        Dictionary<string, string> environment = ProductionEnvironment(root, sessions, archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Contains("capture_cycle_failed", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerAcknowledgesRetryOnlyAfterDurableQueueDelivery()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-retry-instruction-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-retry.jsonl"), Transcript("retry-session"));
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);

        try
        {
            _ = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"reason\":\"endpoint_unavailable\"");
            Guid instructionId = Guid.NewGuid();
            Guid sourceStreamUuid = Guid.NewGuid();
            using var listener = new HttpListener();
            int port = FreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Task server = Task.Run(async () =>
            {
                HttpListenerContext poll = await listener.GetContextAsync();
                await RespondJsonAsync(poll, new
                {
                    paused = false,
                    instructions = new[]
                    {
                        new { instructionId, operation = "retry", createdAt = DateTimeOffset.UtcNow }
                    }
                });
                for (int delivered = 0; delivered < 2; delivered++)
                {
                    HttpListenerContext observation = await listener.GetContextAsync();
                    Assert.Equal("/capture/v1/observations", observation.Request.Url!.AbsolutePath);
                    await RespondWithObservationReceiptAsync(observation, sourceStreamUuid);
                }
                HttpListenerContext acknowledgement = await listener.GetContextAsync();
                Assert.Equal(
                    $"/capture/v1/instructions/{instructionId}/acknowledge",
                    acknowledgement.Request.Url!.AbsolutePath);
                await RespondJsonAsync(acknowledgement, new
                {
                    instructionId, acknowledgedAt = DateTimeOffset.UtcNow
                });
            });
            environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            Assert.Contains("capture_delivery_accepted", await stderr, StringComparison.Ordinal);
            listener.Stop();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PausedMixtureDefersScanAndRetryAcknowledgementsUntilResumedWork()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-paused-mixture-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-mixture.jsonl"), Transcript("mixture-session"));
        Guid pauseId = Guid.NewGuid();
        Guid scanId = Guid.NewGuid();
        Guid retryId = Guid.NewGuid();
        Guid resumeId = Guid.NewGuid();
        Guid sourceStreamUuid = Guid.NewGuid();
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext firstPoll = await listener.GetContextAsync();
            await RespondJsonAsync(firstPoll, new
            {
                paused = true,
                instructions = new object[]
                {
                    new { instructionId = pauseId, operation = "pause", createdAt = DateTimeOffset.UtcNow },
                    new { instructionId = scanId, operation = "scan", createdAt = DateTimeOffset.UtcNow },
                    new { instructionId = retryId, operation = "retry", createdAt = DateTimeOffset.UtcNow }
                }
            });
            HttpListenerContext pauseAck = await listener.GetContextAsync();
            Assert.Equal(
                $"/capture/v1/instructions/{pauseId}/acknowledge",
                pauseAck.Request.Url!.AbsolutePath);
            await RespondJsonAsync(pauseAck, new
            {
                instructionId = pauseId, acknowledgedAt = DateTimeOffset.UtcNow
            });

            HttpListenerContext secondPoll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", secondPoll.Request.Url!.AbsolutePath);
            await RespondJsonAsync(secondPoll, new
            {
                paused = false,
                instructions = new object[]
                {
                    new { instructionId = scanId, operation = "scan", createdAt = DateTimeOffset.UtcNow },
                    new { instructionId = retryId, operation = "retry", createdAt = DateTimeOffset.UtcNow },
                    new { instructionId = resumeId, operation = "resume", createdAt = DateTimeOffset.UtcNow }
                }
            });
            for (int delivered = 0; delivered < 2; delivered++)
            {
                HttpListenerContext observation = await listener.GetContextAsync();
                Assert.Equal("/capture/v1/observations", observation.Request.Url!.AbsolutePath);
                await RespondWithObservationReceiptAsync(observation, sourceStreamUuid);
            }
            foreach (Guid instructionId in new[] { scanId, retryId, resumeId })
            {
                HttpListenerContext acknowledgement = await listener.GetContextAsync();
                Assert.Equal(
                    $"/capture/v1/instructions/{instructionId}/acknowledge",
                    acknowledgement.Request.Url!.AbsolutePath);
                await RespondJsonAsync(acknowledgement, new
                {
                    instructionId, acknowledgedAt = DateTimeOffset.UtcNow
                });
            }
        });
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Empty(await stdout);
            string diagnostics = await stderr;
            Assert.Contains("capture_policy_paused", diagnostics, StringComparison.Ordinal);
            Assert.Contains("capture_delivery_accepted", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerRetainsQueueWhenPersistedPauseCannotBePolled()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-persisted-pause-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string transcript = Path.Combine(sessions, "rollout-paused.jsonl");
        await File.WriteAllTextAsync(transcript, Transcript("paused-session"));
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);

        try
        {
            _ = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            string beforePause = await File.ReadAllTextAsync(
                Path.Combine(state, "capture-state.json"));
            await File.WriteAllTextAsync(
                Path.Combine(state, "capture-instruction-policy.json"),
                "{\"paused\":true}");
            await File.AppendAllTextAsync(
                transcript,
                JsonSerializer.Serialize(new
                {
                    type = "response_item",
                    payload = new
                    {
                        type = "message",
                        role = "assistant",
                        content = new[]
                        {
                            new { type = "output_text", text = "must remain unclaimed" }
                        }
                    }
                }) + "\n");

            using var listener = new HttpListener();
            int port = FreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Task server = Task.Run(async () =>
            {
                HttpListenerContext poll = await listener.GetContextAsync();
                Assert.Equal("/capture/v1/instructions", poll.Request.Url!.AbsolutePath);
                poll.Response.StatusCode = 503;
                poll.Response.Close();
            });
            environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                environment, "\"event\":\"capture_cycle_failed\"");
            await server.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(result.Stdout);
            Assert.Contains("\"reason\":\"endpoint_unavailable\"", result.Stderr);
            Assert.Equal(
                beforePause,
                await File.ReadAllTextAsync(Path.Combine(state, "capture-state.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{}")]
    [InlineData("{\"paused\":\"true\"}")]
    [InlineData("{\"paused\":true,\"paused\":false}")]
    public async Task PackagedTracerFailsClosedOnMalformedPersistedPolicy(
        string policy)
    {
        const string privateContent = "must-not-be-scanned-from-malformed-policy";
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-invalid-policy-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-invalid-policy.jsonl"),
            Transcript("invalid-policy-session").Replace(
                "public evidence", privateContent, StringComparison.Ordinal));
        await File.WriteAllTextAsync(
            Path.Combine(state, "capture-instruction-policy.json"), policy);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerUntilDiagnosticAsync(
                ProductionEnvironment(root, Path.Combine(root, "sessions"), archive),
                "\"event\":\"capture_cycle_failed\"");

            Assert.Empty(result.Stdout);
            Assert.False(File.Exists(Path.Combine(state, "capture-state.json")));
            Assert.DoesNotContain(privateContent, result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(root, result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerCurrentResumeClearsPersistedPauseAndRestoresScanning()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-current-resume-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "08", "13");
        string archive = Path.Combine(root, "archive");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-resumed.jsonl"),
            Transcript("resumed-session"));
        await File.WriteAllTextAsync(
            Path.Combine(state, "capture-instruction-policy.json"),
            "{\"paused\":true}");
        using var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            HttpListenerContext poll = await listener.GetContextAsync();
            Assert.Equal("/capture/v1/instructions", poll.Request.Url!.AbsolutePath);
            await JsonSerializer.SerializeAsync(poll.Response.OutputStream, new
            {
                paused = false,
                instructions = Array.Empty<object>()
            });
            poll.Response.StatusCode = 200;
            poll.Response.Close();
            Guid sourceStreamUuid = Guid.NewGuid();
            for (int delivered = 0; delivered < 2; delivered++)
            {
                HttpListenerContext observation = await listener.GetContextAsync();
                Assert.Equal("/capture/v1/observations", observation.Request.Url!.AbsolutePath);
                await RespondWithObservationReceiptAsync(observation, sourceStreamUuid);
            }
        });
        Dictionary<string, string> environment = ProductionEnvironment(
            root, Path.Combine(root, "sessions"), archive);
        environment["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}";

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            Assert.Empty(await stdout);
            Assert.Contains("capture_delivery_accepted", await stderr, StringComparison.Ordinal);
            JsonElement policy = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(state, "capture-instruction-policy.json"))).RootElement;
            Assert.False(policy.GetProperty("paused").GetBoolean());
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
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

    private static async Task RespondWithObservationReceiptAsync(
        HttpListenerContext context,
        Guid sourceStreamUuid)
    {
        using JsonDocument request = await JsonDocument.ParseAsync(
            context.Request.InputStream);
        JsonElement root = request.RootElement;
        JsonElement locator = root.GetProperty("locator");
        Guid observationUuid = Guid.NewGuid();
        await RespondJsonAsync(context, new
        {
            observationUuid,
            status = "new",
            sourcePosition = root.GetProperty("sourcePosition").GetInt64(),
            sourceStreamUuid,
            observation = new
            {
                observationUuid,
                sourceStreamUuid,
                locator = new
                {
                    kind = "byte_range",
                    byteOffset = locator.GetProperty("byteOffset").GetInt64(),
                    byteLength = locator.GetProperty("byteLength").GetInt64()
                }
            }
        });
    }

    private static async Task RespondJsonAsync(HttpListenerContext context, object body)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, body);
        context.Response.StatusCode = 200;
        context.Response.Close();
    }

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
