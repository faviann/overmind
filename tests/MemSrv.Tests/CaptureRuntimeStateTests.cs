using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CaptureAdapters;
using MemSrv.Core;

namespace MemSrv.Tests;

public sealed class CaptureRuntimeStateTests
{
    [Fact]
    public async Task CompletedRecordIsDurablyClaimedOnlyAfterLocalSanitization()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-state-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        const string seededSyntheticSecret = "AKIAIOSFODNN7EXAMPLE";
        await File.WriteAllTextAsync(
            transcript,
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "user",
                    content = seededSyntheticSecret
                }
            }) + "\n",
            new UTF8Encoding(false));

        try
        {
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            var claims = await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "codex-runtime-state-test",
                state,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")));

            var claim = Assert.Single(claims);
            Assert.Equal(0, claim.SourcePosition);
            Assert.DoesNotContain(seededSyntheticSecret, claim.CandidateObservationJson);
            Assert.Contains("[REDACTED:aws-access-key-id]", claim.CandidateObservationJson);

            CaptureRuntimeSnapshot snapshot = await state.ReadAsync();
            var stream = Assert.Single(snapshot.Streams);
            Assert.Equal(0, stream.EnqueuedThrough);
            Assert.NotNull(stream.VerifiedPrefix);
            var queued = Assert.Single(stream.Queue);
            Assert.Equal(claim.LocatorIdentity, queued.LocatorIdentity);
            Assert.Equal("codex-runtime-state-test", queued.SourceStream);
            Assert.Equal(0, queued.SourcePosition);
            Assert.DoesNotContain(seededSyntheticSecret, queued.CandidateObservationJson);
            Assert.DoesNotContain(
                seededSyntheticSecret,
                await File.ReadAllTextAsync(Path.Combine(directory, "state", "capture-state.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessTerminationAfterClaimAndRestartLeaveExactlyTheSameRetryableClaims()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-restart-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var environment = new Dictionary<string, string>
        {
            ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
            ["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}",
            ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
            ["OVERMIND_CODEX_FIXTURE"] = transcript,
            ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory
        };

        try
        {
            using (var first = TestProcessRunner.StartCaptureTracer(environment))
            {
                Task<string> stdout = first.StandardOutput.ReadToEndAsync();
                Task<string> stderr = first.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using TcpClient delivery = await listener.AcceptTcpClientAsync(timeout.Token);

                // Delivery cannot start until every completed source record has
                // crossed the claim transaction. Terminate the packaged
                // process while the first request is blocked without a
                // response, immediately after the durable enqueue boundary.
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync();
                Assert.Empty(await stdout);
                await stderr;
            }

            var state = new FileCaptureRuntimeState(stateDirectory);
            CaptureRuntimeSnapshot afterFailure = await state.ReadAsync();
            var failedStream = Assert.Single(afterFailure.Streams);
            Assert.Equal(2, failedStream.EnqueuedThrough);
            Assert.Equal(3, failedStream.Queue.Count);
            Assert.Null(failedStream.LastServerReceipt);

            listener.Stop();
            var restarted = await TestProcessRunner.RunCaptureTracerToExitAsync(environment);
            Assert.NotEqual(0, restarted.ExitCode);
            Assert.Empty(restarted.Stdout);
            CaptureRuntimeSnapshot afterRestart = await state.ReadAsync();
            Assert.Equal(
                JsonSerializer.Serialize(afterFailure),
                JsonSerializer.Serialize(afterRestart));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaimConflictCannotAdvancePastTheDurableQueue()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-atomic-{Guid.NewGuid():N}");
        try
        {
            var state = new FileCaptureRuntimeState(directory);
            var prefix = new CapturePrefixEvidence(10, new string('a', 64));
            var claim = new CaptureRuntimeClaim(
                "stream",
                new string('b', 64),
                0,
                0,
                10,
                new string('c', 64),
                prefix,
                new string('d', 64),
                """{"safe":"candidate"}""");

            await Assert.ThrowsAsync<CaptureRuntimeConcurrencyException>(() =>
                state.ClaimAsync(
                    claim,
                    new CapturePrefixEvidence(1, new string('e', 64))));

            Assert.Empty((await state.ReadAsync()).Streams);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnterminatedFinalRecordRemainsWhollyUnclaimed()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-incomplete-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"response_item","payload":{"type":"message","role":"user","content":"still writing"}}""",
            new UTF8Encoding(false));

        try
        {
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            Assert.Empty(await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "incomplete-stream",
                state,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"))));
            Assert.Empty((await state.ReadAsync()).Streams);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
