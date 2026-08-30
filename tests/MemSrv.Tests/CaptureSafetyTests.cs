using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using MemSrv.Core;
using Npgsql;

namespace MemSrv.Tests;

// Inaccessible legacy capture-core safety behavior, exercised only through the
// authorized CaptureIngestion module seam and narrow ledger mechanics.
// Every credential in this file is synthetic.
[Collection("database")]
public sealed class CaptureSafetyTests : HttpSeamTestBase
{
    [Fact]
    public async Task ObservationBeyondInjectedContentLimitAdvancesAsAWholePayloadOmission()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-content-limit-{Guid.NewGuid():N}", captureKey);
        var binding = await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey);
        Assert.NotNull(binding);
        const string rawPayload = "RAW-PAYLOAD-MUST-BE-WHOLLY-OMITTED";
        object request = Observation(
            sourceSessionId,
            0,
            $"content-limit-{Guid.NewGuid():N}",
            string.Concat(Enumerable.Repeat(rawPayload, 30)));
        var command = CaptureObservationCommand.FromRequest(
            JsonSerializer.Deserialize<CaptureObservationRequest>(
                JsonSerializer.Serialize(
                    request,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            2_048);

        CaptureImportReceipt receipt = await ingestion.ImportAsync(binding!, command);

        Assert.Equal("new", receipt.Status);
        Assert.Equal(0, receipt.SourcePosition);
        Assert.Equal("healthy", receipt.Outcome.CaptureHealth);
        Assert.Equal("degraded", receipt.Outcome.CaptureFidelity);
        Assert.Equal(
            new CaptureOutcomeCounter(
                "codex",
                CaptureOutcomeAggregation.FidelityOmissionClass,
                CaptureFidelityPolicy.ContentLimitReason,
                CaptureSizeBand.UpTo1MiB,
                1),
            Assert.Single(receipt.Outcome.Counters));
        Assert.DoesNotContain(rawPayload, receipt.Observation.SafeSourcePayload.GetRawText());
        JsonElement omission = receipt.Observation.SafeSourcePayload.GetProperty("omission");
        Assert.Equal(
            "observation_exceeds_content_limit",
            omission.GetProperty("reason").GetString());
        Assert.Equal(
            CaptureFidelityPolicy.CurrentVersion,
            omission.GetProperty("policyVersion").GetString());
        Assert.True(omission.GetProperty("originalByteCount").GetInt64() > 2_048);
        var capturedEvent = Assert.Single(receipt.Events).Event;
        Assert.Equal("observation/omitted", capturedEvent.PartKey);
        Assert.DoesNotContain(rawPayload, capturedEvent.Payload.GetRawText());

        CaptureImportReceipt retry = await ingestion.ImportAsync(binding!, command);
        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(receipt.ObservationUuid, retry.ObservationUuid);
    }

    [Fact]
    public async Task UnexpectedScannerFailureIsContentFreeAndAppendsNothing()
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"scanner-internal-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        var command = CaptureObservationCommand.FromRequest(
            JsonSerializer.Deserialize<CaptureObservationRequest>(
                JsonSerializer.Serialize(
                    Observation(
                        UniqueSession(),
                        0,
                        $"scanner-internal-{Guid.NewGuid():N}",
                        "safe candidate"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        var failingIngestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(new ThrowingSafetyScanner()));

        WriteSafetyScannerInternalException failure =
            await Assert.ThrowsAsync<WriteSafetyScannerInternalException>(
                () => failingIngestion.ImportAsync(binding, command));
        CaptureOutcomeSummary outcome = CaptureOutcomeAggregation.FromWriteSafetyFailure(
            command.Source.Harness, failure);

        Assert.DoesNotContain("scanner implementation detail", failure.Message);
        Assert.Equal(WriteSafetyFailureCode.ScannerInternalFailure, failure.FailureCode);
        Assert.Equal("blocked", outcome.CaptureHealth);
        Assert.Equal("complete", outcome.CaptureFidelity);
        Assert.Equal(
            CaptureOutcomeReason.ScannerInternalFailure,
            Assert.Single(outcome.Counters).Reason);

        CaptureImportReceipt accepted = await new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(Path.Combine(_root, "config/never_store.yaml")))
            .ImportAsync(binding, command);
        Assert.Equal("new", accepted.Status);
        Assert.Equal(0, accepted.SourcePosition);
    }

    [Fact]
    public async Task IncompleteRequiredInspectionIsBlockedAndAppendsNothing()
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"inspection-incomplete-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        var command = CaptureObservationCommand.FromRequest(
            JsonSerializer.Deserialize<CaptureObservationRequest>(
                JsonSerializer.Serialize(
                    Observation(
                        UniqueSession(),
                        0,
                        $"inspection-incomplete-{Guid.NewGuid():N}",
                        "safe"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        var constrained = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml"),
                null,
                WriteSafetyBudgets.Default with { MaxLeafBytes = 8 }));

        WriteSafetyScanException failure = await Assert.ThrowsAsync<WriteSafetyScanException>(
            () => constrained.ImportAsync(binding, command));
        CaptureOutcomeSummary outcome = CaptureOutcomeAggregation.FromWriteSafetyFailure(
            command.Source.Harness, failure);

        Assert.Equal("blocked", outcome.CaptureHealth);
        Assert.Equal(
            CaptureOutcomeReason.RequiredInspectionIncomplete,
            Assert.Single(outcome.Counters).Reason);

        CaptureImportReceipt accepted = await new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(Path.Combine(_root, "config/never_store.yaml")))
            .ImportAsync(binding, command);
        Assert.Equal("new", accepted.Status);
        Assert.Equal(0, accepted.SourcePosition);
    }

    [Fact]
    public async Task BinaryRewriteBeyondContentBoundAppendsOnlyAWholeObservationOmission()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"binary-content-overflow-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        JsonElement sourcePayload = JsonSerializer.SerializeToElement(new
        {
            type = "response_item",
            payload = new
            {
                type = "message",
                content = Enumerable.Range(0, 300).Select(_ => new
                {
                    type = "binary_content",
                    category = "attachment",
                    byte_payload = new[] { 91, 92, 93 }
                })
            }
        });
        JsonElement safeEventPayload =
            JsonSerializer.SerializeToElement(new { text = "safe event" });
        var command = new CaptureObservationCommand(
            1,
            new CaptureSourceIdentity(sourceSessionId),
            0,
            new CaptureSourceLocator.NativeId($"binary-overflow-{Guid.NewGuid():N}"),
            null,
            new CaptureSource("codex", null, "response_item", null, null, null),
            new CaptureAdapter("test", "1"),
            sourcePayload,
            [
                new CaptureEvent(
                    "message/0",
                    0,
                    "message",
                    "user",
                    safeEventPayload,
                    null,
                    [])
            ],
            null);
        const int contentBound = 64 * 1_024;
        Assert.True(
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                command,
                CaptureLedger.JsonOptions)) < contentBound);
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            contentBound);

        CaptureImportReceipt receipt = await ingestion.ImportAsync(binding, command);

        Assert.Equal("new", receipt.Status);
        Assert.Equal(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            receipt.Observation.SafeSourcePayload.GetProperty("omission")
                .GetProperty("reason").GetString());
        Assert.Equal(
            "observation/omitted",
            Assert.Single(receipt.Events).Event.PartKey);
        string canonical = JsonSerializer.Serialize(receipt, CaptureLedger.JsonOptions);
        Assert.DoesNotContain("\"byte_payload\"", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("[91,92,93]", canonical, StringComparison.Ordinal);

        CaptureImportReceipt retry = await ingestion.ImportAsync(binding, command);
        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(receipt.ObservationUuid, retry.ObservationUuid);
    }

    [Fact]
    public async Task OversizedContentIsCompactedAndSignedWithoutWholeOriginalAllocation()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"content-resource-bound-{Guid.NewGuid():N}", captureKey);
        var binding = await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey);
        Assert.NotNull(binding);
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            padding = new string('p', 4 * 1024 * 1024)
        });
        var command = CaptureObservationCommand.FromRequest(
            new CaptureObservationRequest(
                1,
                sourceSessionId,
                0,
                new CaptureLocator(
                    "native_id",
                    $"content-resource-bound-{Guid.NewGuid():N}",
                    null,
                    null,
                    null),
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
                SourceIdentity: new CaptureSourceIdentity(sourceSessionId)));
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            2_048);
        JsonElement warmPayload =
            JsonSerializer.SerializeToElement(new { padding = "warm" });
        string warmIdentity = UniqueSession();
        await ingestion.ImportAsync(
            binding!,
            command with
            {
                SourceIdentity = new CaptureSourceIdentity(warmIdentity),
                Locator = new CaptureSourceLocator.NativeId(
                    $"content-resource-warm-{Guid.NewGuid():N}"),
                SourcePayload = warmPayload,
                Events =
                [
                    new CaptureEvent(
                        "synthetic/0",
                        0,
                        "opaque",
                        "harness",
                        warmPayload,
                        null,
                        [])
                ]
            });
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();

        CaptureImportReceipt receipt = await ingestion.ImportAsync(binding!, command);

        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal("new", receipt.Status);
        Assert.Equal(
            CaptureFidelityPolicy.ContentLimitReason,
            receipt.Observation.SafeSourcePayload
                .GetProperty("omission")
                .GetProperty("reason")
                .GetString());
        Assert.True(
            allocated < 12L * 1024 * 1024,
            $"Bounded content ingestion allocated {allocated:N0} bytes; it " +
            "should not materialize the roughly 8 MiB original JSON for signing.");
        Assert.True(
            clock.Elapsed < WriteSafetyBudgets.Default.MaxScanTime,
            $"Bounded content ingestion took {clock.Elapsed}; the published deadline is " +
            $"{WriteSafetyBudgets.Default.MaxScanTime}.");
    }

    [Fact]
    public async Task RetainedMetadataBeyondInjectedContentLimitAdvancesAsABoundedOmission()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-content-metadata-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        const string rawSentinel = "RAW-RETAINED-METADATA-MUST-NOT-BE-CANONICAL";
        string oversized = string.Concat(Enumerable.Repeat(rawSentinel, 100));
        JsonElement safePayload = JsonSerializer.SerializeToElement(new { safe = true });
        var request = new CaptureObservationRequest(
            1,
            sourceSessionId,
            0,
            new CaptureLocator(
                "native_id",
                $"content-metadata-{Guid.NewGuid():N}",
                null,
                null,
                null),
            new CaptureSourceTimestamp(oversized, null),
            new CaptureSource(
                "codex",
                oversized,
                oversized,
                oversized,
                oversized,
                oversized),
            new CaptureAdapter(oversized, oversized),
            safePayload,
            [
                new CaptureEvent(
                    "metadata/0",
                    0,
                    "opaque",
                    "harness",
                    safePayload,
                    null,
                    [])
            ],
            SourceIdentity: new CaptureSourceIdentity(sourceSessionId));
        CaptureObservationCommand command = CaptureObservationCommand.FromRequest(request);
        const int contentBound = 2_048;
        Assert.True(
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                command,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))) > contentBound);
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            contentBound);

        CaptureImportReceipt receipt = await ingestion.ImportAsync(binding, command);

        Assert.Equal("new", receipt.Status);
        string canonical = JsonSerializer.Serialize(
            receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(Encoding.UTF8.GetByteCount(canonical) <= contentBound);
        Assert.DoesNotContain(rawSentinel, canonical);
        Assert.Equal(
            "observation_exceeds_content_limit",
            receipt.Observation.SafeSourcePayload.GetProperty("omission")
                .GetProperty("reason").GetString());
        CaptureImportReceipt retry = await ingestion.ImportAsync(binding, command);
        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(receipt.ObservationUuid, retry.ObservationUuid);
        CaptureConflictException conflict = await Assert.ThrowsAsync<CaptureConflictException>(
            () => ingestion.ImportAsync(
                binding,
                command with
                {
                    Source = command.Source with { Model = oversized + "-changed" }
                }));
        Assert.Equal("accepted_source_conflict", conflict.Reason);
    }

    [Fact]
    public async Task OversizedMalformedOptionalContentIsOmittedButTheSameInLimitShapeIsRejected()
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"malformed-optional-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
        CaptureEvent malformed = new(
            "duplicate",
            0,
            "opaque",
            "harness",
            payload,
            null,
            [new CaptureRelationship("", null!)]);
        var oversized = new CaptureObservationCommand(
            1,
            new CaptureSourceIdentity(UniqueSession()),
            0,
            new CaptureSourceLocator.NativeId($"malformed-wide-{Guid.NewGuid():N}"),
            new CaptureSourceTimestamp("", null),
            new CaptureSource("codex", null, null, null, null, null),
            new CaptureAdapter("test", "1"),
            payload,
            Enumerable.Repeat(malformed, 500).ToArray(),
            null);
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            2_048);

        CaptureImportReceipt receipt = await ingestion.ImportAsync(binding, oversized);

        Assert.Equal("new", receipt.Status);
        Assert.Null(receipt.Observation.SourceTimestamp);
        Assert.Equal("observation/omitted", Assert.Single(receipt.Events).Event.PartKey);
        Assert.DoesNotContain("duplicate", JsonSerializer.Serialize(receipt));

        CaptureObservationCommand inLimit = oversized with
        {
            SourceIdentity = new CaptureSourceIdentity(UniqueSession()),
            Events = [malformed]
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => ingestion.ImportAsync(binding, inLimit));
    }

    [Fact]
    public async Task LaterCallerListMutationCannotChangeTheBoundedEventFanout()
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"mutable-snapshot-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        string sourceSessionId = UniqueSession();
        JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
        CaptureEvent retained = new(
            "retained/0", 0, "opaque", "harness", payload, null, []);
        CaptureEvent[] laterEvents = Enumerable.Range(0, 100)
            .Select(index => new CaptureEvent(
                $"later/{index}",
                index,
                "opaque",
                "harness",
                JsonSerializer.SerializeToElement(new { raw = new string('x', 256) }),
                null,
                []))
            .ToArray();
        var statefulEvents = new StatefulEventList(retained, laterEvents);
        var command = new CaptureObservationCommand(
            1,
            new CaptureSourceIdentity(sourceSessionId),
            0,
            new CaptureSourceLocator.NativeId($"mutable-{Guid.NewGuid():N}"),
            null,
            new CaptureSource("codex", null, null, null, null, null),
            new CaptureAdapter("test", "1"),
            payload,
            statefulEvents,
            null);
        const int contentBound = 4_096;
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            command with { Events = laterEvents },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))) > contentBound);
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            contentBound);

        CaptureImportReceipt receipt = await ingestion.ImportAsync(binding, command);

        Assert.Equal("new", receipt.Status);
        Assert.Equal("retained/0", Assert.Single(receipt.Events).Event.PartKey);
        Assert.False(receipt.Observation.SafeSourcePayload.TryGetProperty("omission", out _));
        Assert.Equal(2, statefulEvents.EnumerationCount);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    public async Task OversizedCodexAdapterUpgradeRetryUsesTheOriginalContentSignature(
        string acceptedAdapterVersion)
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"oversized-upgrade-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        string sourceSessionId = UniqueSession();
        string locator = $"oversized-upgrade-{Guid.NewGuid():N}";
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            message = "same source record",
            padding = new string('p', 8_192)
        });
        var accepted = new CaptureObservationCommand(
            1,
            new CaptureSourceIdentity(sourceSessionId),
            0,
            new CaptureSourceLocator.NativeId(locator),
            null,
            new CaptureSource("codex", "0.144.synthetic", "session_meta", null, null, null),
            new CaptureAdapter("codex-synthetic-jsonl", acceptedAdapterVersion),
            payload,
            [new CaptureEvent("metadata/0", 0, "lifecycle", "harness", payload, null, [])],
            null);
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            2_048);

        CaptureImportReceipt first = await ingestion.ImportAsync(binding, accepted);
        CaptureImportReceipt retry = await ingestion.ImportAsync(
            binding,
            accepted with
            {
                Adapter = accepted.Adapter with { Version = "7" }
            });

        Assert.Equal("new", first.Status);
        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(first.ObservationUuid, retry.ObservationUuid);
        CaptureConflictException changed = await Assert.ThrowsAsync<CaptureConflictException>(
            () => ingestion.ImportAsync(
                binding,
                accepted with
                {
                    Adapter = accepted.Adapter with { Version = "7" },
                    SourcePayload = JsonSerializer.SerializeToElement(new
                    {
                        message = "changed source record",
                        padding = new string('p', 8_192)
                    })
                }));
        Assert.Equal("accepted_source_conflict", changed.Reason);
    }

    [Fact]
    public async Task LegacySessionTransportOmissionImportsAndRetriesWithOneCanonicalIdentity()
    {
        string captureKey = CaptureCredential();
        const string sourceSessionId = "legacy-transport-session";
        await EnrollAsync($"legacy-transport-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
        var legacy = new CaptureObservationRequest(
            1,
            sourceSessionId,
            0,
            new CaptureLocator(
                "byte_range",
                null,
                0,
                2_048,
                new string('a', 64)),
            null,
            new CaptureSource("codex", null, new string('p', 2_048), null, null, null),
            new CaptureAdapter("legacy", "1"),
            payload,
            [new CaptureEvent("legacy/0", 0, "opaque", "harness", payload, null, [])]);

        BoundedCaptureRepresentation<CaptureObservationRequest> bounded =
            CaptureFidelityPolicy.SerializeForTransport(legacy, 1_024);
        CaptureObservationRequest request =
            JsonSerializer.Deserialize<CaptureObservationRequest>(
                bounded.Serialized,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(request.SourceSessionId);
        Assert.Equal(
            new CaptureSourceIdentity(sourceSessionId),
            request.SourceIdentity);
        Assert.Equal(
            sourceSessionId,
            request.SourcePayload.GetProperty("omission")
                .GetProperty("sourceIdentity")
                .GetProperty("externalSessionId").GetString());

        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(Path.Combine(_root, "config/never_store.yaml")));
        CaptureObservationCommand command =
            CaptureObservationCommand.FromRequest(request);
        CaptureImportReceipt first = await ingestion.ImportAsync(binding, command);
        CaptureImportReceipt retry = await ingestion.ImportAsync(binding, command);

        Assert.Equal("new", first.Status);
        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(first.ObservationUuid, retry.ObservationUuid);
        Assert.Equal(
            sourceSessionId,
            first.Observation.SourceIdentity.ExternalSessionId);
        Assert.Equal("healthy", first.Outcome.CaptureHealth);
        Assert.Equal("degraded", first.Outcome.CaptureFidelity);
        Assert.Equal(
            CaptureFidelityPolicy.TransportLimitReason,
            Assert.Single(first.Outcome.Counters).Reason);
        CapturedEventEnvelope envelope = Assert.Single(
            await new OperatorCaptureReads(RuntimeConnection)
                .ReadCapturedEventEnvelopesAsync(first.ObservationUuid));
        Assert.Equal(
            JsonSerializer.Serialize(first.Outcome, CaptureLedger.JsonOptions),
            JsonSerializer.Serialize(envelope.Outcome, CaptureLedger.JsonOptions));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MandatoryIdentityOrNativeLocatorThatCannotFitContentBoundAppendsNothing(
        bool oversizedIdentity)
    {
        string captureKey = CaptureCredential();
        string bindingName = $"unfit-content-{Guid.NewGuid():N}";
        await EnrollAsync(bindingName, captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        const string sentinel = "MANDATORY-RAW-VALUE-MUST-NOT-BE-CANONICAL";
        string oversized = string.Concat(Enumerable.Repeat(sentinel, 100));
        string sourceSessionId = oversizedIdentity ? oversized : UniqueSession();
        string locator = oversizedIdentity
            ? $"unfit-identity-{Guid.NewGuid():N}"
            : oversized;
        JsonElement payload = JsonSerializer.SerializeToElement(new { safe = true });
        var command = CaptureObservationCommand.FromRequest(
            new CaptureObservationRequest(
                1,
                sourceSessionId,
                0,
                new CaptureLocator("native_id", locator, null, null, null),
                null,
                new CaptureSource("codex", null, null, null, null, null),
                new CaptureAdapter("test", "1"),
                payload,
                [new CaptureEvent("test/0", 0, "opaque", "harness", payload, null, [])],
                SourceIdentity: new CaptureSourceIdentity(sourceSessionId)));
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(
                Path.Combine(_root, "config/never_store.yaml")),
            1_024);

        WriteSafetyScanException failure =
            await Assert.ThrowsAsync<WriteSafetyScanException>(
                () => ingestion.ImportAsync(binding, command));

        Assert.Contains("failed closed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, failure.ToString(), StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM capture_observations o
                JOIN capture_source_streams s USING (stream_uuid)
                JOIN capture_source_bindings b USING (binding_uuid)
                WHERE b.stable_name = @bindingName)
            """,
            new { bindingName }));
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM capture_source_streams s
                JOIN capture_source_bindings b USING (binding_uuid)
                WHERE b.stable_name = @bindingName)
            """,
            new { bindingName }));
    }

    private sealed class StatefulEventList(
        CaptureEvent retained,
        IReadOnlyList<CaptureEvent> later) : IReadOnlyList<CaptureEvent>
    {
        private int _enumerationCount;

        public int EnumerationCount => _enumerationCount;
        public int Count => 1;
        public CaptureEvent this[int index] => index == 0
            ? retained
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<CaptureEvent> GetEnumerator()
        {
            int enumeration = Interlocked.Increment(ref _enumerationCount);
            return (enumeration <= 3 ? [retained] : later).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static string CaptureCredential() => $"mcap_{Guid.NewGuid():N}";
    private static string UniqueSession() => $"safety-session-{Guid.NewGuid():N}";

    private async Task EnrollAsync(string name, string captureKey)
    {
        await new CaptureEnrollment(RuntimeConnection, SafetyGate()).EnrollAsync(
            name,
            "codex",
            $"capture:{name}",
            captureKey);
    }

    private static object Observation(
        string sourceSessionId, long position, string nativeId, string message) =>
        ObservationWithPayload(sourceSessionId, position, nativeId, new { text = message });

    private static object ObservationWithPayload(
        string sourceSessionId, long position, string nativeId, object payload) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = payload,
            events = new object[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    payload
                }
            }
        };

    [Fact]
    public async Task InjectedScannerOmissionsAppendAdvanceAndCountEveryOccurrence()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"scanner-omissions-{Guid.NewGuid():N}", captureKey);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey));
        CaptureObservationCommand Command(long position, string locator, JsonElement payload) =>
            CaptureObservationCommand.FromRequest(new CaptureObservationRequest(
                1,
                sourceSessionId,
                position,
                new CaptureLocator("native_id", locator, null, null, null),
                null,
                new CaptureSource("codex", null, "synthetic"),
                new CaptureAdapter("test", "1"),
                payload,
                [new CaptureEvent(
                    "event/0", 0, "opaque", "harness", payload, null, [])]));
        var ingestion = new CaptureIngestion(
            RuntimeConnection,
            new WriteSafetyGate(new SelectiveOmissionScanner()));
        JsonElement omittedPayload = JsonSerializer.SerializeToElement(
            new { first = "omit-me", second = "omit-me" });

        CaptureImportReceipt omitted = await ingestion.ImportAsync(
            binding,
            Command(0, $"scanner-omitted-{Guid.NewGuid():N}", omittedPayload));

        Assert.Equal("new", omitted.Status);
        CaptureOutcomeCounter counter = Assert.Single(omitted.Outcome.Counters);
        Assert.Equal(CaptureOutcomeReason.LeafExceedsLimit, counter.Reason);
        Assert.Equal(CaptureSizeBand.UpTo1MiB, counter.SizeBand);
        Assert.Equal(4, counter.Count);
        CaptureImportReceipt advanced = await ingestion.ImportAsync(
            binding,
            Command(
                1,
                $"scanner-safe-{Guid.NewGuid():N}",
                JsonSerializer.SerializeToElement(new { safe = "kept" })));
        Assert.Equal("new", advanced.Status);
        Assert.Equal(1, advanced.SourcePosition);
    }

    private sealed class SelectiveOmissionScanner : ISafetyScanner
    {
        public LeafOutcome ScanLeaf(
            string value,
            string? propertyName,
            ScanBudgetState state) =>
            value == "omit-me"
                ? LeafOutcome.Omitted(
                    CaptureOutcomeReason.LeafExceedsLimit,
                    Encoding.UTF8.GetByteCount(value))
                : LeafOutcome.Scanned(value, [], [], 0, null);

        public bool IsSensitiveField(string propertyName, ScanBudgetState state) => false;
    }

    private sealed class ThrowingSafetyScanner : ISafetyScanner
    {
        public LeafOutcome ScanLeaf(
            string value,
            string? propertyName,
            ScanBudgetState state) =>
            throw new InvalidOperationException("scanner implementation detail");

        public bool IsSensitiveField(string propertyName, ScanBudgetState state) =>
            throw new InvalidOperationException("scanner implementation detail");
    }
}
