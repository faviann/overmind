using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaptureAdapters;
using MemSrv.Core;

namespace MemSrv.Tests;

public sealed class CaptureRuntimeStateTests
{
    [Theory]
    [InlineData(
        409,
        """{"reason":"blocked_by_earlier_gap"}""",
        CaptureRuntimeStopCode.BlockedByEarlierGap)]
    [InlineData(
        409,
        """{"reason":"accepted_source_conflict"}""",
        CaptureRuntimeStopCode.AcceptedSourceConflict)]
    [InlineData(409, """{"reason":"unknown_conflict"}""", null)]
    [InlineData(409, """{"reason":null}""", null)]
    [InlineData(409, """{"reason":""", null)]
    [InlineData(400, """{"reason":"blocked_by_earlier_gap"}""", null)]
    [InlineData(401, """{"reason":"accepted_source_conflict"}""", null)]
    [InlineData(408, """{"reason":"blocked_by_earlier_gap"}""", null)]
    [InlineData(429, """{"reason":"accepted_source_conflict"}""", null)]
    [InlineData(500, """{"reason":"blocked_by_earlier_gap"}""", null)]
    [InlineData(503, """{"reason":"accepted_source_conflict"}""", null)]
    public void HttpFailureStopsOnlyForRecognizedConflictReasons(
        int statusCode,
        string responseBody,
        string? expectedStopCode)
    {
        CaptureRuntimeStopState? stop =
            CaptureRuntimeConflictClassifier.FromHttpFailure(
                7,
                (HttpStatusCode)statusCode,
                responseBody);

        Assert.Equal(expectedStopCode, stop?.Code);
        Assert.Equal(expectedStopCode is null ? null : 7L, stop?.SourcePosition);
    }

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

