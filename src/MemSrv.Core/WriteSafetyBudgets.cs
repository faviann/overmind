namespace MemSrv.Core;

/// <summary>
/// The versioned numeric ceilings one deterministic safety scan may spend.
/// Every dimension the research note requires is named here so a reviewer can
/// see the whole bound in one place, and so a test can inject a smaller budget
/// to exercise a mechanism without paying for the production number.
/// Published defaults and their measured justification live in
/// <c>docs/write-safety.md</c>.
/// </summary>
public sealed record WriteSafetyBudgets
{
    /// <summary>Bump when any default below changes.</summary>
    public const string CurrentVersion = "write-safety-budgets/2026-08-30.1";

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
    /// leakage, not a determined evader. See docs/write-safety.md.
    /// </summary>
    public required int MaxDecoderCandidateLength { get; init; }

    /// <summary>Maximum decoded bytes one scan call may produce.</summary>
    public required long MaxDecodedBytes { get; init; }

    public required string Version { get; init; }

    public static readonly WriteSafetyBudgets Default = new()
    {
        Version = CurrentVersion,
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
public sealed class WriteSafetyConfigurationException(string reason)
    : InvalidOperationException($"Write-safety rules are not usable: {reason}.")
{
    public string Reason { get; } = reason;
}

/// <summary>Closed machine-readable reasons for an incomplete safety inspection.</summary>
public static class WriteSafetyFailureCode
{
    public const string MatcherTimeout = "matcher_timeout";
    public const string RequiredInspectionIncomplete = "required_inspection_incomplete";
    public const string ScanBudgetExhausted = "scan_budget_exhausted";
    public const string ScannerInternalFailure = "scanner_internal_failure";
    public const string ScannerPolicyUnavailable = "scanner_policy_unavailable";

    public static bool IsKnown(string code) =>
        code is MatcherTimeout
            or RequiredInspectionIncomplete
            or ScanBudgetExhausted
            or ScannerInternalFailure
            or ScannerPolicyUnavailable;
}

/// <summary>
/// A scan could not complete within its bounds, or a required value could not
/// be inspected completely. Callers must persist nothing and advance nothing.
/// </summary>
public class WriteSafetyScanException : InvalidOperationException
{
    public WriteSafetyScanException(string failureCode, string reason)
        : base($"Write-safety scan failed closed: {reason}.")
    {
        if (!WriteSafetyFailureCode.IsKnown(failureCode)
            || failureCode == WriteSafetyFailureCode.ScannerPolicyUnavailable)
        {
            throw new ArgumentException(
                "Write-safety scan failure code is not recognized.",
                nameof(failureCode));
        }
        FailureCode = failureCode;
        Reason = reason;
    }

    public string FailureCode { get; }
    public string Reason { get; }
}

/// <summary>
/// An unexpected failure thrown by the scanner implementation itself. The
/// original exception and candidate content deliberately do not cross the
/// safety boundary.
/// </summary>
public sealed class WriteSafetyScannerInternalException()
    : WriteSafetyScanException(
        WriteSafetyFailureCode.ScannerInternalFailure,
        "the scanner failed internally");
