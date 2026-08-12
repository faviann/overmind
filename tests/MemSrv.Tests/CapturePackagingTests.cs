using System.Text.Json;

namespace MemSrv.Tests;

public sealed class CapturePackagingTests
{
    [Fact]
    public async Task ProductionRuntimeStartsWithoutSyntheticGateAndKeepsStdoutEmpty()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-production-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
                    ["OVERMIND_CODEX_SESSIONS_ROOT"] = root,
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
        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
                    ["OVERMIND_CODEX_SESSIONS_ROOT"] = missingRoot,
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

    [Fact]
    public void ReferenceRuntimeComposeHasOnlyTheDeclaredHostAndDurableStateAccess()
    {
        string compose = File.ReadAllText(Path.Combine(
            TestProcessRunner.RepoRoot, "compose.capture.yaml"));

        Assert.Contains("read_only: true", compose, StringComparison.Ordinal);
        Assert.Contains("OVERMIND_CODEX_SESSIONS_ROOT", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERMIND_CODEX_TRANSCRIPT_ROOT", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("/.codex\n", compose, StringComparison.Ordinal);
        Assert.Contains("OVERMIND_CAPTURE_STATE_DIR", compose, StringComparison.Ordinal);
        Assert.Contains("OVERMIND_CAPTURE_URL", compose, StringComparison.Ordinal);
        Assert.Contains("OVERMIND_CAPTURE_CREDENTIAL", compose, StringComparison.Ordinal);
        Assert.Contains("cap_drop: [ALL]", compose, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges:true", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("privileged:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("CONNECTION_STRING", compose, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", compose, StringComparison.Ordinal);

        string dockerfile = File.ReadAllText(Path.Combine(
            TestProcessRunner.RepoRoot, "Dockerfile.capture-runtime"));
        Assert.Contains("ARG VERSION", dockerfile, StringComparison.Ordinal);
        Assert.Contains("CodexCaptureTracer", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("Claude", dockerfile, StringComparison.OrdinalIgnoreCase);
    }
}
