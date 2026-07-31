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

    [Fact]
    public void SafetyOutcomeClassificationDoesNotDependOnHumanWording()
    {
        var failure = new SafetyScanException(
            CaptureOutcomeReason.MatcherTimeout,
            "wording with no classification keywords");

        Assert.Equal(
            CaptureOutcomeReason.MatcherTimeout,
            failure.OutcomeReason);
        Assert.Contains("wording with no classification keywords", failure.Message);
    }

    [Theory]
    [InlineData(CaptureOutcomeReason.MatcherTimeout)]
    [InlineData(CaptureOutcomeReason.ScanBudgetExhausted)]
    [InlineData(CaptureOutcomeReason.RequiredInspectionIncomplete)]
    [InlineData(CaptureOutcomeReason.ScannerInternalFailure)]
    public void SafetyScanExceptionAcceptsEveryClosedMachineReason(string reason)
    {
        var failure = new SafetyScanException(reason, "safe prose");

        Assert.Equal(reason, failure.OutcomeReason);
    }

    [Fact]
    public void SafetyScanExceptionRejectsAnOpenMachineReasonWithoutEchoingIt()
    {
        const string contentLike = "secret-content-machine-reason";

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => new SafetyScanException(contentLike, "safe prose"));

        Assert.DoesNotContain(contentLike, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("record", "fidelity_omission", "observation_exceeds_content_limit", "unknown")]
    [InlineData("codex", "raw-content", "observation_exceeds_content_limit", "unknown")]
    [InlineData("codex", "fidelity_omission", "raw-content", "unknown")]
    [InlineData("codex", "fidelity_omission", "observation_exceeds_content_limit", "raw-content")]
    public void OutcomeRecordAndCounterConstructorsRejectOpenDimensions(
        string harness,
        string @class,
        string reason,
        string sizeBand)
    {
        Assert.Throws<ArgumentException>(
            () => new CaptureOutcomeRecord(harness, @class, reason, sizeBand));
        Assert.Throws<ArgumentException>(
            () => new CaptureOutcomeCounter(harness, @class, reason, sizeBand, 1));
    }

    [Fact]
    public void OutcomeSummaryConstructorRejectsOpenAndContradictoryState()
    {
        Assert.Throws<ArgumentException>(
            () => new CaptureOutcomeSummary(1, "raw-content", "complete", []));
        Assert.Throws<ArgumentException>(
            () => new CaptureOutcomeSummary(1, "healthy", "raw-content", []));
        Assert.Throws<ArgumentException>(() => new CaptureOutcomeSummary(
            1,
            "healthy",
            "complete",
            [
                new CaptureOutcomeCounter(
                    "codex",
                    CaptureOutcomeAggregation.SafetyFailureClass,
                    CaptureOutcomeReason.MatcherTimeout,
                    CaptureSizeBand.Unknown,
                    1)
            ]));
    }

    [Fact]
    public void OutcomeSummaryConstructorRejectsDuplicateAndNoncanonicalCounters()
    {
        var first = new CaptureOutcomeCounter(
            "codex",
            CaptureOutcomeAggregation.FidelityOmissionClass,
            CaptureOutcomeReason.LeafExceedsLimit,
            CaptureSizeBand.Unknown,
            1);
        var second = new CaptureOutcomeCounter(
            "codex",
            CaptureOutcomeAggregation.FidelityOmissionClass,
            CaptureOutcomeReason.SensitiveFieldScalar,
            CaptureSizeBand.Unknown,
            1);

        Assert.Throws<ArgumentException>(() => new CaptureOutcomeSummary(
            1, "healthy", "degraded", [first, first]));
        Assert.Throws<ArgumentException>(() => new CaptureOutcomeSummary(
            1, "healthy", "degraded", [second, first]));
    }

    [Fact]
    public void OutcomeJsonDeserializationRejectsContentLikeDimensionsWithoutEcho()
    {
        const string contentLike = "private-content-must-not-echo";
        string json = $$"""
            {
              "contractVersion": 1,
              "captureHealth": "healthy",
              "captureFidelity": "degraded",
              "counters": [{
                "harness": "codex",
                "class": "fidelity_omission",
                "reason": "{{contentLike}}",
                "sizeBand": "unknown",
                "count": 1
              }]
            }
            """;

        Exception failure = Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize<CaptureOutcomeSummary>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.DoesNotContain(contentLike, failure.ToString(), StringComparison.Ordinal);
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
