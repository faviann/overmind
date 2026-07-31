using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemSrv.Core;

/// <summary>
/// Closed, content-free reasons exposed by capture outcome projections.
/// Fidelity reasons reuse <see cref="CaptureFidelityPolicy"/> constants.
/// </summary>
public static class CaptureOutcomeReason
{
    public const string InvalidEncoding = CaptureFidelityPolicy.InvalidUtf8ContentPolicy;
    public const string MatcherTimeout = "matcher_timeout";
    public const string RequiredInspectionIncomplete = "required_inspection_incomplete";
    public const string ScanBudgetExhausted = "scan_budget_exhausted";
    public const string ScannerInternalFailure = "scanner_internal_failure";
    public const string ScannerPolicyUnavailable = "scanner_policy_unavailable";

    internal static bool IsFidelity(string reason) =>
        reason is CaptureFidelityPolicy.TransportLimitReason
            or CaptureFidelityPolicy.ContentLimitReason
            or CaptureFidelityPolicy.UnsupportedBinaryReason
            or CaptureFidelityPolicy.MalformedJsonReason
            or CaptureFidelityPolicy.UninspectableSourceRecordReason
            or InvalidEncoding;

    internal static bool IsSafetyFailure(string reason) =>
        reason is MatcherTimeout
            or RequiredInspectionIncomplete
            or ScanBudgetExhausted
            or ScannerInternalFailure
            or ScannerPolicyUnavailable;
}

/// <summary>
/// Deliberately coarse byte bands. An outcome never exposes an exact byte
/// count, excerpt, digest, locator, credential, or request.
/// </summary>
public static class CaptureSizeBand
{
    public const string Unknown = "unknown";
    public const string UpTo1MiB = "up_to_1_mib";
    public const string Over1MiBThrough64MiB = "over_1_mib_through_64_mib";
    public const string Over64MiBThrough128MiB = "over_64_mib_through_128_mib";
    public const string Over128MiB = "over_128_mib";
}

/// <summary>One content-free input to the aggregation seam.</summary>
public sealed record CaptureOutcomeRecord
{
    [JsonConstructor]
    public CaptureOutcomeRecord(
        string harness,
        string @class,
        string reason,
        string sizeBand)
    {
        CaptureOutcomeContract.ValidateDimensions(harness, @class, reason, sizeBand);
        Harness = harness;
        Class = @class;
        Reason = reason;
        SizeBand = sizeBand;
    }

    public string Harness { get; }
    public string Class { get; }
    public string Reason { get; }
    public string SizeBand { get; }
}

/// <summary>One grouped counter; no per-record identity is retained.</summary>
public sealed record CaptureOutcomeCounter
{
    [JsonConstructor]
    public CaptureOutcomeCounter(
        string harness,
        string @class,
        string reason,
        string sizeBand,
        long count)
    {
        CaptureOutcomeContract.ValidateDimensions(harness, @class, reason, sizeBand);
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), "Capture outcome count must be positive.");
        }
        Harness = harness;
        Class = @class;
        Reason = reason;
        SizeBand = sizeBand;
        Count = count;
    }

    public string Harness { get; }
    public string Class { get; }
    public string Reason { get; }
    public string SizeBand { get; }
    public long Count { get; }
}

