using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemSrv.Core;

/// <summary>
/// Why an entire value was dropped instead of span-redacted. This vocabulary is
/// closed and documented in docs/capture-safety-budgets.md; the marker written
/// into the sanitized value is <c>[OMITTED:&lt;reason&gt;]</c>, deliberately
/// distinct from the <c>[REDACTED:&lt;rule-id&gt;]</c> span marker so a reader
/// can tell a surgical redaction from a dropped value.
/// </summary>
internal static class OmissionReasons
{
    /// <summary>The leaf is larger than the versioned per-leaf byte budget.</summary>
    public const string LeafExceedsLimit = "leaf_exceeds_limit";

    /// <summary>A sensitive property name carried a non-string scalar; there is no span to map.</summary>
    public const string SensitiveFieldScalar = "sensitive_field_scalar";

    /// <summary>A sensitive property name carried an object or array; a subtree has no exact span.</summary>
    public const string SensitiveFieldSubtree = "sensitive_field_subtree";
}

/// <summary>
/// The bounded deterministic detector. It matches one decoded leaf value at a
/// time — never a serialized JSON document — resolves overlaps deterministically,
/// and replaces exact spans. Every dimension it spends is charged to a
/// <see cref="ScanBudgetState"/> that fails the whole scan closed when a budget
/// is exhausted.
/// </summary>
internal sealed class SecretScanner(SecretRuleSet ruleSet, SafetyBudgets budgets)
{
    private const string LiteralRuleId = "operator-literal";
    private const string MarkerPrefix = "[REDACTED:";

    // One bounded decoding level. Candidate shapes only; the decoded text is
    // never re-scanned for further candidates.
    private static readonly Regex PercentCandidates = new(
        "(?:%[0-9A-Fa-f]{2}){4,}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex HexCandidates = new(
        "[0-9A-Fa-f]{16,}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex Base64Candidates = new(
        "[A-Za-z0-9+/]{16,}={0,2}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    public SafetyBudgets Budgets => budgets;

    /// <summary>
    /// Scans one leaf value. <paramref name="propertyName"/> is the structured
    /// field name the value sits under, or null for free text.
    /// </summary>
    public LeafOutcome ScanLeaf(string value, string? propertyName, ScanBudgetState state)
    {
        state.CheckDeadline();
        if (Encoding.UTF8.GetByteCount(value) > budgets.MaxLeafBytes)
        {
            return LeafOutcome.Omitted(OmissionReasons.LeafExceedsLimit);
        }

        var matches = new List<SpanMatch>();
        CollectLiteralMatches(value, matches, state);
        CollectRuleMatches(value, matches, state);
        if (propertyName is not null && MatchesSensitiveField(propertyName) is { } fieldRule
            && value.Length > 0)
        {
            matches.Add(new SpanMatch(0, value.Length, fieldRule.Id, fieldRule.Category, fieldRule.Priority));
            state.ChargeMatch();
        }
        CollectDecodedMatches(value, matches, state);

        if (matches.Count == 0)
        {
            return LeafOutcome.Scanned(value, [], [], 0);
        }

        var accepted = ResolveOverlaps(matches);
        var ruleIds = new SortedSet<string>(StringComparer.Ordinal);
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        int length = value.Length;
        foreach (var span in accepted)
        {
            length += MarkerPrefix.Length + span.RuleId.Length + 1 - span.Length;
            ruleIds.Add(span.RuleId);
            categories.Add(span.Category);
        }

        // Written straight into the final string: a leaf may be tens of
        // megabytes, and a StringBuilder would hold a second full copy of it
        // alive while ToString() allocates the third.
        string redacted = string.Create(length, (value, accepted), static (destination, state) =>
        {
            var (source, spans) = state;
            int read = 0;
            int write = 0;
            foreach (var span in spans)
            {
                source.AsSpan(read, span.Start - read).CopyTo(destination[write..]);
                write += span.Start - read;
                MarkerPrefix.CopyTo(destination[write..]);
                write += MarkerPrefix.Length;
                span.RuleId.CopyTo(destination[write..]);
                write += span.RuleId.Length;
                destination[write++] = ']';
                read = span.Start + span.Length;
            }
            source.AsSpan(read).CopyTo(destination[write..]);
        });
        return LeafOutcome.Scanned(redacted, ruleIds, categories, accepted.Count);
    }

    private void CollectLiteralMatches(string value, List<SpanMatch> matches, ScanBudgetState state)
    {
        foreach (string literal in ruleSet.Literals)
        {
            int index = value.IndexOf(literal, StringComparison.Ordinal);
            while (index >= 0)
            {
                // Exact operator-known values outrank every structural rule.
                matches.Add(new SpanMatch(
                    index, literal.Length, LiteralRuleId,
                    SecretCategories.ConfiguredCredential, int.MaxValue));
                state.ChargeMatch();
                index = value.IndexOf(literal, index + literal.Length, StringComparison.Ordinal);
            }
        }
    }

    private void CollectRuleMatches(string value, List<SpanMatch> matches, ScanBudgetState state)
    {
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Matcher != SecretMatcherKind.Regex || !PrefilterHits(value, rule))
            {
                continue;
            }
            state.CheckDeadline();
            try
            {
                foreach (Match match in rule.Compiled.Matches(value))
                {
                    if (match.Length == 0)
                    {
                        continue;
                    }
                    matches.Add(new SpanMatch(
                        match.Index, match.Length, rule.Id, rule.Category, rule.Priority));
                    state.ChargeMatch();
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new SafetyScanException($"rule '{rule.Id}' exceeded its matcher timeout");
            }
        }
    }

