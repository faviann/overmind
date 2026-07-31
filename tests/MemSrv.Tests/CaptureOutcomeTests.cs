using System.Text.Json;
using MemSrv.Core;

namespace MemSrv.Tests;

public sealed class CaptureOutcomeTests
{
    [Fact]
    public void OutcomeSummarySeparatesFidelityFromSafetyWithoutContentIdentity()
    {
        CaptureOutcomeSummary summary = CaptureOutcomeAggregation.Summarize(
        [
            CaptureOutcomeAggregation.FidelityOmission(
                "codex",
                CaptureFidelityPolicy.TransportLimitReason,
                1_048_577),
            CaptureOutcomeAggregation.FidelityOmission(
                "codex",
                CaptureFidelityPolicy.TransportLimitReason,
                2_000_000),
            CaptureOutcomeAggregation.SafetyFailure(
                "claude_code",
                CaptureOutcomeReason.MatcherTimeout,
                64L * 1024 * 1024 + 1)
        ]);

        Assert.Equal("blocked", summary.CaptureHealth);
        Assert.Equal("degraded", summary.CaptureFidelity);
        Assert.Equal(
        [
            new CaptureOutcomeCounter(
                "claude_code",
                CaptureOutcomeAggregation.SafetyFailureClass,
                CaptureOutcomeReason.MatcherTimeout,
                CaptureSizeBand.Over64MiBThrough128MiB,
                1),
            new CaptureOutcomeCounter(
                "codex",
                CaptureOutcomeAggregation.FidelityOmissionClass,
                CaptureFidelityPolicy.TransportLimitReason,
                CaptureSizeBand.Over1MiBThrough64MiB,
                2)
        ],
            summary.Counters);

        string json = JsonSerializer.Serialize(
            summary,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("1048577", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2000000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("67108865", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_048_576L, CaptureSizeBand.UpTo1MiB)]
    [InlineData(1_048_577L, CaptureSizeBand.Over1MiBThrough64MiB)]
    [InlineData(67_108_864L, CaptureSizeBand.Over1MiBThrough64MiB)]
    [InlineData(67_108_865L, CaptureSizeBand.Over64MiBThrough128MiB)]
    [InlineData(134_217_728L, CaptureSizeBand.Over64MiBThrough128MiB)]
    [InlineData(134_217_729L, CaptureSizeBand.Over128MiB)]
    public void SizeBandsAreBoundedAtPublishedFidelityLimits(
        long bytes,
        string expectedBand)
    {
        CaptureOutcomeRecord outcome = CaptureOutcomeAggregation.FidelityOmission(
            "codex",
            CaptureFidelityPolicy.ContentLimitReason,
            bytes);

        Assert.Equal(expectedBand, outcome.SizeBand);
    }

    [Theory]
    [InlineData(CaptureFidelityPolicy.MalformedJsonReason)]
    [InlineData(CaptureFidelityPolicy.InvalidUtf8ContentPolicy)]
    public void CanonicalTerminalOmissionUsesScanProvenanceAndTrustedByteRange(
        string reason)
    {
        CaptureObservationReceipt observation = CanonicalObservation(
            reason,
            new CaptureSourceLocator.ByteRange(
                0,
                2L * 1024 * 1024,
                null),
            JsonSerializer.SerializeToElement(new { safe = true }));

        CaptureOutcomeCounter outcome = Assert.Single(
            CaptureOutcomeAggregation.FromCanonical(observation).Counters);

        Assert.Equal(reason, outcome.Reason);
        Assert.Equal(CaptureSizeBand.Over1MiBThrough64MiB, outcome.SizeBand);
    }

    [Fact]
    public void AdapterAndRecordTypeCannotSpoofTerminalOmission()
    {
        CaptureObservationReceipt observation = CanonicalObservation(
            reason: null,
            new CaptureSourceLocator.ByteRange(0, 2L * 1024 * 1024, null),
            JsonSerializer.SerializeToElement(new { safe = true }),
            recordType: "malformed_json");

        CaptureOutcomeSummary outcome =
            CaptureOutcomeAggregation.FromCanonical(observation);

        Assert.Equal("complete", outcome.CaptureFidelity);
        Assert.Empty(outcome.Counters);
    }

    [Fact]
    public void CanonicalWholeOmissionUsesOnlyExactPolicyProvenanceCount()
    {
        const string externalSessionId = "canonical-session";
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            omission = new
            {
                reason = CaptureFidelityPolicy.TransportLimitReason,
                originalByteCount = 2L * 1024 * 1024,
                policyVersion = CaptureFidelityPolicy.CurrentVersion,
                sourceIdentity = new
                {
                    externalSessionId,
                    childId = (string?)null,
                    sourcePosition = 0,
                    locatorKind = "byte_range"
                }
            }
        });
        CaptureObservationReceipt observation = CanonicalObservation(
            CaptureFidelityPolicy.TransportLimitReason,
            new CaptureSourceLocator.ByteRange(0, 1, null),
            payload,
            adapter: new CaptureAdapter(
                "capture-fidelity-policy",
                CaptureFidelityPolicy.CurrentVersion),
            externalSessionId: externalSessionId);

        CaptureOutcomeCounter outcome = Assert.Single(
            CaptureOutcomeAggregation.FromCanonical(observation).Counters);

        Assert.Equal(CaptureSizeBand.Over1MiBThrough64MiB, outcome.SizeBand);
    }

