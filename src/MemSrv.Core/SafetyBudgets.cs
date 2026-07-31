namespace MemSrv.Core;

/// <summary>
/// The versioned numeric ceilings one deterministic safety scan may spend.
/// Every dimension the research note requires is named here so a reviewer can
/// see the whole bound in one place, and so a test can inject a smaller budget
/// to exercise a mechanism without paying for the production number.
/// Published defaults and their measured justification live in
/// <c>docs/capture-safety-budgets.md</c>.
/// </summary>
public sealed record SafetyBudgets
{
    /// <summary>Bump when any default below changes.</summary>
    public const string CurrentVersion = "capture-safety-budgets/2026-07-26.2";

    /// <summary>Maximum UTF-8 bytes in one source observation.</summary>
    public required long MaxObservationBytes { get; init; }

    /// <summary>Maximum UTF-8 bytes in one decoded structured leaf value.</summary>
    public required long MaxLeafBytes { get; init; }

    /// <summary>Total wall-clock deadline for one scan call.</summary>
    public required TimeSpan MaxScanTime { get; init; }

    /// <summary>Per-rule matcher timeout handed to the regex engine.</summary>
    public required TimeSpan MaxRuleTime { get; init; }

    /// <summary>Maximum matches one scan call may accumulate.</summary>
    public required int MaxMatches { get; init; }

    /// <summary>Maximum encoded candidates one scan call may decode.</summary>
    public required int MaxDecoderCandidates { get; init; }

    /// <summary>
    /// Longest encoded run that qualifies as a decode candidate. This is a
    /// qualification bound, not a fail-closed budget: a longer run is simply
    /// not decoded, so a secret encoded inside it is NOT detected. That is an
    /// accepted, bounded residual risk whose threat model is accidental
    /// leakage, not a determined evader. See docs/capture-safety-budgets.md.
    /// </summary>
    public required int MaxDecoderCandidateLength { get; init; }

    /// <summary>Maximum decoded bytes one scan call may produce.</summary>
    public required long MaxDecodedBytes { get; init; }

    public required string Version { get; init; }

    public static readonly SafetyBudgets Default = new()
    {
        Version = CurrentVersion,
        MaxObservationBytes = 128L * 1024 * 1024,
        MaxLeafBytes = 64L * 1024 * 1024,
        MaxScanTime = TimeSpan.FromSeconds(30),
        MaxRuleTime = TimeSpan.FromSeconds(5),
        MaxMatches = 10_000,
        MaxDecoderCandidates = 65_536,
        // 64 KiB: large enough that a base64'd credentials file, kubeconfig, or
        // JWT is always decoded rather than skipped.
        MaxDecoderCandidateLength = 65_536,
        MaxDecodedBytes = 16L * 1024 * 1024
    };
}

/// <summary>
/// The rule set cannot be used at all: missing, empty, invalid, duplicated,
/// unsupported, or un-loadable configuration. Derives from
/// <see cref="InvalidOperationException"/> so every existing fail-closed caller
/// (HTTP 400, memctl exit 1) keeps its behavior while gaining the reason.
/// </summary>
public sealed class SafetyConfigurationException(string reason)
    : InvalidOperationException($"Capture safety rules are not usable: {reason}.")
{
    public string Reason { get; } = reason;
    public CaptureOutcomeSummary? Outcome { get; private set; }

    public void ReportCaptureOutcome(string harness, long? inspectedByteCount = null) =>
        Outcome = CaptureOutcomeAggregation.Summarize(
        [
            CaptureOutcomeAggregation.SafetyFailure(
                harness,
                CaptureOutcomeReason.ScannerPolicyUnavailable,
                inspectedByteCount)
        ]);
}

/// <summary>
/// A scan could not complete within its bounds, or a required value could not
/// be inspected completely. Callers must persist nothing and advance nothing.
/// </summary>
public class SafetyScanException : InvalidOperationException
{
    public SafetyScanException(string outcomeReason, string reason)
        : base($"Capture safety scan failed closed: {reason}.")
    {
        if (outcomeReason is not (
                CaptureOutcomeReason.MatcherTimeout
                or CaptureOutcomeReason.ScanBudgetExhausted
                or CaptureOutcomeReason.RequiredInspectionIncomplete
                or CaptureOutcomeReason.ScannerInternalFailure))
        {
            throw new ArgumentException(
                "Capture safety scan outcome reason is not recognized.",
                nameof(outcomeReason));
        }
        OutcomeReason = outcomeReason;
        Reason = reason;
    }

    public string OutcomeReason { get; }
    public string Reason { get; }
    public CaptureOutcomeSummary? Outcome { get; private set; }

    public void ReportCaptureOutcome(string harness, long? inspectedByteCount = null) =>
        Outcome = CaptureOutcomeAggregation.Summarize(
        [
            CaptureOutcomeAggregation.SafetyFailure(
                harness,
                CaptureOutcomeAggregation.Classify(this),
                inspectedByteCount)
        ]);
}

/// <summary>
/// An unexpected failure thrown by the scanner implementation itself. The
/// original exception and candidate content deliberately do not cross the
/// safety boundary.
/// </summary>
public sealed class SafetyScannerInternalException()
    : SafetyScanException(
        CaptureOutcomeReason.ScannerInternalFailure,
        "the scanner failed internally");
