using System.Text.Json;

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
public sealed record CaptureOutcomeRecord(
    string Harness,
    string Class,
    string Reason,
    string SizeBand);

/// <summary>One grouped counter; no per-record identity is retained.</summary>
public sealed record CaptureOutcomeCounter(
    string Harness,
    string Class,
    string Reason,
    string SizeBand,
    long Count);

/// <summary>
/// Health and fidelity are independent: deterministic omissions degrade
/// fidelity, while operational safety failures block health.
/// </summary>
public sealed class CaptureOutcomeSummary(
    int contractVersion,
    string captureHealth,
    string captureFidelity,
    IReadOnlyList<CaptureOutcomeCounter> counters)
    : IEquatable<CaptureOutcomeSummary>
{
    public int ContractVersion { get; } = contractVersion;
    public string CaptureHealth { get; } = captureHealth;
    public string CaptureFidelity { get; } = captureFidelity;
    public IReadOnlyList<CaptureOutcomeCounter> Counters { get; } = counters;

    public bool Equals(CaptureOutcomeSummary? other) =>
        other is not null
        && ContractVersion == other.ContractVersion
        && string.Equals(CaptureHealth, other.CaptureHealth, StringComparison.Ordinal)
        && string.Equals(CaptureFidelity, other.CaptureFidelity, StringComparison.Ordinal)
        && Counters.SequenceEqual(other.Counters);

    public override bool Equals(object? obj) =>
        obj is CaptureOutcomeSummary other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ContractVersion);
        hash.Add(CaptureHealth, StringComparer.Ordinal);
        hash.Add(CaptureFidelity, StringComparer.Ordinal);
        foreach (CaptureOutcomeCounter counter in Counters)
        {
            hash.Add(counter);
        }
        return hash.ToHashCode();
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
        foreach (CaptureOutcomeRecord outcome in materialized)
        {
            Validate(outcome);
        }

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
        CaptureObservationReceipt observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var reasons = observation.Scan.RuleIds
            .Where(ruleId => ruleId.StartsWith("omission:", StringComparison.Ordinal))
            .Select(ruleId => ruleId["omission:".Length..])
            .Where(CaptureOutcomeReason.IsFidelity)
            .ToHashSet(StringComparer.Ordinal);

        return Summarize(reasons.Select(reason => FidelityOmission(
            observation.Source.Harness,
            reason,
            CanonicalByteCount(observation, reason))));
    }

    public static string Classify(SafetyScanException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        string reason = failure.Reason;
        if (reason.Contains("matcher timeout", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureOutcomeReason.MatcherTimeout;
        }
        if (reason.Contains("budget", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("deadline", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureOutcomeReason.ScanBudgetExhausted;
        }
        if (reason.Contains(
                "could not be inspected completely",
                StringComparison.OrdinalIgnoreCase)
            || reason.Contains(
                "could not be reconstructed",
                StringComparison.OrdinalIgnoreCase))
        {
            return CaptureOutcomeReason.RequiredInspectionIncomplete;
        }
        return CaptureOutcomeReason.ScannerInternalFailure;
    }

    private static long? CanonicalByteCount(
        CaptureObservationReceipt observation,
        string reason)
    {
        if (reason is CaptureFidelityPolicy.TransportLimitReason
            or CaptureFidelityPolicy.ContentLimitReason)
        {
            return PolicyOmissionByteCount(observation, reason);
        }
        if (reason == CaptureFidelityPolicy.UnsupportedBinaryReason
            && PolicyOmissionByteCount(observation, reason) is { } wholeCount)
        {
            return wholeCount;
        }
        return observation.Locator is CaptureSourceLocator.ByteRange range
            ? range.Length
            : null;
    }

    private static long? PolicyOmissionByteCount(
        CaptureObservationReceipt observation,
        string expectedReason)
    {
        if (!string.Equals(
                observation.Adapter.Name,
                "capture-fidelity-policy",
                StringComparison.Ordinal)
            || !string.Equals(
                observation.Adapter.Version,
                CaptureFidelityPolicy.CurrentVersion,
                StringComparison.Ordinal)
            || observation.SafeSourcePayload.ValueKind != JsonValueKind.Object
            || !observation.SafeSourcePayload.TryGetProperty(
                "omission",
                out JsonElement omission)
            || !HasOnlyProperties(
                omission,
                "reason",
                "originalByteCount",
                "policyVersion",
                "sourceIdentity")
            || !HasString(omission, "reason", expectedReason)
            || !HasString(
                omission,
                "policyVersion",
                CaptureFidelityPolicy.CurrentVersion)
            || !omission.TryGetProperty(
                "originalByteCount",
                out JsonElement countElement)
            || !countElement.TryGetInt64(out long count)
            || count < 0
            || !omission.TryGetProperty(
                "sourceIdentity",
                out JsonElement sourceIdentity)
            || !HasCanonicalSourceIdentity(sourceIdentity, observation))
        {
            return null;
        }
        return count;
    }

    private static bool HasCanonicalSourceIdentity(
        JsonElement value,
        CaptureObservationReceipt observation)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || property.Name is not (
                    "externalSessionId"
                    or "childId"
                    or "sourcePosition"
                    or "locatorKind"))
            {
                return false;
            }
        }
        bool hasChild = value.TryGetProperty("childId", out JsonElement child);
        bool childMatches = observation.SourceIdentity.ChildId is null
            ? !hasChild || child.ValueKind == JsonValueKind.Null
            : hasChild
                && child.ValueKind == JsonValueKind.String
                && string.Equals(
                    child.GetString(),
                    observation.SourceIdentity.ChildId,
                    StringComparison.Ordinal);
        return seen.Count == (hasChild ? 4 : 3)
            && HasString(
                value,
                "externalSessionId",
                observation.SourceIdentity.ExternalSessionId)
            && childMatches
            && value.TryGetProperty("sourcePosition", out JsonElement position)
            && position.TryGetInt64(out long sourcePosition)
            && sourcePosition >= 0
            && HasString(value, "locatorKind", observation.Locator.Kind);
    }

    private static bool HasOnlyProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }
        return seen.SetEquals(expected);
    }

    private static bool HasString(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
        && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

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

    private static void Validate(CaptureOutcomeRecord outcome)
    {
        bool recognized =
            outcome.Class == FidelityOmissionClass
                ? CaptureOutcomeReason.IsFidelity(outcome.Reason)
                : outcome.Class == SafetyFailureClass
                    && CaptureOutcomeReason.IsSafetyFailure(outcome.Reason);
        if (!recognized)
        {
            throw new ArgumentException("Capture outcome is not recognized.");
        }
        if (outcome.Harness is not ("codex" or "claude_code" or "other"))
        {
            throw new ArgumentException("Capture outcome harness is not recognized.");
        }
        if (outcome.SizeBand is not (
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