    /// <summary>True when a structured property name is in the governed sensitive vocabulary.</summary>
    public bool IsSensitiveField(string propertyName) => MatchesSensitiveField(propertyName) is not null;

    private SecretRule? MatchesSensitiveField(string propertyName)
    {
        SecretRule? best = null;
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Matcher != SecretMatcherKind.SensitiveField
                || !PrefilterHits(propertyName, rule))
            {
                continue;
            }
            try
            {
                if (rule.Compiled.IsMatch(propertyName)
                    && (best is null || rule.Priority > best.Priority))
                {
                    best = rule;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new SafetyScanException($"rule '{rule.Id}' exceeded its matcher timeout");
            }
        }
        return best;
    }

    /// <summary>
    /// One bounded percent/hex/Base64 pass wrapped around the high-confidence
    /// rules only. A hit redacts the ORIGINAL encoded span, never the decoded
    /// text, so the mapping back into the source value is exact.
    /// </summary>
    private void CollectDecodedMatches(string value, List<SpanMatch> matches, ScanBudgetState state)
    {
        foreach (var (start, length, kind) in EnumerateCandidates(value))
        {
            if (length > budgets.MaxDecoderCandidateLength)
            {
                // Qualification bound, not a fail-closed budget: the run was
                // already scanned in full undecoded by every rule above.
                continue;
            }
            // Charged and deadline-checked per candidate: a leaf can hold tens
            // of thousands of decodable runs that no rule prefilter ever hits,
            // and that path must still be bounded in time.
            state.CheckDeadline();
            state.ChargeDecoderCandidate();
            string encoded = value.Substring(start, length);
            if (!TryDecode(encoded, kind, out string decoded))
            {
                continue;
            }
            state.ChargeDecodedBytes(Encoding.UTF8.GetByteCount(decoded));
            if (decoded.Length == 0 || !IsPrintableText(decoded))
            {
                continue;
            }

            foreach (var rule in ruleSet.Rules)
            {
                if (!rule.DecodeEligible || !PrefilterHits(decoded, rule))
                {
                    continue;
                }
                state.CheckDeadline();
                try
                {
                    if (rule.Compiled.IsMatch(decoded))
                    {
                        matches.Add(new SpanMatch(
                            start, length, rule.Id, rule.Category, rule.Priority));
                        state.ChargeMatch();
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new SafetyScanException($"rule '{rule.Id}' exceeded its matcher timeout");
                }
            }
        }
    }

    private IEnumerable<(int Start, int Length, DecoderKind Kind)> EnumerateCandidates(string value)
    {
        if (!ruleSet.Rules.Any(rule => rule.DecodeEligible))
        {
            yield break;
        }
        foreach (Match match in PercentCandidates.Matches(value))
        {
            yield return (match.Index, match.Length, DecoderKind.Percent);
        }
        foreach (Match match in HexCandidates.Matches(value))
        {
            yield return (match.Index, match.Length, DecoderKind.Hex);
        }
        foreach (Match match in Base64Candidates.Matches(value))
        {
            yield return (match.Index, match.Length, DecoderKind.Base64);
        }
    }

    private static bool TryDecode(string encoded, DecoderKind kind, out string decoded)
    {
        decoded = "";
        try
        {
            switch (kind)
            {
                case DecoderKind.Percent:
                    decoded = Uri.UnescapeDataString(encoded);
                    return true;
                case DecoderKind.Hex:
                    if (encoded.Length % 2 != 0)
                    {
                        encoded = encoded[..^1];
                    }
                    decoded = DecodeStrictUtf8(Convert.FromHexString(encoded));
                    return decoded.Length > 0;
                case DecoderKind.Base64:
                    string padded = encoded.TrimEnd('=');
                    int remainder = padded.Length % 4;
                    if (remainder == 1)
                    {
                        return false;
                    }
                    padded = remainder == 0 ? padded : padded + new string('=', 4 - remainder);
                    decoded = DecodeStrictUtf8(Convert.FromBase64String(padded));
                    return decoded.Length > 0;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return "";
        }
    }

    private static bool IsPrintableText(string text)
    {
        foreach (char character in text)
        {
            if (char.IsControl(character) && character is not ('\t' or '\r' or '\n'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool PrefilterHits(string text, SecretRule rule)
    {
        foreach (string prefilter in rule.Prefilters)
        {
            if (text.Contains(prefilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Deterministic overlap resolution, documented once here and in
    /// docs/capture-safety-budgets.md: highest priority wins; ties break by
    /// longest match, then by rule id ordinal, then by earliest start. A match
    /// overlapping an already-accepted one is discarded.
    /// </summary>
    private static List<SpanMatch> ResolveOverlaps(List<SpanMatch> matches)
    {
        matches.Sort(static (left, right) =>
        {
            int byPriority = right.Priority.CompareTo(left.Priority);
            if (byPriority != 0) { return byPriority; }
            int byLength = right.Length.CompareTo(left.Length);
            if (byLength != 0) { return byLength; }
            int byId = string.CompareOrdinal(left.RuleId, right.RuleId);
            return byId != 0 ? byId : left.Start.CompareTo(right.Start);
        });

        var accepted = new List<SpanMatch>();
        foreach (var candidate in matches)
        {
            bool overlaps = false;
            foreach (var already in accepted)
            {
                if (candidate.Start < already.Start + already.Length
                    && already.Start < candidate.Start + candidate.Length)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
            {
                accepted.Add(candidate);
            }
        }
        accepted.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return accepted;
    }

    private enum DecoderKind { Percent, Hex, Base64 }

    private readonly record struct SpanMatch(
        int Start, int Length, string RuleId, string Category, int Priority);
}

/// <summary>The result of scanning one leaf: either a sanitized value or an omission.</summary>
internal sealed record LeafOutcome(
    string? Value,
    string? OmissionReason,
    IReadOnlyCollection<string> RuleIds,
    IReadOnlyCollection<string> Categories,
    int RedactionCount)
{
    public bool IsOmitted => OmissionReason is not null;

    public static LeafOutcome Scanned(
        string value,
        IReadOnlyCollection<string> ruleIds,
        IReadOnlyCollection<string> categories,
        int redactionCount) => new(value, null, ruleIds, categories, redactionCount);

    public static LeafOutcome Omitted(string reason) => new(null, reason, [], [], 0);
}

/// <summary>
/// The per-scan budget ledger. Every charge is checked eagerly, so an
/// exhausted budget throws before any partially-scanned text can be returned.
/// </summary>
internal sealed class ScanBudgetState(SafetyBudgets budgets)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _matches;
    private int _decoderCandidates;
    private long _decodedBytes;

    public void CheckDeadline()
    {
        if (_clock.Elapsed > budgets.MaxScanTime)
        {
            throw new SafetyScanException(
                $"the total scan-time budget of {budgets.MaxScanTime.TotalMilliseconds:0}ms was exceeded");
        }
    }

    public void ChargeMatch()
    {
        if (++_matches > budgets.MaxMatches)
        {
            throw new SafetyScanException(
                $"the match-count budget of {budgets.MaxMatches} was exceeded");
        }
    }

    public void ChargeDecoderCandidate()
    {
        if (++_decoderCandidates > budgets.MaxDecoderCandidates)
        {
            throw new SafetyScanException(
                $"the decoder-candidate budget of {budgets.MaxDecoderCandidates} was exceeded");
        }
    }

    public void ChargeDecodedBytes(long bytes)
    {
        _decodedBytes += bytes;
        if (_decodedBytes > budgets.MaxDecodedBytes)
        {
            throw new SafetyScanException(
                $"the total-decoded-byte budget of {budgets.MaxDecodedBytes} was exceeded");
        }
    }
}