    [Fact]
    public void NativeBinaryOutcomeStaysUnknownWhenCountCannotBeReconstructed()
    {
        CaptureObservationReceipt observation = CanonicalObservation(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            new CaptureSourceLocator.NativeId("native"),
            JsonSerializer.SerializeToElement(new { safe = true }));

        CaptureOutcomeCounter outcome = Assert.Single(
            CaptureOutcomeAggregation.FromCanonical(observation).Counters);

        Assert.Equal(CaptureSizeBand.Unknown, outcome.SizeBand);
    }

    [Fact]
    public void CanonicalBinaryOmissionsPreserveEachExactPolicyCountByBoundedBand()
    {
        const string externalSessionId = "canonical-binary-session";
        object Omission(long originalByteCount) => new
        {
            reason = CaptureFidelityPolicy.UnsupportedBinaryReason,
            category = "image",
            originalByteCount,
            policyVersion = CaptureFidelityPolicy.CurrentVersion,
            sourceIdentity = new
            {
                externalSessionId,
                sourcePosition = 0,
                locatorKind = "native_id"
            }
        };
        CaptureObservationReceipt observation = CanonicalObservation(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            new CaptureSourceLocator.NativeId("native"),
            JsonSerializer.SerializeToElement(new
            {
                capture_fidelity_omission = Omission(4),
                capture_fidelity_omission_1 = Omission(4),
                capture_fidelity_omission_2 = Omission(2L * 1024 * 1024)
            }),
            externalSessionId: externalSessionId);

        CaptureOutcomeSummary outcome =
            CaptureOutcomeAggregation.FromCanonical(observation);

        Assert.Collection(
            outcome.Counters,
            counter =>
            {
                Assert.Equal(CaptureSizeBand.Over1MiBThrough64MiB, counter.SizeBand);
                Assert.Equal(1, counter.Count);
            },
            counter =>
            {
                Assert.Equal(CaptureSizeBand.UpTo1MiB, counter.SizeBand);
                Assert.Equal(2, counter.Count);
            });
        string serialized = JsonSerializer.Serialize(
            outcome,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("originalByteCount", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("2097152", serialized, StringComparison.Ordinal);
    }

    private static CaptureObservationReceipt CanonicalObservation(
        string? reason,
        CaptureSourceLocator locator,
        JsonElement payload,
        string? recordType = null,
        CaptureAdapter? adapter = null,
        string externalSessionId = "canonical-session") =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CaptureSourceIdentity(externalSessionId),
            new CaptureSource(
                "codex",
                null,
                recordType,
                MaterialKind: "persisted_record"),
            locator,
            null,
            null,
            adapter ?? new CaptureAdapter("codex-synthetic-jsonl", "10"),
            payload,
            new CaptureScanReceipt(
                reason is null ? "clean" : "omitted",
                "rules",
                reason is null ? [] : [$"omission:{reason}"],
                [],
                0),
            DateTimeOffset.UtcNow);
}
