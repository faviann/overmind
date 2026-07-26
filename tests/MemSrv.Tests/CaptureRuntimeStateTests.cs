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
    public void LocatorEvidenceIdentityBindsEveryMechanicalComponent()
    {
        var baseline = new CaptureRuntimeLocatorEvidence(
            "transcript", 7, 11, 13, "record",
            new CapturePrefixEvidence(24, "prefix"));

        Assert.Equal(
            "87c6b278198689495a3d56ecbc0dba5748ff6588e825a0a7f74ea07a55fa895f",
            baseline.Identity);
        Assert.Equal(
            baseline.Identity,
            new CaptureRuntimeLocatorEvidence(
                "transcript", 7, 11, 13, "record",
                new CapturePrefixEvidence(24, "prefix")).Identity);

        CaptureRuntimeLocatorEvidence[] changed =
        [
            new("other-transcript", 7, 11, 13, "record", new(24, "prefix")),
            new("transcript", 8, 11, 13, "record", new(24, "prefix")),
            new("transcript", 7, 12, 13, "record", new(24, "prefix")),
            new("transcript", 7, 11, 14, "record", new(24, "prefix")),
            new("transcript", 7, 11, 13, "other-record", new(24, "prefix")),
            new("transcript", 7, 11, 13, "record", new(25, "prefix")),
            new("transcript", 7, 11, 13, "record", new(24, "other-prefix"))
        ];
        Assert.All(changed, evidence => Assert.NotEqual(baseline.Identity, evidence.Identity));
    }

    [Fact]
    public void QueueItemCannotDeserializeWithAContradictorySourcePosition()
    {
        const string contradictory = """
            {
              "sourceStream": "stream",
              "sourcePosition": 8,
              "deterministicLocatorEvidence": {
                "transcriptIdentity": "transcript",
                "sourcePosition": 7,
                "byteOffset": 11,
                "byteLength": 13,
                "recordSha256": "record",
                "prefixEvidence": { "byteLength": 24, "sha256": "prefix" }
              },
              "redactedSafeCandidate": "{\"safe\":true}"
            }
            """;

        var exception = Assert.Throws<InvalidDataException>(() =>
            JsonSerializer.Deserialize<CaptureRuntimeQueueItem>(
                contradictory,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(
            "Queued sourcePosition must match deterministic locator evidence.",
            exception.Message);
    }

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
            Assert.DoesNotContain(seededSyntheticSecret, claim.RedactedSafeCandidate);
            Assert.Contains("[REDACTED:aws-access-key-id]", claim.RedactedSafeCandidate);

            CaptureRuntimeSnapshot snapshot = await state.ReadAsync();
            var stream = Assert.Single(snapshot.Streams);
            Assert.Equal(0, stream.EnqueuedThrough);
            Assert.NotNull(stream.VerifiedPrefix);
            var queued = Assert.Single(stream.Queue);
            Assert.Equal(
                claim.DeterministicLocatorEvidence.Identity,
                queued.DeterministicLocatorEvidence.Identity);
            Assert.Equal("codex-runtime-state-test", queued.SourceStream);
            Assert.Equal(0, queued.SourcePosition);
            Assert.DoesNotContain(seededSyntheticSecret, queued.RedactedSafeCandidate);
            using JsonDocument stateDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "state", "capture-state.json")));
            JsonElement persistedQueueItem = Assert.Single(
                stateDocument.RootElement.GetProperty("streams")[0].GetProperty("queue")
                    .EnumerateArray());
            Assert.Equal(
                [
                    "sourceStream",
                    "sourcePosition",
                    "deterministicLocatorEvidence",
                    "redactedSafeCandidate"
                ],
                persistedQueueItem.EnumerateObject().Select(property => property.Name));
            JsonElement persistedLocatorEvidence =
                persistedQueueItem.GetProperty("deterministicLocatorEvidence");
            Assert.Equal(
                [
                    "transcriptIdentity",
                    "sourcePosition",
                    "byteOffset",
                    "byteLength",
                    "recordSha256",
                    "prefixEvidence",
                    "identity"
                ],
                persistedLocatorEvidence.EnumerateObject().Select(property => property.Name));
            Assert.Equal(
                ["byteLength", "sha256"],
                persistedLocatorEvidence.GetProperty("prefixEvidence")
                    .EnumerateObject()
                    .Select(property => property.Name));
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
            var claim = new CaptureRuntimeQueueItem(
                "stream",
                new CaptureRuntimeLocatorEvidence(
                    new string('b', 64),
                    0,
                    0,
                    10,
                    new string('c', 64),
                    prefix),
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
    public async Task DuplicateLocatorDoesNotDuplicateResponsibilityOrAdvanceProgress()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-duplicate-{Guid.NewGuid():N}");
        try
        {
            var state = new FileCaptureRuntimeState(directory);
            var prefix = new CapturePrefixEvidence(10, new string('a', 64));
            var claim = new CaptureRuntimeQueueItem(
                "stream",
                new CaptureRuntimeLocatorEvidence(
                    new string('b', 64), 0, 0, 10, new string('c', 64), prefix),
                """{"safe":"candidate"}""");

            Assert.True(await state.ClaimAsync(claim, expectedPrefix: null));
            CaptureRuntimeSnapshot once = await state.ReadAsync();
            Assert.False(await state.ClaimAsync(claim, prefix));
            CaptureRuntimeSnapshot twice = await state.ReadAsync();

            Assert.Equal(JsonSerializer.Serialize(once), JsonSerializer.Serialize(twice));
            CaptureRuntimeStreamState stream = Assert.Single(twice.Streams);
            Assert.Equal(0, stream.EnqueuedThrough);
            Assert.Single(stream.Queue);
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
    public async Task KillingPackagedTracerDuringRealStateTempWriteLeavesAtomicClaimSnapshot()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-kill-write-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(stateDirectory);
        string fixture = await File.ReadAllTextAsync(
            Path.Combine(root, "fixtures/codex-synthetic.jsonl"));
        fixture = fixture.Replace(
            "Show the working directory.",
            new string('x', 700_000),
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(transcript, fixture, new UTF8Encoding(false));

        var tempObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(stateDirectory, ".capture-state.json.*.tmp")
        {
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => tempObserved.TrySetResult();

        try
        {
            using var process = TestProcessRunner.StartCaptureTracer(
                TracerEnvironment(transcript, stateDirectory, port: 1));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await tempObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);

            CaptureRuntimeSnapshot snapshot =
                await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
            if (snapshot.Streams.Count == 0)
            {
                return;
            }

            CaptureRuntimeStreamState stream = Assert.Single(snapshot.Streams);
            Assert.NotEmpty(stream.Queue);
            Assert.Equal(stream.Queue.Count - 1, stream.EnqueuedThrough);
            Assert.Equal(
                Enumerable.Range(0, stream.Queue.Count).Select(value => (long)value),
                stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(
                stream.VerifiedPrefix,
                stream.Queue[^1].DeterministicLocatorEvidence.PrefixEvidence);
            Assert.Null(stream.LastServerReceipt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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

    [Fact]
    public async Task PackagedTracerDoesNotDeliverAnUnterminatedFinalRecord()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-undelivered-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        string fixture = await File.ReadAllTextAsync(
            Path.Combine(root, "fixtures/codex-synthetic.jsonl"));
        await File.WriteAllTextAsync(
            transcript,
            fixture.TrimEnd('\n'),
            new UTF8Encoding(false));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> server = ServeResponsesAsync(
            listener,
            [
                (HttpStatusCode.OK, Receipt(0)),
                (HttpStatusCode.OK, Receipt(1)),
                (HttpStatusCode.OK, Receipt(2))
            ],
            serverCancellation.Token);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));
            await serverCancellation.CancelAsync();

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, await server);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(1, stream.EnqueuedThrough);
            Assert.Equal([0L, 1L], stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(1, stream.LastServerReceipt?.SourcePosition);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerPersistsEachReceiptBeforeAttemptingTheNextDelivery()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-receipt-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Guid firstObservation = Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> server = ServeResponsesAsync(
            listener,
            [
                (HttpStatusCode.OK, Receipt(0, firstObservation)),
                (HttpStatusCode.InternalServerError, """{"error":"later failure"}""")
            ],
            serverCancellation.Token);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(2, await server);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(2, stream.EnqueuedThrough);
            Assert.Equal([0L, 1L, 2L], stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(0, stream.LastServerReceipt?.SourcePosition);
            Assert.Equal("new", stream.LastServerReceipt?.Status);
            Assert.Equal(firstObservation, stream.LastServerReceipt?.ObservationUuid);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            listener.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerRejectsReceiptForAnotherSourcePosition()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-receipt-position-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Guid acceptedObservation = Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> server = ServeResponsesAsync(
            listener,
            [
                (HttpStatusCode.OK, Receipt(0, acceptedObservation)),
                (HttpStatusCode.OK, Receipt(2))
            ],
            serverCancellation.Token);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(2, await server);
            Assert.Contains(
                "does not match queued sourcePosition 1",
                result.Stderr,
                StringComparison.Ordinal);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(0, stream.LastServerReceipt?.SourcePosition);
            Assert.Equal(acceptedObservation, stream.LastServerReceipt?.ObservationUuid);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            listener.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"sourcePosition":1,"status":"   ","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5"}""")]
    [InlineData("""{"sourcePosition":1,"status":"new"}""")]
    [InlineData("""{"sourcePosition":1,"status":"new","observationUuid":"not-a-uuid"}""")]
    public async Task PackagedTracerRejectsMalformedSuccessfulReceiptWithoutReplacingLastValidReceipt(
        string malformedReceipt)
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-receipt-malformed-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Guid acceptedObservation = Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> server = ServeResponsesAsync(
            listener,
            [
                (HttpStatusCode.OK, Receipt(0, acceptedObservation)),
                (HttpStatusCode.OK, malformedReceipt),
                (HttpStatusCode.InternalServerError, """{"error":"unexpected delivery"}""")
            ],
            serverCancellation.Token);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));
            await serverCancellation.CancelAsync();

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(2, await server);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(0, stream.LastServerReceipt?.SourcePosition);
            Assert.Equal("new", stream.LastServerReceipt?.Status);
            Assert.Equal(acceptedObservation, stream.LastServerReceipt?.ObservationUuid);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            listener.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<string, string> TracerEnvironment(
        string transcript,
        string stateDirectory,
        int port) => new()
        {
            ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
            ["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{port}",
            ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
            ["OVERMIND_CODEX_FIXTURE"] = transcript,
            ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory
        };

    private static string Receipt(long sourcePosition, Guid? observationUuid = null) =>
        JsonSerializer.Serialize(new
        {
            sourcePosition,
            status = "new",
            observationUuid = observationUuid ?? Guid.NewGuid()
        });

    private static async Task<int> ServeResponsesAsync(
        TcpListener listener,
        IReadOnlyList<(HttpStatusCode Status, string Body)> responses,
        CancellationToken cancellationToken)
    {
        int requestCount = 0;
        try
        {
            foreach (var (status, body) in responses)
            {
                using TcpClient client =
                    await listener.AcceptTcpClientAsync(cancellationToken);
                await using NetworkStream stream = client.GetStream();
                await ReadRequestAsync(stream, cancellationToken);
                requestCount++;
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                byte[] response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(int)status} {status}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                await stream.WriteAsync(bodyBytes, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return requestCount;
    }

    private static async Task ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        byte[] oneByte = new byte[1];
        while (header.Count < 64 * 1024)
        {
            int read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("HTTP request ended before its headers.");
            }
            header.Add(oneByte[0]);
            int count = header.Count;
            if (count >= 4
                && header[count - 4] == '\r'
                && header[count - 3] == '\n'
                && header[count - 2] == '\r'
                && header[count - 1] == '\n')
            {
                break;
            }
        }

        string headerText = Encoding.ASCII.GetString([.. header]);
        string contentLengthHeader = headerText
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        int contentLength = int.Parse(
            contentLengthHeader["Content-Length:".Length..].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        byte[] body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, cancellationToken);
    }
}
