using System.Text.Json;
using CaptureAdapters;

namespace MemSrv.Tests;

public sealed class CapturePackagingTests
{
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
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(environment);

            Assert.Equal(0, result.ExitCode);
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
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                ProductionEnvironment(root, sessions, archive));

            Assert.Equal(0, result.ExitCode);
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
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                ProductionEnvironment(root, sessions, archive));

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stdout);
            AssertContentFreeJsonDiagnostics(result.Stderr, expectedReason, root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                ProductionEnvironment(root, sessions, archive));

            Assert.Equal(0, result.ExitCode);
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
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(environment);

            Assert.Equal(0, result.ExitCode);
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
            ["OVERMIND_CAPTURE_RUN_ONCE"] = "true",
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "1",
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
        };

        try
        {
            var outage = await TestProcessRunner.RunCaptureTracerToExitAsync(environment);
            Assert.Equal(0, outage.ExitCode);
            CaptureRuntimeStreamState queued = Assert.Single(
                (await new FileCaptureRuntimeState(state).ReadAsync()).Streams);
            Assert.Equal(2, queued.Queue.Count);

            File.Move(active, archived);
            await File.WriteAllTextAsync(
                Path.Combine(archive, "rollout-unrelated.jsonl"),
                Transcript("unrelated-session"));

            var restarted = await TestProcessRunner.RunCaptureTracerToExitAsync(environment);
            Assert.Equal(0, restarted.ExitCode);
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
    public async Task ProductionRuntimeStartsWithoutSyntheticGateAndKeepsStdoutEmpty()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-production-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(archive);
        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
                    ["OVERMIND_CODEX_SESSIONS_ROOT"] = root,
                    ["OVERMIND_CODEX_ARCHIVE_ROOT"] = archive,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = Path.Combine(root, "state"),
                    ["OVERMIND_CAPTURE_RUN_ONCE"] = "true",
                    ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "1",
                    ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Stdout);
            JsonElement diagnostic = JsonDocument.Parse(
                Assert.Single(result.Stderr.Split(
                    Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries))).RootElement;
            Assert.Equal("capture_runtime_stopped", diagnostic.GetProperty("event").GetString());
            Assert.Equal("codex", diagnostic.GetProperty("adapter").GetString());
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
        ["OVERMIND_CAPTURE_RUN_ONCE"] = "true",
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
