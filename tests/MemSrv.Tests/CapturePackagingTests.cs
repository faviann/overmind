using System.Text.Json;
using CaptureAdapters;

namespace MemSrv.Tests;

public sealed class CapturePackagingTests
{
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
}