/// <summary>
/// Health and fidelity are independent: deterministic omissions degrade
/// fidelity, while operational safety failures block health.
/// </summary>
public sealed class CaptureOutcomeSummary
{
    [JsonConstructor]
    public CaptureOutcomeSummary(
        int contractVersion,
        string captureHealth,
        string captureFidelity,
        IReadOnlyList<CaptureOutcomeCounter> counters)
    {
        if (contractVersion != 1)
        {
            throw new ArgumentException(
                "Capture outcome contract version is not recognized.",
                nameof(contractVersion));
        }
        ArgumentNullException.ThrowIfNull(counters);
        CaptureOutcomeCounter[] materialized = counters.ToArray();
        if (materialized.Any(counter => counter is null))
        {
            throw new ArgumentException(
                "Capture outcome counters must be valid.", nameof(counters));
        }
        string expectedHealth = materialized.Any(
            counter => counter.Class == CaptureOutcomeAggregation.SafetyFailureClass)
                ? "blocked"
                : "healthy";
        string expectedFidelity = materialized.Any(
            counter => counter.Class == CaptureOutcomeAggregation.FidelityOmissionClass)
                ? "degraded"
                : "complete";
        if (!string.Equals(captureHealth, expectedHealth, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capture outcome health is not recognized.", nameof(captureHealth));
        }
        if (!string.Equals(captureFidelity, expectedFidelity, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capture outcome fidelity is not recognized.", nameof(captureFidelity));
        }
        ContractVersion = contractVersion;
        CaptureHealth = captureHealth;
        CaptureFidelity = captureFidelity;
        Counters = Array.AsReadOnly(materialized);
    }

    public int ContractVersion { get; }
    public string CaptureHealth { get; }
    public string CaptureFidelity { get; }
    public IReadOnlyList<CaptureOutcomeCounter> Counters { get; }
}

internal static class CaptureOutcomeContract
{
    internal static void ValidateDimensions(
        string harness,
        string @class,
        string reason,
        string sizeBand)
    {
        bool recognized =
            @class == CaptureOutcomeAggregation.FidelityOmissionClass
                ? CaptureOutcomeReason.IsFidelity(reason)
                : @class == CaptureOutcomeAggregation.SafetyFailureClass
                    && CaptureOutcomeReason.IsSafetyFailure(reason);
        if (!recognized)
        {
            throw new ArgumentException("Capture outcome is not recognized.");
        }
        if (harness is not ("codex" or "claude_code" or "other"))
        {
            throw new ArgumentException("Capture outcome harness is not recognized.");
        }
        if (sizeBand is not (
                CaptureSizeBand.Unknown
                or CaptureSizeBand.UpTo1MiB
                or CaptureSizeBand.Over1MiBThrough64MiB
                or CaptureSizeBand.Over64MiBThrough128MiB
                or CaptureSizeBand.Over128MiB))
        {
            throw new ArgumentException("Capture outcome size band is not recognized.");
        }
    }
}

/// <summary>
/// Narrow content-free projection shared by module, runtime, API, and operator
/// receipt seams. It is computed from operation outcomes and creates no store.
/// </summary>
public static class CaptureOutcomeAggregation
{
    public const string FidelityOmissionClass = "fidelity_omission";
    public const string SafetyFailureClass = "safety_failure";

    public static CaptureOutcomeRecord FidelityOmission(
        string harness,
        string reason,
        long? originalByteCount)
    {
        if (!CaptureOutcomeReason.IsFidelity(reason))
        {
            throw new ArgumentException(
                "Capture fidelity reason is not recognized.",
                nameof(reason));
        }
        return new(
            NormalizeHarness(harness),
            FidelityOmissionClass,
            reason,
            originalByteCount is null
                ? CaptureSizeBand.Unknown
                : SizeBand(originalByteCount.Value));
    }

    public static CaptureOutcomeRecord SafetyFailure(
        string harness,
        string reason,
        long? inspectedByteCount = null)
    {
        if (!CaptureOutcomeReason.IsSafetyFailure(reason))
        {
            throw new ArgumentException(
                "Capture safety failure reason is not recognized.",
                nameof(reason));
        }
        return new(
            NormalizeHarness(harness),
            SafetyFailureClass,
            reason,
            inspectedByteCount is null
                ? CaptureSizeBand.Unknown
                : SizeBand(inspectedByteCount.Value));
    }

    public static CaptureOutcomeSummary Summarize(
        IEnumerable<CaptureOutcomeRecord> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        CaptureOutcomeRecord[] materialized = outcomes.ToArray();
        CaptureOutcomeCounter[] counters = materialized
            .GroupBy(
                outcome => (
                    outcome.Harness,
                    outcome.Class,
                    outcome.Reason,
                    outcome.SizeBand))
            .Select(group => new CaptureOutcomeCounter(
                group.Key.Harness,
                group.Key.Class,
                group.Key.Reason,
                group.Key.SizeBand,
                group.LongCount()))
            .OrderBy(counter => counter.Harness, StringComparer.Ordinal)
            .ThenBy(counter => counter.Class, StringComparer.Ordinal)
            .ThenBy(counter => counter.Reason, StringComparer.Ordinal)
            .ThenBy(counter => counter.SizeBand, StringComparer.Ordinal)
            .ToArray();

        return new(
            1,
            materialized.Any(outcome => outcome.Class == SafetyFailureClass)
                ? "blocked"
                : "healthy",
            materialized.Any(outcome => outcome.Class == FidelityOmissionClass)
                ? "degraded"
                : "complete",
            counters);
    }

    public static CaptureOutcomeSummary Empty { get; } = Summarize([]);

    public static CaptureOutcomeSummary FromCanonical(
        CaptureObservationReceipt observation,
        IEnumerable<JsonElement>? eventPayloads = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var reasons = observation.Scan.RuleIds
            .Where(ruleId => ruleId.StartsWith("omission:", StringComparison.Ordinal))
            .Select(ruleId => ruleId["omission:".Length..])
            .Where(CaptureOutcomeReason.IsFidelity)
            .ToHashSet(StringComparer.Ordinal);

        CaptureDeterministicFidelity? wholeObservation =
            CaptureFidelityPolicy.ClassifyCanonicalWholeObservationOmission(
                observation);
        var outcomes = reasons
            .Where(reason => reason != CaptureFidelityPolicy.UnsupportedBinaryReason)
            .Select(reason => FidelityOmission(
                observation.Source.Harness,
                reason,
                wholeObservation?.Reason == reason
                    ? wholeObservation.OriginalByteCount
                    : reason is CaptureFidelityPolicy.TransportLimitReason
                        or CaptureFidelityPolicy.ContentLimitReason
                            ? null
                            : observation.Locator is CaptureSourceLocator.ByteRange range
                                ? range.Length
                                : null))
            .ToList();
        if (reasons.Contains(CaptureFidelityPolicy.UnsupportedBinaryReason))
        {
            long? wholeCount = wholeObservation?.Reason
                == CaptureFidelityPolicy.UnsupportedBinaryReason
                    ? wholeObservation.OriginalByteCount
                    : null;
            if (wholeCount is not null)
            {
                outcomes.Add(FidelityOmission(
                    observation.Source.Harness,
                    CaptureFidelityPolicy.UnsupportedBinaryReason,
                    wholeCount));
            }
            else
            {
                IReadOnlyList<long> counts =
                    CaptureFidelityPolicy.UnsupportedBinaryOmissionByteCounts(
                        observation,
                        eventPayloads ?? []);
                if (counts.Count == 0)
                {
                    outcomes.Add(FidelityOmission(
                        observation.Source.Harness,
                        CaptureFidelityPolicy.UnsupportedBinaryReason,
                        observation.Locator is CaptureSourceLocator.ByteRange range
                            ? range.Length
                            : null));
                }
                else
                {
                    outcomes.AddRange(counts.Select(count => FidelityOmission(
                        observation.Source.Harness,
                        CaptureFidelityPolicy.UnsupportedBinaryReason,
                        count)));
                }
            }
        }
        return Summarize(outcomes);
    }

    public static string Classify(SafetyScanException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.OutcomeReason;
    }

    private static string NormalizeHarness(string harness) =>
        harness switch
        {
            "codex" => "codex",
            "claude" or "claude_code" => "claude_code",
            _ => "other"
        };

    private static string SizeBand(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes), bytes, "Capture byte count cannot be negative.");
        }
        return bytes switch
        {
            <= 1L * 1024 * 1024 => CaptureSizeBand.UpTo1MiB,
            <= 64L * 1024 * 1024 => CaptureSizeBand.Over1MiBThrough64MiB,
            <= 128L * 1024 * 1024 => CaptureSizeBand.Over64MiBThrough128MiB,
            _ => CaptureSizeBand.Over128MiB
        };
    }

}