    [Theory]
    [MemberData(nameof(InvalidStopStateShapes))]
    public void CaptureRuntimeStopStateContractRejectsInvalidConstructorAndJsonShapes(
        string code,
        long? sourcePosition,
        string durableJson)
    {
        Assert.Throws<InvalidDataException>(() =>
            new CaptureRuntimeStopState(code, sourcePosition));
        Assert.Throws<InvalidDataException>(() =>
            JsonSerializer.Deserialize<CaptureRuntimeStopState>(
                durableJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData(CaptureRuntimeStopCode.VerifiedPrefixChanged, null, "code")]
    [InlineData(CaptureRuntimeStopCode.TranscriptIdentityChanged, null, "code")]
    [InlineData(
        CaptureRuntimeStopCode.QueuedSourceEvidenceChanged,
        0L,
        "code,sourcePosition")]
    [InlineData(CaptureRuntimeStopCode.BlockedByEarlierGap, 1L, "code,sourcePosition")]
    [InlineData(CaptureRuntimeStopCode.AcceptedSourceConflict, 2L, "code,sourcePosition")]
    public void CaptureRuntimeStopStateContractSerializesOnlyItsDurableFields(
        string code,
        long? sourcePosition,
        string expectedPropertyNames)
    {
        var stop = new CaptureRuntimeStopState(code, sourcePosition);
        string durableJson = JsonSerializer.Serialize(
            stop,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using JsonDocument document = JsonDocument.Parse(durableJson);
        Assert.Equal(
            expectedPropertyNames.Split(','),
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            stop,
            JsonSerializer.Deserialize<CaptureRuntimeStopState>(
                durableJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    public static TheoryData<string, long?, string> InvalidStopStateShapes =>
        new()
        {
            {
                "unknown_stop_code",
                null,
                """{"code":"unknown_stop_code"}"""
            },
            {
                CaptureRuntimeStopCode.VerifiedPrefixChanged,
                0,
                """{"code":"verified_prefix_changed","sourcePosition":0}"""
            },
            {
                CaptureRuntimeStopCode.TranscriptIdentityChanged,
                0,
                """{"code":"transcript_identity_changed","sourcePosition":0}"""
            },
            {
                CaptureRuntimeStopCode.QueuedSourceEvidenceChanged,
                null,
                """{"code":"queued_source_evidence_changed","sourcePosition":null}"""
            },
            {
                CaptureRuntimeStopCode.QueuedSourceEvidenceChanged,
                null,
                """{"code":"queued_source_evidence_changed"}"""
            },
            {
                CaptureRuntimeStopCode.BlockedByEarlierGap,
                null,
                """{"code":"blocked_by_earlier_gap","sourcePosition":null}"""
            },
            {
                CaptureRuntimeStopCode.BlockedByEarlierGap,
                null,
                """{"code":"blocked_by_earlier_gap"}"""
            },
            {
                CaptureRuntimeStopCode.AcceptedSourceConflict,
                null,
                """{"code":"accepted_source_conflict","sourcePosition":null}"""
            },
            {
                CaptureRuntimeStopCode.AcceptedSourceConflict,
                null,
                """{"code":"accepted_source_conflict"}"""
            },
            {
                CaptureRuntimeStopCode.QueuedSourceEvidenceChanged,
                -1,
                """{"code":"queued_source_evidence_changed","sourcePosition":-1}"""
            },
            {
                CaptureRuntimeStopCode.BlockedByEarlierGap,
                -1,
                """{"code":"blocked_by_earlier_gap","sourcePosition":-1}"""
            },
            {
                CaptureRuntimeStopCode.AcceptedSourceConflict,
                -1,
                """{"code":"accepted_source_conflict","sourcePosition":-1}"""
            }
        };

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
    public async Task RecordBeyondTransportLimitIsDurablyClaimedAsContentFreeObservationOmission()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-omission-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        const string rawPayload = "RAW-PAYLOAD-MUST-NOT-ENTER-DURABLE-STATE";
        string record = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "message",
                role = "user",
                content = string.Concat(Enumerable.Repeat(rawPayload, 100))
            }
        });
        await File.WriteAllTextAsync(transcript, record + "\n", new UTF8Encoding(false));

        try
        {
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            var claims = await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "codex-runtime-transport-omission",
                state,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                maxTransportBytes: 1_024);

            var claim = Assert.Single(claims);
            Assert.DoesNotContain(rawPayload, claim.RedactedSafeCandidate);
            using var candidate = JsonDocument.Parse(claim.RedactedSafeCandidate);
            JsonElement omission = candidate.RootElement
                .GetProperty("sourcePayload")
                .GetProperty("omission");
            Assert.Equal(
                "observation_exceeds_transport_limit",
                omission.GetProperty("reason").GetString());
            Assert.Equal(
                CaptureFidelityPolicy.CurrentVersion,
                omission.GetProperty("policyVersion").GetString());
            var originalOutcome = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                new CodexJsonlAdapter().Adapt(
                    Assert.Single(JsonlSourceReader.Read(
                        Encoding.UTF8.GetBytes(record + "\n"),
                        "codex-runtime-transport-omission",
                        terminalAtEndOfFile: false))));
            Assert.Equal(
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                    originalOutcome.Observation,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                omission.GetProperty("originalByteCount").GetInt64());
            JsonElement sourceIdentity = omission.GetProperty("sourceIdentity");
            Assert.Equal(
                "codex-runtime-transport-omission",
                sourceIdentity.GetProperty("externalSessionId").GetString());
            Assert.Equal(
                0,
                sourceIdentity.GetProperty("sourcePosition").GetInt64());
            Assert.Equal(
                "byte_range",
                sourceIdentity.GetProperty("locatorKind").GetString());
            Assert.Single(candidate.RootElement.GetProperty("events").EnumerateArray());

            string durableState = await File.ReadAllTextAsync(
                Path.Combine(directory, "state", "capture-state.json"));
            Assert.DoesNotContain(rawPayload, durableState);
            Assert.Contains("observation_exceeds_transport_limit", durableState);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitBinaryMediaBytesAreOmittedBeforeDurableClaimAndRetriesConverge()
    {
        string root = TestProcessRunner.RepoRoot;
        string fixture = Path.Combine(
            root,
            "fixtures/adapter-conformance/codex-cli-0.146.binary-media.synthetic.jsonl");
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-binary-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            var gate = new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"));
            var adapter = new CodexJsonlAdapter();

            IReadOnlyList<CaptureRuntimeQueueItem> first =
                await CodexCaptureClaimer.ClaimCompletedAsync(
                    adapter,
                    fixture,
                    "binary-media-runtime",
                    state,
                    gate,
                    terminalAtEndOfFile: true);
            IReadOnlyList<CaptureRuntimeQueueItem> retry =
                await CodexCaptureClaimer.ClaimCompletedAsync(
                    adapter,
                    fixture,
                    "binary-media-runtime",
                    state,
                    gate,
                    terminalAtEndOfFile: true);

            Assert.Equal(11, first.Count);
            Assert.Empty(retry);
            CaptureRuntimeStreamState durable = Assert.Single(
                (await state.ReadAsync()).Streams);
            Assert.Equal(11, durable.Queue.Count);
            using JsonDocument queued = JsonDocument.Parse(
                durable.Queue[0].RedactedSafeCandidate);
            JsonElement block = queued.RootElement.GetProperty("sourcePayload")
                .GetProperty("payload").GetProperty("content")[0];
            Assert.False(block.TryGetProperty("byte_payload", out _));
            Assert.Equal(
                CaptureFidelityPolicy.UnsupportedBinaryReason,
                block.GetProperty("capture_fidelity_omission")
                    .GetProperty("reason").GetString());
            Assert.Equal(4, block.GetProperty("capture_fidelity_omission")
                .GetProperty("originalByteCount").GetInt64());
            JsonElement sourceIdentity = block
                .GetProperty("capture_fidelity_omission")
                .GetProperty("sourceIdentity");
            Assert.Equal(
                "binary-media-runtime",
                sourceIdentity.GetProperty("externalSessionId").GetString());
            Assert.Equal(0, sourceIdentity.GetProperty("sourcePosition").GetInt64());
            Assert.Equal(
                "byte_range",
                sourceIdentity.GetProperty("locatorKind").GetString());
            Assert.Equal("Visible attachment caption.", block.GetProperty("text").GetString());

            string durableJson = await File.ReadAllTextAsync(
                Path.Combine(directory, "state", "capture-state.json"));
            Assert.DoesNotContain("\"byte_payload\":[1,2,3,4]", durableJson);
            Assert.DoesNotContain(
                "\"digest\"",
                block.GetProperty("capture_fidelity_omission").GetRawText());
            Assert.DoesNotContain(
                "\"excerpt\"",
                block.GetProperty("capture_fidelity_omission").GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NativeRecordBeyondTransportLimitFailsClosedAtTheFidelityPolicy()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            padding = new string('p', 4_096)
        });
        CaptureObservationRequest observation = ResourceBoundObservation(
            payload,
            "native-overlimit-policy",
            new CaptureLocator(
                "native_id",
                "stable-native-locator",
                null,
                null,
                null));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => CaptureFidelityPolicy.SerializeForTransport(observation, 1_024));

        Assert.Contains("native_id", failure.Message, StringComparison.Ordinal);
        Assert.Contains("fails closed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeRecordBeyondTransportLimitClaimsNothingAndPersistsNoRawContent()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-native-overlimit-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"synthetic"}""" + "\n",
            new UTF8Encoding(false));

        try
        {
            const string rawSentinel = "NATIVE-OVERLIMIT-RAW-MUST-NOT-PERSIST";
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));

            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => CodexCaptureClaimer.ClaimCompletedAsync(
                        new NativeTransportPaddingAdapter(rawSentinel),
                        transcript,
                        "native-overlimit-runtime",
                        state,
                        new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                        maxTransportBytes: 1_024));

            Assert.Contains("native_id", failure.Message, StringComparison.Ordinal);
            Assert.Empty((await state.ReadAsync()).Streams);
            string statePath = Path.Combine(directory, "state", "capture-state.json");
            if (File.Exists(statePath))
            {
                Assert.DoesNotContain(
                    rawSentinel,
                    await File.ReadAllTextAsync(statePath),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RequestedTransportBoundCannotQueueAProductionCapPlusOneObservationRaw()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-fixed-cap-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"synthetic"}""" + "\n",
            new UTF8Encoding(false));

        try
        {
            TrustedSourceObservation source = Assert.Single(JsonlSourceReader.Read(
                await File.ReadAllBytesAsync(transcript),
                "fixed-cap-stream",
                terminalAtEndOfFile: false));
            var unpaddedAdapter = new TransportPaddingAdapter(0);
            var unpadded = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                unpaddedAdapter.Adapt(source));
            int unpaddedBytes = Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(
                    unpadded.Observation,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            int paddingLength =
                CaptureFidelityPolicy.ProductionTransportBytes + 1 - unpaddedBytes;
            Assert.True(paddingLength > 0);
            var adapter = new TransportPaddingAdapter(paddingLength);
            var exact = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                adapter.Adapt(source));
            Assert.Equal(
                CaptureFidelityPolicy.ProductionTransportBytes + 1,
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                    exact.Observation,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))));

            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            CaptureRuntimeQueueItem claim = Assert.Single(
                await CodexCaptureClaimer.ClaimCompletedAsync(
                    adapter,
                    transcript,
                    "fixed-cap-stream",
                    state,
                    new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                    maxTransportBytes: CaptureFidelityPolicy.ProductionTransportBytes * 2));

            Assert.True(
                Encoding.UTF8.GetByteCount(claim.RedactedSafeCandidate)
                <= CaptureFidelityPolicy.ProductionTransportBytes);
            Assert.Contains(
                CaptureFidelityPolicy.TransportLimitReason,
                claim.RedactedSafeCandidate,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                new string('p', 256),
                claim.RedactedSafeCandidate,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MandatoryIdentityOrLocatorThatCannotFitTransportBoundClaimsNothing(
        bool oversizedIdentity)
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-unfit-identity-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"synthetic"}""" + "\n",
            new UTF8Encoding(false));

        try
        {
            const string sentinel = "MANDATORY-RAW-VALUE-MUST-NOT-BE-PERSISTED";
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => CodexCaptureClaimer.ClaimCompletedAsync(
                        new OversizedMandatoryFieldAdapter(sentinel, oversizedIdentity),
                        transcript,
                        "mandatory-field-stream",
                        state,
                        new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                        maxTransportBytes: 512));

            Assert.Contains("cannot fit", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, failure.ToString(), StringComparison.Ordinal);
            Assert.Empty((await state.ReadAsync()).Streams);
            string statePath = Path.Combine(directory, "state", "capture-state.json");
            if (File.Exists(statePath))
            {
                Assert.DoesNotContain(
                    sentinel,
                    await File.ReadAllTextAsync(statePath),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConflictingDualIdentityBeyondTransportLimitClaimsNothing()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-conflicting-identity-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"synthetic"}""" + "\n",
            new UTF8Encoding(false));

        try
        {
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));

            ArgumentException failure =
                await Assert.ThrowsAsync<ArgumentException>(
                    () => CodexCaptureClaimer.ClaimCompletedAsync(
                        new ConflictingDualIdentityAdapter(),
                        transcript,
                        "transport-identity",
                        state,
                        new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                        maxTransportBytes: 512));

            Assert.Contains(
                "sourceSessionId must match sourceIdentity.externalSessionId",
                failure.Message,
                StringComparison.Ordinal);
            Assert.Empty((await state.ReadAsync()).Streams);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PathologicalTransportInputIsCountedWithBoundedAllocationAndTime()
    {
        JsonElement smallPayload =
            JsonSerializer.SerializeToElement(new { padding = "warm" });
        CaptureObservationRequest warm =
            ResourceBoundObservation(smallPayload, "resource-bound-warm");
        CaptureFidelityPolicy.SerializeForTransport(warm, 1_024);

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            padding = new string('p', 8 * 1024 * 1024)
        });
        CaptureObservationRequest pathological =
            ResourceBoundObservation(payload, "resource-bound-pathological");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();

        BoundedCaptureRepresentation<CaptureObservationRequest> bounded =
            CaptureFidelityPolicy.SerializeForTransport(pathological, 1_024);

        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bounded.WasOmitted);
        Assert.True(
            allocated < 4L * 1024 * 1024,
            $"Streaming count allocated {allocated:N0} bytes; the bounded " +
            "counting path should not materialize the 16 MiB original JSON.");
        Assert.True(
            clock.Elapsed < SafetyBudgets.Default.MaxScanTime,
            $"Streaming count took {clock.Elapsed}; the published deadline is " +
            $"{SafetyBudgets.Default.MaxScanTime}.");
    }

    [Fact]
    public void PathologicalBinaryPayloadIsRewrittenWithinTransportAllocationAndDeadline()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            type = "response_item",
            payload = new
            {
                type = "message",
                content = new[]
                {
                    new
                    {
                        type = "binary_content",
                        category = "attachment",
                        byte_payload = new int[8 * 1024 * 1024]
                    }
                }
            }
        });
        CaptureFidelityPolicy.OmitUnsupportedBinaryContent(
            JsonSerializer.SerializeToElement(new { type = "warm" }),
            "codex",
            new CaptureSourceIdentity("warm-session"),
            0,
            "byte_range",
            CaptureFidelityPolicy.ProductionTransportBytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();

        BinaryFidelitySelection<JsonElement> selected =
            CaptureFidelityPolicy.OmitUnsupportedBinaryContent(
                payload,
                "codex",
                new CaptureSourceIdentity("pathological-session", "child-1"),
                17,
                "byte_range",
                CaptureFidelityPolicy.ProductionTransportBytes);

        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(selected.WasOmitted);
        Assert.True(
            allocated < 4L * 1024 * 1024,
            $"Bounded binary rewriting allocated {allocated:N0} bytes.");
        Assert.True(
            clock.Elapsed < SafetyBudgets.Default.MaxScanTime,
            $"Binary rewriting took {clock.Elapsed}; the published deadline is " +
            $"{SafetyBudgets.Default.MaxScanTime}.");
        JsonElement block = selected.Observation.GetProperty("payload")
            .GetProperty("content")[0];
        Assert.False(block.TryGetProperty("byte_payload", out _));
        Assert.Equal(
            8L * 1024 * 1024,
            block.GetProperty("capture_fidelity_omission")
                .GetProperty("originalByteCount").GetInt64());
        JsonElement sourceIdentity = block.GetProperty("capture_fidelity_omission")
            .GetProperty("sourceIdentity");
        Assert.Equal(
            "pathological-session",
            sourceIdentity.GetProperty("externalSessionId").GetString());
        Assert.Equal("child-1", sourceIdentity.GetProperty("childId").GetString());
        Assert.Equal(17, sourceIdentity.GetProperty("sourcePosition").GetInt64());
        Assert.Equal("byte_range", sourceIdentity.GetProperty("locatorKind").GetString());
    }

    [Fact]
    public void BinaryOmissionRecognitionAcceptsOnlyPolicyOwnedFieldNames()
    {
        CaptureObservationCommand Command(JsonElement payload) => new(
            1,
            new CaptureSourceIdentity("recognition-session"),
            0,
            new CaptureSourceLocator.NativeId("recognition-record"),
            null,
            new CaptureSource("codex", "0.146.synthetic", "response_item"),
            new CaptureAdapter("codex-synthetic-jsonl", "9"),
            payload,
            [
                new CaptureEvent(
                    "opaque/0",
                    0,
                    "opaque",
                    "unknown",
                    JsonSerializer.SerializeToElement(new { }),
                    null,
                    [])
            ],
            null);

        object omission = new
        {
            reason = CaptureFidelityPolicy.UnsupportedBinaryReason,
            category = "image",
            originalByteCount = 2,
            policyVersion = CaptureFidelityPolicy.CurrentVersion
        };
        Assert.False(CaptureFidelityPolicy.ContainsUnsupportedBinaryOmission(
            Command(JsonSerializer.SerializeToElement(new
            {
                capture_fidelity_omission_note = omission
            }))));
        Assert.True(CaptureFidelityPolicy.ContainsUnsupportedBinaryOmission(
            Command(JsonSerializer.SerializeToElement(new
            {
                capture_fidelity_omission = omission
            }))));
        Assert.True(CaptureFidelityPolicy.ContainsUnsupportedBinaryOmission(
            Command(JsonSerializer.SerializeToElement(new
            {
                capture_fidelity_omission = new { source = "collision" },
                capture_fidelity_omission_1 = omission
            }))));
        Assert.False(CaptureFidelityPolicy.ContainsUnsupportedBinaryOmission(
            Command(JsonSerializer.SerializeToElement(new
            {
                capture_fidelity_omission_2 = omission
            }))));
    }

    [Fact]
    public void MaterializationGrowthCannotReturnAnOverCapTransportRepresentation()
    {
        JsonElement small = JsonSerializer.SerializeToElement(new { value = "small" });
        JsonElement large = JsonSerializer.SerializeToElement(new
        {
            value = new string('x', 4_096)
        });
        var events = new StatefulEventList(
            new CaptureEvent("small/0", 0, "opaque", "harness", small, null, []),
            new CaptureEvent("large/0", 0, "opaque", "harness", large, null, []));
        CaptureObservationRequest observation = ResourceBoundObservation(
            small,
            "materialization-growth",
            new CaptureLocator(
                "byte_range",
                null,
                0,
                1,
                new string('a', 64))) with
        {
            Events = events
        };

        BoundedCaptureRepresentation<CaptureObservationRequest> bounded =
            CaptureFidelityPolicy.SerializeForTransport(observation, 1_024);

        Assert.True(bounded.WasOmitted);
        Assert.True(Encoding.UTF8.GetByteCount(bounded.Serialized) <= 1_024);
        Assert.Equal(
            CaptureFidelityPolicy.TransportLimitReason,
            bounded.Observation.SourcePayload.GetProperty("omission")
                .GetProperty("reason").GetString());
        Assert.True(bounded.OriginalByteCount > 1_024);
    }

    [Fact]
    public void MaterializedTransportSnapshotCannotDivergeFromBoundedSerialization()
    {
        JsonElement small = JsonSerializer.SerializeToElement(new { value = "small" });
        JsonElement medium = JsonSerializer.SerializeToElement(new
        {
            value = new string('m', 64)
        });
        JsonElement large = JsonSerializer.SerializeToElement(new
        {
            value = new string('x', 4_096)
        });
        var events = new StatefulEventList(
            new CaptureEvent("small/0", 0, "opaque", "harness", small, null, []),
            new CaptureEvent("medium/0", 0, "opaque", "harness", medium, null, []),
            new CaptureEvent("large/0", 0, "opaque", "harness", large, null, []));
        CaptureObservationRequest observation = ResourceBoundObservation(
            small,
            "materialization-snapshot") with
        {
            Events = events
        };

        BoundedCaptureRepresentation<CaptureObservationRequest> bounded =
            CaptureFidelityPolicy.SerializeForTransport(observation, 1_024);
        string returnedJson = JsonSerializer.Serialize(
            bounded.Observation,
            CaptureLedger.JsonOptions);

        Assert.False(bounded.WasOmitted);
        Assert.Equal(bounded.Serialized, returnedJson);
        Assert.True(Encoding.UTF8.GetByteCount(returnedJson) <= 1_024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveContentBoundIsRejected(long contentBound)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new { value = "safe" });
        CaptureObservationCommand command = CaptureObservationCommand.FromRequest(
            ResourceBoundObservation(payload, "invalid-content-bound"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CaptureFidelityPolicy.SerializeForContent(command, contentBound));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveTransportBoundIsRejectedBeforeDurableClaim(
        int transportBound)
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-invalid-bound-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"synthetic"}""" + "\n",
            new UTF8Encoding(false));

        try
        {
            var state = new FileCaptureRuntimeState(Path.Combine(directory, "state"));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => CodexCaptureClaimer.ClaimCompletedAsync(
                    new TransportPaddingAdapter(0),
                    transcript,
                    "invalid-bound-stream",
                    state,
                    new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                    maxTransportBytes: transportBound));
            Assert.Empty((await state.ReadAsync()).Streams);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyIdentityAndRetainedMetadataAreCompactedBeforeClaimAndDelivery()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-metadata-omission-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        const string rawSentinel = "RAW-RETAINED-METADATA-MUST-NOT-BE-QUEUED";
        await File.WriteAllTextAsync(
            transcript,
            """{"type":"synthetic"}""" + "\n",
            new UTF8Encoding(false));

        try
        {
            var state = new FileCaptureRuntimeState(stateDirectory);
            const int transportBound = 1_024;
            const string transcriptIdentity = "metadata-heavy-transcript";
            IReadOnlyList<CaptureRuntimeQueueItem> claims =
                await CodexCaptureClaimer.ClaimCompletedAsync(
                    new RetainedMetadataAdapter(rawSentinel),
                    transcript,
                    "metadata-heavy-stream",
                    state,
                    new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                    transcriptIdentity: transcriptIdentity,
                    maxTransportBytes: transportBound);

            CaptureRuntimeQueueItem claim = Assert.Single(claims);
            Assert.True(
                Encoding.UTF8.GetByteCount(claim.RedactedSafeCandidate) <= transportBound);
            Assert.DoesNotContain(rawSentinel, claim.RedactedSafeCandidate);
            using (var queued = JsonDocument.Parse(claim.RedactedSafeCandidate))
            {
                Assert.Equal(
                    "metadata-heavy-stream",
                    queued.RootElement.GetProperty("sourceIdentity")
                        .GetProperty("externalSessionId").GetString());
                Assert.Equal(
                    "metadata-heavy-stream",
                    queued.RootElement.GetProperty("sourcePayload")
                        .GetProperty("omission")
                        .GetProperty("sourceIdentity")
                        .GetProperty("externalSessionId").GetString());
            }
            string durableState = await File.ReadAllTextAsync(
                Path.Combine(stateDirectory, "capture-state.json"));
            Assert.DoesNotContain(rawSentinel, durableState);
            Assert.Equal(
                claim.RedactedSafeCandidate,
                Assert.Single(Assert.Single((await state.ReadAsync()).Streams).Queue)
                    .RedactedSafeCandidate);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<string> received = Task.Run(async () =>
            {
                using TcpClient delivery =
                    await listener.AcceptTcpClientAsync(timeout.Token);
                await using NetworkStream stream = delivery.GetStream();
                string requestBody = await ReadRequestAsync(stream, timeout.Token);
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                await stream.WriteAsync(response, timeout.Token);
                return requestBody;
            });

            await DisabledCaptureRuntime.RunClaimedFixtureAsync(
                new RetainedMetadataAdapter(rawSentinel),
                transcript,
                "metadata-heavy-stream",
                claims,
                new Uri($"http://127.0.0.1:{port}"),
                $"mcap_{Guid.NewGuid():N}",
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                (_, _, _) => Task.CompletedTask,
                transcriptIdentity: transcriptIdentity,
                maxTransportBytes: transportBound);
            string request = await received.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Encoding.UTF8.GetByteCount(request) <= transportBound);
            Assert.DoesNotContain(rawSentinel, request);
            using (var delivered = JsonDocument.Parse(request))
            {
                Assert.Equal(
                    "metadata-heavy-stream",
                    delivered.RootElement.GetProperty("sourceIdentity")
                        .GetProperty("externalSessionId").GetString());
                Assert.Equal(
                    JsonValueKind.Null,
                    delivered.RootElement.GetProperty("sourceSessionId").ValueKind);
            }
            Assert.Equal(
                claim.RedactedSafeCandidate,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"))
                    .ScanJson(request).Redacted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaimBuildsPrefixAndAdapterRecordsFromOneImmutableSourceSnapshot()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-snapshot-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        const string originalMarker = "immutable-original";
        const string replacementMarker = "immutable-changed!";
        Assert.Equal(originalMarker.Length, replacementMarker.Length);
        string original = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "message",
                role = "user",
                content = originalMarker
            }
        }) + "\n";
        string replacement = original.Replace(
            originalMarker, replacementMarker, StringComparison.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(original),
            Encoding.UTF8.GetByteCount(replacement));
        await File.WriteAllTextAsync(transcript, original, new UTF8Encoding(false));

        try
        {
            var inner = new FileCaptureRuntimeState(stateDirectory);
            var replacingState = new SourceInspectionObservingRuntimeState(
                inner,
                () => File.WriteAllText(
                    transcript, replacement, new UTF8Encoding(false)));

            IReadOnlyList<CaptureRuntimeQueueItem> claims =
                await CodexCaptureClaimer.ClaimCompletedAsync(
                    new CodexJsonlAdapter(),
                    transcript,
                    "stream",
                    replacingState,
                    new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")));

            CaptureRuntimeQueueItem claim = Assert.Single(claims);
            Assert.Contains(originalMarker, claim.RedactedSafeCandidate);
            Assert.DoesNotContain(replacementMarker, claim.RedactedSafeCandidate);
            Assert.Contains(replacementMarker, await File.ReadAllTextAsync(transcript));
            CaptureRuntimeStreamState persisted = Assert.Single(
                (await inner.ReadAsync()).Streams);
            Assert.Equal(0, persisted.EnqueuedThrough);
            Assert.Equal(claim, Assert.Single(persisted.Queue));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaimStopsWhenAnotherProcessEstablishesDifferentTranscriptIdentity()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-establish-race-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            transcript,
            CodexMessageRecord("same-history") + "\n",
            new UTF8Encoding(false));

        try
        {
            var inner = new FileCaptureRuntimeState(stateDirectory);
            var firstClaimEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new ClaimDelayingRuntimeState(
                inner, firstClaimEntered, releaseFirstClaim);
            var gate = new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"));

            Task<IReadOnlyList<CaptureRuntimeQueueItem>> first =
                CodexCaptureClaimer.ClaimCompletedAsync(
                    new CodexJsonlAdapter(),
                    transcript,
                    "stream",
                    delayed,
                    gate,
                    transcriptIdentity: "first-transcript");
            await firstClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                inner,
                gate,
                transcriptIdentity: "competing-transcript"));

            releaseFirstClaim.SetResult();
            CaptureStreamStoppedException stopped =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => first);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.TranscriptIdentityChanged,
                    null),
                stopped.Stop);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await inner.ReadAsync()).Streams);
            Assert.Equal(stopped.Stop, stream.Stop);
            Assert.Equal(0, stream.EnqueuedThrough);
            Assert.Single(stream.Queue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaimStopsWhenAnotherProcessAdvancesToChangedPrefix()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-prefix-race-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        string firstRecord = CodexMessageRecord("common");
        string claimantHistory =
            firstRecord + "\n" + CodexMessageRecord("claimant-history") + "\n";
        string competingHistory =
            firstRecord + "\n" + CodexMessageRecord("changed-history") + "\n";
        await File.WriteAllTextAsync(
            transcript, firstRecord + "\n", new UTF8Encoding(false));

        try
        {
            var inner = new FileCaptureRuntimeState(stateDirectory);
            var gate = new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"));
            Assert.Single(await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                inner,
                gate,
                transcriptIdentity: "transcript"));
            await File.WriteAllTextAsync(
                transcript, claimantHistory, new UTF8Encoding(false));
            var claimEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var callerCancellation = new CancellationTokenSource();
            var delayed = new ClaimDelayingRuntimeState(
                inner,
                claimEntered,
                releaseClaim,
                callerCancellation.Cancel);

            Task<IReadOnlyList<CaptureRuntimeQueueItem>> claimant =
                CodexCaptureClaimer.ClaimCompletedAsync(
                    new CodexJsonlAdapter(),
                    transcript,
                    "stream",
                    delayed,
                    gate,
                    callerCancellation.Token,
                    transcriptIdentity: "transcript");
            await claimEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(
                transcript, competingHistory, new UTF8Encoding(false));
            Assert.Single(await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                inner,
                gate,
                transcriptIdentity: "transcript"));

            releaseClaim.SetResult();
            CaptureStreamStoppedException stopped =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => claimant);
            Assert.True(callerCancellation.IsCancellationRequested);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.VerifiedPrefixChanged,
                    null),
                stopped.Stop);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await inner.ReadAsync()).Streams);
            Assert.Equal(stopped.Stop, stream.Stop);
            Assert.Equal(1, stream.EnqueuedThrough);
            Assert.Equal([0L, 1L], stream.Queue.Select(item => item.SourcePosition));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaimConvergesWhenAnotherProcessAdvancesTheSameHistory()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-benign-race-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        string firstRecord = CodexMessageRecord("common");
        string completeHistory =
            firstRecord + "\n" + CodexMessageRecord("same-history") + "\n";
        await File.WriteAllTextAsync(
            transcript, firstRecord + "\n", new UTF8Encoding(false));

        try
        {
            var inner = new FileCaptureRuntimeState(stateDirectory);
            var gate = new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"));
            Assert.Single(await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                inner,
                gate,
                transcriptIdentity: "transcript"));
            await File.WriteAllTextAsync(
                transcript, completeHistory, new UTF8Encoding(false));
            var claimEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new ClaimDelayingRuntimeState(
                inner, claimEntered, releaseClaim);

            Task<IReadOnlyList<CaptureRuntimeQueueItem>> first =
                CodexCaptureClaimer.ClaimCompletedAsync(
                    new CodexJsonlAdapter(),
                    transcript,
                    "stream",
                    delayed,
                    gate,
                    transcriptIdentity: "transcript");
            await claimEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                inner,
                gate,
                transcriptIdentity: "transcript"));

            releaseClaim.SetResult();
            Assert.Empty(await first);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await inner.ReadAsync()).Streams);
            Assert.Null(stream.Stop);
            Assert.Equal(1, stream.EnqueuedThrough);
            Assert.Equal([0L, 1L], stream.Queue.Select(item => item.SourcePosition));
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
                    new CapturePrefixEvidence(1, new string('e', 64)),
                    _ => false));

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

            Assert.True(await state.ClaimAsync(
                claim, expectedPrefix: null, verifiedPrefixMatchesSnapshot: _ => false));
            CaptureRuntimeSnapshot once = await state.ReadAsync();
            Assert.False(await state.ClaimAsync(
                claim, prefix, verifiedPrefixMatchesSnapshot: _ => false));
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
    public async Task StoppedStreamRetainsResponsibilityAndCannotAdvanceEitherProgressBoundary()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-stopped-{Guid.NewGuid():N}");
        try
        {
            var state = new FileCaptureRuntimeState(directory);
            var first = QueueItem("stream", 0, 10);
            var second = QueueItem("stream", 1, 20);
            Assert.True(await state.ClaimAsync(
                first, expectedPrefix: null, verifiedPrefixMatchesSnapshot: _ => false));
            Assert.True(await state.ClaimAsync(
                second,
                first.DeterministicLocatorEvidence.PrefixEvidence,
                _ => false));
            CaptureRuntimeSnapshot beforeStop = await state.ReadAsync();

            CaptureStreamStoppedException detected =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                    state.InspectSourceAsync(
                        "stream",
                        _ => new CaptureRuntimeStopState(
                            CaptureRuntimeStopCode.VerifiedPrefixChanged,
                            null)));
            CaptureRuntimeSnapshot stopped = await state.ReadAsync();

            var stoppedStream = Assert.Single(stopped.Streams);
            Assert.Equal(
                new CaptureRuntimeStopState("verified_prefix_changed", null),
                detected.Stop);
            Assert.Equal(
                detected.Stop,
                stoppedStream.Stop);
            Assert.Equal(
                Assert.Single(beforeStop.Streams).VerifiedPrefix,
                stoppedStream.VerifiedPrefix);
            Assert.Equal(1, stoppedStream.EnqueuedThrough);
            Assert.Equal([0L, 1L], stoppedStream.Queue.Select(item => item.SourcePosition));
            Assert.Null(stoppedStream.LastServerReceipt);
            using (JsonDocument stateDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "capture-state.json"))))
            {
                JsonElement persistedStop = stateDocument.RootElement
                    .GetProperty("streams")[0]
                    .GetProperty("stop");
                Assert.Equal(
                    ["code"],
                    persistedStop.EnumerateObject().Select(property => property.Name));
            }

            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                state.ClaimAsync(
                    QueueItem("stream", 2, 30),
                    second.DeterministicLocatorEvidence.PrefixEvidence,
                    _ => false));
            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                state.RecordServerReceiptAsync(
                    "stream",
                    new CaptureServerReceiptState(
                        first.SourcePosition,
                        first.DeterministicLocatorEvidence.Identity,
                        "new",
                        Guid.NewGuid(),
                        Guid.NewGuid())));

            bool competingDetectorEntered = false;
            CaptureStreamStoppedException sticky =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                    state.InspectSourceAsync(
                        "stream",
                        _ =>
                        {
                            competingDetectorEntered = true;
                            return new CaptureRuntimeStopState(
                                CaptureRuntimeStopCode.BlockedByEarlierGap,
                                1);
                        }));
            Assert.Equal(detected.Stop, sticky.Stop);
            Assert.False(competingDetectorEntered);
            Assert.Equal(
                JsonSerializer.Serialize(stopped),
                JsonSerializer.Serialize(await state.ReadAsync()));
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
    public async Task DurableStopRevokesStaleDeliverySnapshotAcrossRuntimeStateInstances()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-stop-race-{Guid.NewGuid():N}");
        try
        {
            var staleDeliveryState = new FileCaptureRuntimeState(directory);
            var stoppingState = new FileCaptureRuntimeState(directory);
            CaptureRuntimeQueueItem queued = QueueItem("stream", 0, 10);
            Assert.True(await staleDeliveryState.ClaimAsync(
                queued, expectedPrefix: null, verifiedPrefixMatchesSnapshot: _ => false));
            CaptureRuntimeQueueItem staleQueued = Assert.Single(
                Assert.Single((await staleDeliveryState.ReadAsync()).Streams).Queue);

            CaptureStreamStoppedException detected =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                    stoppingState.InspectSourceAsync(
                        "stream",
                        _ => new CaptureRuntimeStopState(
                            CaptureRuntimeStopCode.VerifiedPrefixChanged,
                            null)));
            CaptureRuntimeStopState durableStop = detected.Stop;
            CaptureRuntimeSnapshot stopped = await stoppingState.ReadAsync();
            bool deliveryAttempted = false;

            CaptureStreamStoppedException rejected =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                    staleDeliveryState.DeliverAuthorizedAsync(
                        staleQueued.SourceStream,
                        staleQueued,
                        _ =>
                        {
                            deliveryAttempted = true;
                            return Task.FromResult(
                                new CaptureRuntimeDeliveryResult<string>(
                                    new CaptureServerReceiptState(
                                        staleQueued.SourcePosition,
                                        staleQueued.DeterministicLocatorEvidence.Identity,
                                        "new",
                                        Guid.NewGuid(),
                                        Guid.NewGuid()),
                                    "should-not-deliver"));
                        }));

            Assert.Equal(durableStop, rejected.Stop);
            Assert.False(deliveryAttempted);
            Assert.Equal(
                JsonSerializer.Serialize(stopped),
                JsonSerializer.Serialize(await staleDeliveryState.ReadAsync()));
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
    public async Task DetectedSourceConflictStopsBeforeACompetingClaimCanEnter()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-inspection-race-{Guid.NewGuid():N}");
        try
        {
            var detectingState = new FileCaptureRuntimeState(directory);
            var competingState = new FileCaptureRuntimeState(directory);
            CaptureRuntimeQueueItem first = QueueItem("stream", 0, 10);
            Assert.True(await detectingState.ClaimAsync(
                first, expectedPrefix: null, verifiedPrefixMatchesSnapshot: _ => false));
            var conflictDetected = new ManualResetEventSlim();
            var allowStop = new ManualResetEventSlim();

            Task detection = Task.Run(async () =>
                await detectingState.InspectSourceAsync(
                    "stream",
                    _ =>
                    {
                        conflictDetected.Set();
                        Assert.True(allowStop.Wait(TimeSpan.FromSeconds(5)));
                        return new CaptureRuntimeStopState(
                            CaptureRuntimeStopCode.VerifiedPrefixChanged,
                            null);
                    }));
            Assert.True(conflictDetected.Wait(TimeSpan.FromSeconds(5)));

            bool competingClaimEntered = false;
            Task competingClaim = Task.Run(async () =>
            {
                competingClaimEntered = true;
                return await competingState.ClaimAsync(
                    QueueItem("stream", 1, 20),
                    first.DeterministicLocatorEvidence.PrefixEvidence,
                    _ => false);
            });
            await Task.Delay(100);
            Assert.True(competingClaimEntered);
            Assert.False(competingClaim.IsCompleted);

            allowStop.Set();
            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => detection);
            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => competingClaim);
            CaptureRuntimeStreamState stopped = Assert.Single(
                (await detectingState.ReadAsync()).Streams);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.VerifiedPrefixChanged,
                    null),
                stopped.Stop);
            Assert.Equal(0, stopped.EnqueuedThrough);
            Assert.Single(stopped.Queue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DetectedDeliveryConflictStopsBeforeACompetingCallbackCanEnter()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-delivery-conflict-race-{Guid.NewGuid():N}");
        try
        {
            var detectingState = new FileCaptureRuntimeState(directory);
            var competingState = new FileCaptureRuntimeState(directory);
            CaptureRuntimeQueueItem queued = QueueItem("stream", 0, 10);
            Assert.True(await detectingState.ClaimAsync(
                queued, expectedPrefix: null, verifiedPrefixMatchesSnapshot: _ => false));
            var conflictDetected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowConflict = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task detection = detectingState.DeliverAuthorizedAsync<string>(
                "stream",
                queued,
                async _ =>
                {
                    conflictDetected.SetResult();
                    await allowConflict.Task;
                    throw new CaptureRuntimeConflictException(
                        new CaptureRuntimeStopState(
                            CaptureRuntimeStopCode.AcceptedSourceConflict,
                            queued.SourcePosition));
                });
            await conflictDetected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            bool competingCallbackEntered = false;
            Task competingDelivery = competingState.DeliverAuthorizedAsync<string>(
                "stream",
                queued,
                _ =>
                {
                    competingCallbackEntered = true;
                    throw new InvalidOperationException("must remain unauthorized");
                });
            await Task.Delay(100);
            Assert.False(competingDelivery.IsCompleted);
            Assert.False(competingCallbackEntered);

            allowConflict.SetResult();
            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => detection);
            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => competingDelivery);
            Assert.False(competingCallbackEntered);
            CaptureRuntimeStreamState stopped = Assert.Single(
                (await detectingState.ReadAsync()).Streams);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.AcceptedSourceConflict,
                    queued.SourcePosition),
                stopped.Stop);
            Assert.Single(stopped.Queue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DetectedPrefixConflictPersistsItsStopAfterCallerCancellation()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-stop-cancellation-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        string original = await File.ReadAllTextAsync(
            Path.Combine(root, "fixtures/codex-synthetic.jsonl"));
        string firstBytes = original.Replace(
            "\"type\":\"response_item\"",
            "\"type\": \"response_item\"",
            StringComparison.Ordinal);
        string changed = original.Replace(
            "\"type\":\"response_item\"",
            "\"type\" :\"response_item\"",
            StringComparison.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(firstBytes),
            Encoding.UTF8.GetByteCount(changed));
        await File.WriteAllTextAsync(transcript, firstBytes, new UTF8Encoding(false));

        try
        {
            var fileState = new FileCaptureRuntimeState(stateDirectory);
            await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                fileState,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")));
            CaptureRuntimeQueueItem staleQueued = Assert.Single(
                (await fileState.ReadAsync()).Streams,
                stream => stream.SourceStream == "stream").Queue[0];

            await File.WriteAllTextAsync(transcript, changed, new UTF8Encoding(false));
            using var callerCancellation = new CancellationTokenSource();
            var observingState = new ConflictObservingRuntimeState(
                fileState,
                callerCancellation.Cancel);
            Task conflict = CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                observingState,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                callerCancellation.Token);

            CaptureStreamStoppedException stopped =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() => conflict);

            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.VerifiedPrefixChanged,
                    null),
                stopped.Stop);
            CaptureRuntimeSnapshot durable = await fileState.ReadAsync();
            CaptureRuntimeStreamState durableStream = Assert.Single(durable.Streams);
            Assert.Equal(stopped.Stop, durableStream.Stop);
            Assert.Equal(2, durableStream.EnqueuedThrough);
            Assert.Equal([0L, 1L, 2L], durableStream.Queue.Select(item => item.SourcePosition));

            bool staleDeliveryAttempted = false;
            await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                fileState.DeliverAuthorizedAsync<string>(
                    "stream",
                    staleQueued,
                    _ =>
                    {
                        staleDeliveryAttempted = true;
                        throw new InvalidOperationException("must remain unauthorized");
                    }));
            Assert.False(staleDeliveryAttempted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TranscriptIdentityConflictStopsWithoutInventingARecordLocation()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-transcript-identity-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);

        try
        {
            var state = new FileCaptureRuntimeState(stateDirectory);
            var gate = new NeverStoreGate(Path.Combine(root, "config/never_store.yaml"));
            await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                state,
                gate,
                transcriptIdentity: "first-transcript");

            CaptureStreamStoppedException conflict =
                await Assert.ThrowsAsync<CaptureStreamStoppedException>(() =>
                    CodexCaptureClaimer.ClaimCompletedAsync(
                        new CodexJsonlAdapter(),
                        transcript,
                        "stream",
                        state,
                        gate,
                        transcriptIdentity: "replacement-transcript"));

            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.TranscriptIdentityChanged,
                    null),
                conflict.Stop);
            using JsonDocument persisted = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(stateDirectory, "capture-state.json")));
            Assert.Equal(
                ["code"],
                persisted.RootElement.GetProperty("streams")[0].GetProperty("stop")
                    .EnumerateObject().Select(property => property.Name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationBeforeConflictDetectionLeavesTheStreamActive()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-pre-detection-cancel-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);

        try
        {
            var state = new FileCaptureRuntimeState(stateDirectory);
            await CodexCaptureClaimer.ClaimCompletedAsync(
                new CodexJsonlAdapter(),
                transcript,
                "stream",
                state,
                new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")));
            CaptureRuntimeSnapshot beforeCancellation = await state.ReadAsync();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CodexCaptureClaimer.ClaimCompletedAsync(
                    new CodexJsonlAdapter(),
                    transcript,
                    "stream",
                    state,
                    new NeverStoreGate(Path.Combine(root, "config/never_store.yaml")),
                    cancellation.Token));

            Assert.Equal(
                JsonSerializer.Serialize(beforeCancellation),
                JsonSerializer.Serialize(await state.ReadAsync()));
            Assert.Null(Assert.Single((await state.ReadAsync()).Streams).Stop);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("new")]
    [InlineData("already_accepted")]
    public async Task ConclusiveReceiptRetiresOnlyTheEarliestQueuedResponsibility(string status)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-retire-{Guid.NewGuid():N}");
        try
        {
            var state = new FileCaptureRuntimeState(directory);
            var first = QueueItem("stream", 0, 10);
            var second = QueueItem("stream", 1, 20);
            Assert.True(await state.ClaimAsync(
                first, expectedPrefix: null, verifiedPrefixMatchesSnapshot: _ => false));
            Assert.True(await state.ClaimAsync(
                second,
                first.DeterministicLocatorEvidence.PrefixEvidence,
                _ => false));

            var receipt = new CaptureServerReceiptState(
                first.SourcePosition,
                first.DeterministicLocatorEvidence.Identity,
                status,
                Guid.NewGuid(),
                Guid.NewGuid());
            await state.RecordServerReceiptAsync("stream", receipt);

            CaptureRuntimeStreamState stream = Assert.Single((await state.ReadAsync()).Streams);
            Assert.Equal([1L], stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(receipt, stream.LastServerReceipt);
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
            Assert.Empty(stream.Queue);
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
            Assert.Equal([1L, 2L], stream.Queue.Select(item => item.SourcePosition));
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
    public async Task PackagedTracerKeepsAllResponsibilityRetryableForNonConflictReasonBody()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-retryable-reason-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(root, "fixtures/codex-synthetic.jsonl"), transcript);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> server = ServeResponsesAsync(
            listener,
            [
                (
                    HttpStatusCode.ServiceUnavailable,
                    """{"reason":"blocked_by_earlier_gap"}""")
            ],
            serverCancellation.Token);

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(1, await server);
            Assert.Empty(result.Stdout);
            CaptureRuntimeStreamState stream = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Null(stream.Stop);
            Assert.Equal([0L, 1L, 2L], stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(2, stream.EnqueuedThrough);
            Assert.Null(stream.LastServerReceipt);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            listener.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerStopsAtTheQueuedPositionWhoseSourceEvidenceChanged()
    {
        string root = TestProcessRunner.RepoRoot;
        string directory = Path.Combine(
            Path.GetTempPath(), $"capture-runtime-evidence-position-{Guid.NewGuid():N}");
        string transcript = Path.Combine(directory, "rollout.jsonl");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(directory);
        string original = await File.ReadAllTextAsync(
            Path.Combine(root, "fixtures/codex-synthetic.jsonl"));
        string changed = original.Replace(
            "\"output\":\"/workspace\"",
            "\"output\":\"/workspacf\"",
            StringComparison.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(original),
            Encoding.UTF8.GetByteCount(changed));
        await File.WriteAllTextAsync(transcript, original, new UTF8Encoding(false));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<int> server = ServeResponsesAsync(
            listener,
            [
                (HttpStatusCode.OK, Receipt(0)),
                (HttpStatusCode.OK, Receipt(1))
            ],
            serverCancellation.Token,
            requestCount =>
            {
                if (requestCount == 2)
                {
                    File.WriteAllText(transcript, changed, new UTF8Encoding(false));
                }
            });

        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));

            Assert.Equal(4, result.ExitCode);
            Assert.Equal(2, await server);
            Assert.Equal(
                [0L, 1L],
                result.Stdout.Split(
                        Environment.NewLine,
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonDocument.Parse(line).RootElement
                        .GetProperty("sourcePosition").GetInt64()));
            Assert.Contains("queued_source_evidence_changed", result.Stderr);
            Assert.Contains("source position 2", result.Stderr);

            var state = new FileCaptureRuntimeState(stateDirectory);
            CaptureRuntimeSnapshot stopped = await state.ReadAsync();
            CaptureRuntimeStreamState stream = Assert.Single(stopped.Streams);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.QueuedSourceEvidenceChanged,
                    2),
                stream.Stop);
            Assert.Equal([2L], stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(1, stream.LastServerReceipt?.SourcePosition);
            Assert.Equal(2, stream.EnqueuedThrough);

            var repeated = await TestProcessRunner.RunCaptureTracerToExitAsync(
                TracerEnvironment(transcript, stateDirectory, port));
            Assert.Equal(4, repeated.ExitCode);
            Assert.Empty(repeated.Stdout);
            Assert.Equal(
                JsonSerializer.Serialize(stopped),
                JsonSerializer.Serialize(await state.ReadAsync()));
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
    [InlineData("""{"sourcePosition":1,"status":"failed","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5"}""")]
    [InlineData("""{"sourcePosition":1,"status":"new"}""")]
    [InlineData("""{"sourcePosition":1,"status":"new","observationUuid":"not-a-uuid"}""")]
    [InlineData("""{"sourcePosition":1,"status":"new","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5","observation":{"locator":{"kind":"byte_range","byteOffset":999999,"byteLength":1}}}""")]
    [InlineData("""{"sourcePosition":1,"status":"new","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5","observation":{"observationUuid":"9da8ad61-92c5-40b5-8b71-0ef233648c56","sourceStreamUuid":"a4d86f4c-e045-4761-929b-eec9e5959f95"}}""")]
    [InlineData("""{"sourcePosition":1,"status":"new","observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5","observation":{"observationUuid":"b6cb766b-b9c0-4d93-a1bb-4ddd3c6db8f5","sourceStreamUuid":"646daf38-73d9-4c9e-8a84-13e1fc5667f2"}}""")]
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
            Assert.Equal([1L, 2L], stream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(
                stream.CanonicalSourceStreamUuid,
                stream.LastServerReceipt?.SourceStreamUuid);
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

    private static CaptureRuntimeQueueItem QueueItem(
        string stream, long sourcePosition, long prefixLength) =>
        new(
            stream,
            new CaptureRuntimeLocatorEvidence(
                "transcript",
                sourcePosition,
                prefixLength - 10,
                10,
                $"record-{sourcePosition}",
                new CapturePrefixEvidence(prefixLength, $"prefix-{sourcePosition}")),
            """{"safe":"candidate"}""");

    private static string CodexMessageRecord(string content) =>
        JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new
            {
                type = "message",
                role = "user",
                content
            }
        });

    private static string Receipt(long sourcePosition, Guid? observationUuid = null)
    {
        Guid uuid = observationUuid ?? Guid.NewGuid();
        return JsonSerializer.Serialize(new
        {
            sourcePosition,
            status = "new",
            observationUuid = uuid,
            observation = new
            {
                observationUuid = uuid
            }
        });
    }

    private static async Task<int> ServeResponsesAsync(
        TcpListener listener,
        IReadOnlyList<(HttpStatusCode Status, string Body)> responses,
        CancellationToken cancellationToken,
        Action<int>? beforeResponse = null)
    {
        int requestCount = 0;
        Guid sourceStreamUuid = Guid.NewGuid();
        try
        {
            foreach (var (status, body) in responses)
            {
                using TcpClient client =
                    await listener.AcceptTcpClientAsync(cancellationToken);
                await using NetworkStream stream = client.GetStream();
                string requestBody = await ReadRequestAsync(stream, cancellationToken);
                requestCount++;
                beforeResponse?.Invoke(requestCount);
                string responseBody = AddRequestObservation(
                    body, requestBody, sourceStreamUuid);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
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

    private static string AddRequestObservation(
        string responseBody,
        string requestBody,
        Guid sourceStreamUuid)
    {
        JsonObject? response = JsonNode.Parse(responseBody)?.AsObject();
        JsonObject? request = JsonNode.Parse(requestBody)?.AsObject();
        if (response is not null && request?["locator"] is JsonObject locator)
        {
            JsonObject observation = response["observation"] as JsonObject ?? new JsonObject();
            observation["locator"] ??= locator.DeepClone();
            observation["observationUuid"] ??= response["observationUuid"]?.DeepClone();
            observation["sourceStreamUuid"] ??= sourceStreamUuid;
            response["observation"] = observation;
        }
        return response?.ToJsonString() ?? responseBody;
    }

    private static async Task<string> ReadRequestAsync(
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
        return Encoding.UTF8.GetString(body);
    }

    private sealed class ConflictObservingRuntimeState(
        ICaptureRuntimeState inner,
        Action conflictDetected) : ICaptureRuntimeState
    {
        public Task<CaptureRuntimeSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task<bool> ClaimAsync(
            CaptureRuntimeQueueItem claim,
            CapturePrefixEvidence? expectedPrefix,
            Func<CapturePrefixEvidence?, bool> verifiedPrefixMatchesSnapshot,
            CancellationToken cancellationToken = default) =>
            inner.ClaimAsync(
                claim,
                expectedPrefix,
                verifiedPrefixMatchesSnapshot,
                cancellationToken);

        public Task<CaptureRuntimeStreamState?> InspectSourceAsync(
            string sourceStream,
            Func<CaptureRuntimeStreamState, CaptureRuntimeStopState?> detectConflict,
            CancellationToken cancellationToken = default) =>
            inner.InspectSourceAsync(
                sourceStream,
                stream =>
                {
                    CaptureRuntimeStopState? stop = detectConflict(stream);
                    if (stop is not null)
                    {
                        conflictDetected();
                    }
                    return stop;
                },
                cancellationToken);

        public Task RecordServerReceiptAsync(
            string sourceStream,
            CaptureServerReceiptState receipt,
            CancellationToken cancellationToken = default) =>
            inner.RecordServerReceiptAsync(sourceStream, receipt, cancellationToken);

        public Task<TResult> DeliverAuthorizedAsync<TResult>(
            string sourceStream,
            CaptureRuntimeQueueItem queued,
            Func<CancellationToken, Task<CaptureRuntimeDeliveryResult<TResult>>> deliverAsync,
            CancellationToken cancellationToken = default) =>
            inner.DeliverAuthorizedAsync(
                sourceStream, queued, deliverAsync, cancellationToken);

    }

    private sealed class SourceInspectionObservingRuntimeState(
        ICaptureRuntimeState inner,
        Action inspectionStarted) : ICaptureRuntimeState
    {
        public Task<CaptureRuntimeSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task<bool> ClaimAsync(
            CaptureRuntimeQueueItem claim,
            CapturePrefixEvidence? expectedPrefix,
            Func<CapturePrefixEvidence?, bool> verifiedPrefixMatchesSnapshot,
            CancellationToken cancellationToken = default) =>
            inner.ClaimAsync(
                claim,
                expectedPrefix,
                verifiedPrefixMatchesSnapshot,
                cancellationToken);

        public Task<CaptureRuntimeStreamState?> InspectSourceAsync(
            string sourceStream,
            Func<CaptureRuntimeStreamState, CaptureRuntimeStopState?> detectConflict,
            CancellationToken cancellationToken = default)
        {
            inspectionStarted();
            return inner.InspectSourceAsync(
                sourceStream, detectConflict, cancellationToken);
        }

        public Task RecordServerReceiptAsync(
            string sourceStream,
            CaptureServerReceiptState receipt,
            CancellationToken cancellationToken = default) =>
            inner.RecordServerReceiptAsync(sourceStream, receipt, cancellationToken);

        public Task<TResult> DeliverAuthorizedAsync<TResult>(
            string sourceStream,
            CaptureRuntimeQueueItem queued,
            Func<CancellationToken, Task<CaptureRuntimeDeliveryResult<TResult>>> deliverAsync,
            CancellationToken cancellationToken = default) =>
            inner.DeliverAuthorizedAsync(
                sourceStream, queued, deliverAsync, cancellationToken);

    }

    private sealed class ClaimDelayingRuntimeState(
        ICaptureRuntimeState inner,
        TaskCompletionSource claimEntered,
        TaskCompletionSource releaseClaim,
        Action? prefixVerificationStarted = null) : ICaptureRuntimeState
    {
        public Task<CaptureRuntimeSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public async Task<bool> ClaimAsync(
            CaptureRuntimeQueueItem claim,
            CapturePrefixEvidence? expectedPrefix,
            Func<CapturePrefixEvidence?, bool> verifiedPrefixMatchesSnapshot,
            CancellationToken cancellationToken = default)
        {
            claimEntered.TrySetResult();
            await releaseClaim.Task.WaitAsync(cancellationToken);
            return await inner.ClaimAsync(
                claim,
                expectedPrefix,
                evidence =>
                {
                    prefixVerificationStarted?.Invoke();
                    return verifiedPrefixMatchesSnapshot(evidence);
                },
                cancellationToken);
        }

        public Task<CaptureRuntimeStreamState?> InspectSourceAsync(
            string sourceStream,
            Func<CaptureRuntimeStreamState, CaptureRuntimeStopState?> detectConflict,
            CancellationToken cancellationToken = default) =>
            inner.InspectSourceAsync(
                sourceStream, detectConflict, cancellationToken);

        public Task RecordServerReceiptAsync(
            string sourceStream,
            CaptureServerReceiptState receipt,
            CancellationToken cancellationToken = default) =>
            inner.RecordServerReceiptAsync(sourceStream, receipt, cancellationToken);

        public Task<TResult> DeliverAuthorizedAsync<TResult>(
            string sourceStream,
            CaptureRuntimeQueueItem queued,
            Func<CancellationToken, Task<CaptureRuntimeDeliveryResult<TResult>>> deliverAsync,
            CancellationToken cancellationToken = default) =>
            inner.DeliverAuthorizedAsync(
                sourceStream, queued, deliverAsync, cancellationToken);
    }

    private sealed class RetainedMetadataAdapter(string rawSentinel) : ICaptureSourceAdapter
    {
        private readonly string _oversized = string.Concat(
            Enumerable.Repeat(rawSentinel, 100));

        public string Harness => "codex";
        public CaptureAdapter Identity => new(_oversized, _oversized);

        public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
        {
            var locator = Assert.IsType<CaptureSourceLocator.ByteRange>(source.Locator);
            JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
            return new CaptureSourcePositionOutcome.Terminal(
                source.SourcePosition,
                new CaptureObservationRequest(
                    1,
                    source.SourceIdentity.ExternalSessionId,
                    source.SourcePosition,
                    new CaptureLocator(
                        locator.Kind,
                        null,
                        locator.Offset,
                        locator.Length,
                        locator.SourceContentSha256),
                    new CaptureSourceTimestamp(_oversized, null),
                    new CaptureSource(
                        Harness,
                        _oversized,
                        _oversized,
                        _oversized,
                        _oversized,
                        _oversized),
                    Identity,
                    payload,
                    [
                        new CaptureEvent(
                            "synthetic/0",
                            0,
                            "opaque",
                            "harness",
                            payload,
                            null,
                            [])
                    ],
                    SourceIdentity: null));
        }
    }

    private sealed class TransportPaddingAdapter(int paddingLength) : ICaptureSourceAdapter
    {
        private readonly string _padding = new('p', paddingLength);

        public string Harness => "codex";
        public CaptureAdapter Identity => new("test", "1");

        public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
        {
            var locator = Assert.IsType<CaptureSourceLocator.ByteRange>(source.Locator);
            JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
            return new CaptureSourcePositionOutcome.Terminal(
                source.SourcePosition,
                new CaptureObservationRequest(
                    1,
                    source.SourceIdentity.ExternalSessionId,
                    source.SourcePosition,
                    new CaptureLocator(
                        locator.Kind,
                        null,
                        locator.Offset,
                        locator.Length,
                        locator.SourceContentSha256),
                    null,
                    new CaptureSource(Harness, null, _padding, null, null, null),
                    Identity,
                    payload,
                    [
                        new CaptureEvent(
                            "synthetic/0",
                            0,
                            "opaque",
                            "harness",
                            payload,
                            null,
                            [])
                    ],
                    SourceIdentity: source.SourceIdentity));
        }
    }

    private sealed class NativeTransportPaddingAdapter(string rawSentinel)
        : ICaptureSourceAdapter
    {
        private readonly string _padding = string.Concat(
            Enumerable.Repeat(rawSentinel, 200));

        public string Harness => "codex";
        public CaptureAdapter Identity => new("test", "1");

        public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
        {
            JsonElement payload = JsonSerializer.SerializeToElement(new
            {
                padding = _padding
            });
            return new CaptureSourcePositionOutcome.Terminal(
                source.SourcePosition,
                new CaptureObservationRequest(
                    1,
                    source.SourceIdentity.ExternalSessionId,
                    source.SourcePosition,
                    new CaptureLocator(
                        "native_id",
                        "stable-native-locator",
                        null,
                        null,
                        null),
                    null,
                    new CaptureSource(Harness, null, null, null, null, null),
                    Identity,
                    payload,
                    [
                        new CaptureEvent(
                            "synthetic/0",
                            0,
                            "opaque",
                            "harness",
                            payload,
                            null,
                            [])
                    ],
                    SourceIdentity: source.SourceIdentity));
        }
    }

    private sealed class OversizedMandatoryFieldAdapter(
        string sentinel,
        bool oversizedIdentity) : ICaptureSourceAdapter
    {
        private readonly string _oversized = string.Concat(
            Enumerable.Repeat(sentinel, 100));

        public string Harness => "codex";
        public CaptureAdapter Identity => new("test", "1");

        public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
        {
            JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
            string externalSessionId = oversizedIdentity
                ? _oversized
                : source.SourceIdentity.ExternalSessionId;
            return new CaptureSourcePositionOutcome.Terminal(
                source.SourcePosition,
                new CaptureObservationRequest(
                    1,
                    externalSessionId,
                    source.SourcePosition,
                    oversizedIdentity
                        ? new CaptureLocator(
                            "byte_range",
                            null,
                            0,
                            21,
                            new string('a', 64))
                        : new CaptureLocator("native_id", _oversized, null, null, null),
                    null,
                    new CaptureSource(Harness, null, null, null, null, null),
                    Identity,
                    payload,
                    [
                        new CaptureEvent(
                            "synthetic/0",
                            0,
                            "opaque",
                            "harness",
                            payload,
                            null,
                            [])
                    ],
                    SourceIdentity: new CaptureSourceIdentity(externalSessionId)));
        }
    }

    private sealed class ConflictingDualIdentityAdapter : ICaptureSourceAdapter
    {
        public string Harness => "codex";
        public CaptureAdapter Identity => new("test", "1");

        public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
        {
            JsonElement payload = JsonSerializer.SerializeToElement(new
            {
                padding = new string('p', 4_096)
            });
            var locator = Assert.IsType<CaptureSourceLocator.ByteRange>(source.Locator);
            return new CaptureSourcePositionOutcome.Terminal(
                source.SourcePosition,
                new CaptureObservationRequest(
                    1,
                    "legacy-identity",
                    source.SourcePosition,
                    new CaptureLocator(
                        locator.Kind,
                        null,
                        locator.Offset,
                        locator.Length,
                        locator.SourceContentSha256),
                    null,
                    new CaptureSource(Harness, null, null, null, null, null),
                    Identity,
                    payload,
                    [
                        new CaptureEvent(
                            "synthetic/0",
                            0,
                            "opaque",
                            "harness",
                            payload,
                            null,
                            [])
                    ],
                    SourceIdentity: new CaptureSourceIdentity("current-identity")));
        }
    }

    private static CaptureObservationRequest ResourceBoundObservation(
        JsonElement payload,
        string identity,
        CaptureLocator? locator = null) =>
        new(
            1,
            identity,
            0,
            locator ?? new CaptureLocator(
                "byte_range",
                null,
                0,
                1,
                new string('a', 64)),
            null,
            new CaptureSource("codex", null, null, null, null, null),
            new CaptureAdapter("test", "1"),
            payload,
            [
                new CaptureEvent(
                    "synthetic/0",
                    0,
                    "opaque",
                    "harness",
                    payload,
                    null,
                    [])
            ],
            SourceIdentity: new CaptureSourceIdentity(identity));

    private sealed class StatefulEventList(
        params CaptureEvent[] enumerations) : IReadOnlyList<CaptureEvent>
    {
        private int _enumeration;

        public int Count => 1;
        public CaptureEvent this[int index] => index == 0
            ? enumerations[Math.Min(_enumeration, enumerations.Length - 1)]
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<CaptureEvent> GetEnumerator()
        {
            int enumeration = Interlocked.Increment(ref _enumeration) - 1;
            CaptureEvent item =
                enumerations[Math.Min(enumeration, enumerations.Length - 1)];
            yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
