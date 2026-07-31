using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemSrv.Core;

/// <summary>
/// The whole marker vocabulary the sanitizer can write, in one home. Two
/// deliberately distinct forms so a reader can tell a surgical span redaction
/// from a dropped value, and so no caller has to reconstruct either shape by
/// hand. Documented in docs/capture-safety-budgets.md; the redaction form is
/// fixed by memory-server-phase1-spec §5.
/// </summary>
internal static class SafetyMarkers
{
    /// <summary>
    /// An exact span was replaced by the rule that claimed it. Written from the
    /// parts rather than through a helper, because span redaction composes the
    /// whole sanitized leaf in one pass with <c>string.Create</c> and must not
    /// allocate a marker string per match.
    /// </summary>
    public const string RedactionPrefix = "[REDACTED:";

    public const string OmissionPrefix = "[OMITTED:";
    public const char Suffix = ']';

    /// <summary>The whole value was dropped because no exact span could be mapped.</summary>
    public static string Omission(string reason) => OmissionPrefix + reason + Suffix;
}

/// <summary>
/// Why an entire value was dropped instead of span-redacted. This vocabulary is
/// closed and documented in docs/capture-safety-budgets.md; the marker written
/// into the sanitized value is <see cref="SafetyMarkers.Omission"/>.
/// </summary>
internal static class OmissionReasons
{
    /// <summary>The leaf is larger than the versioned per-leaf byte budget.</summary>
    public const string LeafExceedsLimit = "leaf_exceeds_limit";

    /// <summary>A sensitive property name carried a non-string scalar; there is no span to map.</summary>
    public const string SensitiveFieldScalar = "sensitive_field_scalar";

    /// <summary>A sensitive property name carried an object or array; a subtree has no exact span.</summary>
    public const string SensitiveFieldSubtree = "sensitive_field_subtree";

    /// <summary>
    /// Two sibling property names became the same text after redaction. Writing
    /// both would emit a duplicate JSON key and silently lose one value on
    /// re-parse, so the whole object is dropped instead.
    /// </summary>
    public const string RedactedNameCollision = "redacted_name_collision";
}

/// <summary>
/// The single match a refusal names: the highest-priority accepted span in a
/// scan, resolved with the same deterministic order the overlap sweep uses.
/// </summary>
internal readonly record struct PrimaryMatch(string RuleId, int Priority, int Length)
{
    public Ranked Rank => new(Priority, Length, RuleId);

    public static PrimaryMatch? Best(PrimaryMatch? left, PrimaryMatch? right)
    {
        if (left is null) { return right; }
        if (right is null) { return left; }
        return MatchRanking.Wins(left.Value.Rank, right.Value.Rank)
            ? left.Value
            : right.Value;
    }
}

/// <summary>
/// One competitor in the ranking, carried as a single value. The comparison
/// takes two of these rather than two shredded (priority, length, id) triples,
/// because adjacent same-typed triples can be transposed silently and still
/// type-check — which is the hazard extracting <see cref="MatchRanking"/> was
/// meant to remove.
/// </summary>
internal readonly record struct Ranked(int Priority, int Length, string RuleId);

/// <summary>
/// The one deterministic ranking this detector uses to pick a winner among
/// competing matches: highest priority, then longest match, then lowest rule id
/// ordinal. Both the overlap sweep (over original match lengths) and the
/// refusal's primary match (over merged span lengths) rank the same way, and
/// they must not be able to drift apart.
/// </summary>
internal static class MatchRanking
{
    public static bool Wins(Ranked left, Ranked right)
    {
        if (left.Priority != right.Priority) { return left.Priority > right.Priority; }
        if (left.Length != right.Length) { return left.Length > right.Length; }
        return string.CompareOrdinal(left.RuleId, right.RuleId) <= 0;
    }
}

/// <summary>
/// The bounded deterministic detector. It matches one decoded leaf value at a
/// time — never a serialized JSON document — resolves overlaps deterministically,
/// and replaces exact spans. Every dimension it spends is charged to a
/// <see cref="ScanBudgetState"/> that fails the whole scan closed when a budget
/// is exhausted.
/// </summary>
internal interface ISafetyScanner
{
    LeafOutcome ScanLeaf(string value, string? propertyName, ScanBudgetState state);
    bool IsSensitiveField(string propertyName, ScanBudgetState state);
}

internal sealed class SecretScanner : ISafetyScanner
{
    private const string LiteralRuleId = "operator-literal";

    private readonly SecretRuleSet _ruleSet;
    private readonly SafetyBudgets _budgets;

    // One bounded decoding level. Candidate shapes only; the decoded text is
    // never re-scanned for further candidates. These carry the same per-rule
    // matcher timeout the governed rules get: research constraint 5 keeps a
    // timeout as defense in depth even under NonBacktracking, and these three
    // run over exactly the same untrusted leaf.
    private readonly Regex _percentCandidates;
    private readonly Regex _hexCandidates;
    private readonly Regex _base64Candidates;

    public SecretScanner(SecretRuleSet ruleSet, SafetyBudgets budgets)
    {
        _ruleSet = ruleSet;
        _budgets = budgets;
        const RegexOptions options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        _percentCandidates = new Regex("(?:%[0-9A-Fa-f]{2}){4,}", options, budgets.MaxRuleTime);
        _hexCandidates = new Regex("[0-9A-Fa-f]{16,}", options, budgets.MaxRuleTime);
        // Both Base64 alphabets: standard (`+/`) and base64url (`-_`), which is
        // what JWTs and most modern tokens use.
        _base64Candidates = new Regex("[A-Za-z0-9+/_-]{16,}={0,2}", options, budgets.MaxRuleTime);
    }

    /// <summary>
    /// Scans one leaf value. <paramref name="propertyName"/> is the structured
    /// field name the value sits under, or null for free text.
    /// </summary>
    public LeafOutcome ScanLeaf(string value, string? propertyName, ScanBudgetState state)
    {
        state.CheckDeadline();
        if (Encoding.UTF8.GetByteCount(value) > _budgets.MaxLeafBytes)
        {
            return LeafOutcome.Omitted(OmissionReasons.LeafExceedsLimit);
        }

        var matches = new List<SpanMatch>();
        CollectLiteralMatches(value, matches, state);
        CollectRuleMatches(value, matches, state);
        if (propertyName is not null && MatchesSensitiveField(propertyName, state) is { } fieldRule
            && value.Length > 0)
        {
            matches.Add(new SpanMatch(0, value.Length, fieldRule.Id, fieldRule.Category, fieldRule.Priority));
            state.ChargeMatch();
        }
        CollectDecodedMatches(value, matches, state);

        if (matches.Count == 0)
        {
            return LeafOutcome.Scanned(value, [], [], 0, null);
        }

        var accepted = ResolveOverlaps(matches);
        var ruleIds = new SortedSet<string>(StringComparer.Ordinal);
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        PrimaryMatch? primary = null;
        int length = value.Length;
        foreach (var span in accepted)
        {
            length += SafetyMarkers.RedactionPrefix.Length + span.RuleId.Length + 1 - span.Length;
            ruleIds.Add(span.RuleId);
            categories.Add(span.Category);
            primary = PrimaryMatch.Best(
                primary, new PrimaryMatch(span.RuleId, span.Priority, span.Length));
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
                SafetyMarkers.RedactionPrefix.CopyTo(destination[write..]);
                write += SafetyMarkers.RedactionPrefix.Length;
                span.RuleId.CopyTo(destination[write..]);
                write += span.RuleId.Length;
                destination[write++] = SafetyMarkers.Suffix;
                read = span.Start + span.Length;
            }
            source.AsSpan(read).CopyTo(destination[write..]);
        });
        return LeafOutcome.Scanned(redacted, ruleIds, categories, accepted.Count, primary);
    }

    private void CollectLiteralMatches(string value, List<SpanMatch> matches, ScanBudgetState state)
    {
        foreach (string literal in _ruleSet.Literals)
        {
            // The literal sweep is one of the most expensive phases over a
            // limit-sized leaf (docs/capture-safety-budgets.md), so the scan
            // deadline is checked here too — once per literal, which is the
            // granularity that bounds the phase without distorting it.
            state.CheckDeadline();
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
        foreach (var rule in _ruleSet.Rules)
        {
            if (rule.Matcher != SecretMatcherKind.Regex || !PrefilterHits(value, rule))
            {
                continue;
            }
            state.CheckDeadline();
            foreach (Match match in TimedMatches(rule.Compiled, value, $"rule '{rule.Id}'"))
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
    }

    /// <summary>True when a structured property name is in the governed sensitive vocabulary.</summary>
    public bool IsSensitiveField(string propertyName, ScanBudgetState state) =>
        MatchesSensitiveField(propertyName, state) is not null;

    private SecretRule? MatchesSensitiveField(string propertyName, ScanBudgetState state)
    {
        SecretRule? best = null;
        foreach (var rule in _ruleSet.Rules)
        {
            if (rule.Matcher != SecretMatcherKind.SensitiveField
                || !PrefilterHits(propertyName, rule))
            {
                continue;
            }
            state.CheckDeadline();
            if (IsMatch(rule, propertyName) && (best is null || rule.Priority > best.Priority))
            {
                best = rule;
            }
        }
        return best;
    }

    /// <summary>
    /// One bounded percent/hex/Base64 pass wrapped around the high-confidence
    /// rules and the operator-provisioned exact values. A hit redacts the
    /// ORIGINAL encoded span, never the decoded text, so the mapping back into
    /// the source value is exact.
    /// </summary>
    private void CollectDecodedMatches(string value, List<SpanMatch> matches, ScanBudgetState state)
    {
        foreach (var (start, length, kind) in EnumerateCandidates(value))
        {
            // Deadline-checked per candidate, BEFORE the over-length skip: a
            // leaf can hold tens of thousands of decodable runs that no rule
            // prefilter ever hits, and a flood of over-length ones must not
            // spin unchecked either.
            state.CheckDeadline();
            if (length > _budgets.MaxDecoderCandidateLength)
            {
                // Not decoded, and NOT fail-closed: an accepted, bounded
                // residual risk documented in docs/capture-safety-budgets.md.
                // The undecoded bytes were still crossed by every rule, but a
                // secret encoded inside a run this long is not detected.
                continue;
            }
            state.ChargeDecoderCandidate();
            string encoded = value.Substring(start, length);
            // At most two decodings per candidate — the two byte alignments an
            // odd-length hex run allows — and never a second decoding LEVEL.
            // Each attempt charges the decoded-byte budget.
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string decoded in Decode(encoded, kind))
            {
                state.ChargeDecodedBytes(Encoding.UTF8.GetByteCount(decoded));
                foreach (var run in PrintableRuns(decoded))
                {
                    ScanDecodedRun(decoded, run, start, length, claimed, matches, state);
                }
            }
        }
    }

    /// <summary>
    /// Scans one printable run of a decoded candidate. <paramref name="claimed"/>
    /// carries the rule ids that already took a span for this candidate, so a
    /// credential visible in two runs or under both hex alignments produces one
    /// span, not several identical ones that would merge anyway while charging
    /// the match budget twice.
    /// </summary>
    private void ScanDecodedRun(
        string decoded,
        Range run,
        int start,
        int length,
        HashSet<string> claimed,
        List<SpanMatch> matches,
        ScanBudgetState state)
    {
        // Once per RUN, not once per candidate: control-byte-dense decoded text
        // splits into many short runs, and the per-rule loop below only reaches
        // its own deadline check when a prefilter actually hits. Without this,
        // a candidate that decodes to alternating control and printable bytes
        // would spin through the rule list millions of times unchecked.
        state.CheckDeadline();
        var text = decoded.AsSpan()[run];

        // Exact operator-known values are the highest-confidence rule there is,
        // so they sweep the decoded text in the same loop, charge the same
        // budgets, and attribute to the same original encoded span.
        if (!claimed.Contains(LiteralRuleId))
        {
            foreach (string literal in _ruleSet.Literals)
            {
                state.CheckDeadline();
                if (text.Contains(literal, StringComparison.Ordinal))
                {
                    matches.Add(new SpanMatch(
                        start, length, LiteralRuleId,
                        SecretCategories.ConfiguredCredential, int.MaxValue));
                    state.ChargeMatch();
                    claimed.Add(LiteralRuleId);
                    break;
                }
            }
        }

        string? materialized = null;
        foreach (var rule in _ruleSet.Rules)
        {
            if (!rule.DecodeEligible
                || claimed.Contains(rule.Id)
                || !PrefilterHits(text, rule))
            {
                continue;
            }
            state.CheckDeadline();
            // Only paid for once, and only when some prefilter actually hit.
            materialized ??= decoded[run];
            if (IsMatch(rule, materialized))
            {
                matches.Add(new SpanMatch(
                    start, length, rule.Id, rule.Category, rule.Priority));
                state.ChargeMatch();
                claimed.Add(rule.Id);
            }
        }
    }

    private IEnumerable<(int Start, int Length, DecoderKind Kind)> EnumerateCandidates(string value)
    {
        if (_ruleSet.Literals.Count == 0 && !_ruleSet.Rules.Any(rule => rule.DecodeEligible))
        {
            yield break;
        }
        const string subject = "the decoder candidate scan";
        foreach (Match match in TimedMatches(_percentCandidates, value, subject))
        {
            yield return (match.Index, match.Length, DecoderKind.Percent);
        }
        foreach (Match match in TimedMatches(_hexCandidates, value, subject))
        {
            yield return (match.Index, match.Length, DecoderKind.Hex);
        }
        foreach (Match match in TimedMatches(_base64Candidates, value, subject))
        {
            yield return (match.Index, match.Length, DecoderKind.Base64);
        }
    }

    /// <summary>
    /// Enumerates matches lazily — so a per-candidate budget can stop a flood
    /// before the whole collection is materialized — while converting a matcher
    /// timeout into the fail-closed scan error. Every enumerating matcher in
    /// this scanner goes through here; the only other shape is
    /// <see cref="IsMatch"/>, which is a bare boolean test with no match object
    /// to allocate.
    /// </summary>
    private static IEnumerable<Match> TimedMatches(Regex regex, string value, string subject)
    {
        var matches = regex.Matches(value).GetEnumerator();
        while (true)
        {
            try
            {
                if (!matches.MoveNext())
                {
                    yield break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw MatcherTimedOut(subject);
            }
            yield return (Match)matches.Current;
        }
    }

    private static bool IsMatch(SecretRule rule, string text)
    {
        try
        {
            return rule.Compiled.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            throw MatcherTimedOut($"rule '{rule.Id}'");
        }
    }

    private static SafetyScanException MatcherTimedOut(string subject) =>
        new($"{subject} exceeded its matcher timeout");

    /// <summary>
    /// The decodings of one candidate: one for percent and Base64, and for an
    /// odd-length hex run BOTH byte alignments, because only one of the two can
    /// be the real encoding and there is no way to tell which from the run
    /// alone. Still exactly one decoding level — a decoding is never re-fed to
    /// this method.
    /// </summary>
    private static IEnumerable<string> Decode(string encoded, DecoderKind kind)
    {
        if (kind == DecoderKind.Hex && encoded.Length % 2 != 0)
        {
            if (TryDecode(encoded[..^1], kind, out string trailingDropped))
            {
                yield return trailingDropped;
            }
            if (TryDecode(encoded[1..], kind, out string leadingDropped))
            {
                yield return leadingDropped;
            }
            yield break;
        }
        if (TryDecode(encoded, kind, out string decoded))
        {
            yield return decoded;
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
                    return decoded.Length > 0;
                case DecoderKind.Hex:
                    decoded = DecodeStrictUtf8(Convert.FromHexString(encoded));
                    return decoded.Length > 0;
                case DecoderKind.Base64:
                    // base64url ('-' and '_') folds onto the standard alphabet
                    // before decoding; JWTs and most modern tokens use it.
                    string padded = encoded.TrimEnd('=').Replace('-', '+').Replace('_', '/');
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

    /// <summary>
    /// Splits decoded text into its maximal printable runs. A decoded blob
    /// routinely carries binary framing — length prefixes, NUL padding, a
    /// container header — around perfectly ordinary plaintext, and discarding
    /// the whole candidate because it held ANY control character made every such
    /// blob invisible. This is a bounded deterministic split, not a heuristic:
    /// no scoring, no recursion, no second decoding level. A credential that
    /// straddles a control byte is not detected, which is the same accepted
    /// residual risk line-wrapped Base64 already sits behind.
    /// </summary>
    private static IEnumerable<Range> PrintableRuns(string decoded)
    {
        int runStart = 0;
        for (int index = 0; index <= decoded.Length; index++)
        {
            bool boundary = index == decoded.Length
                || (char.IsControl(decoded[index]) && decoded[index] is not ('\t' or '\r' or '\n'));
            if (!boundary)
            {
                continue;
            }
            if (index > runStart)
            {
                yield return runStart..index;
            }
            runStart = index + 1;
        }
    }

    private static bool PrefilterHits(ReadOnlySpan<char> text, SecretRule rule)
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
    /// docs/capture-safety-budgets.md. Overlapping matches MERGE into one union
    /// span attributed to the highest-priority rule among them; ties break by
    /// longest original match, then by rule id ordinal. Merging, not discarding,
    /// is what guarantees no byte covered by ANY match survives unredacted: a
    /// short high-priority literal sitting inside a long private-key block used
    /// to win and take the block's remaining bytes out of the redaction with it.
    /// </summary>
    private static List<SpanMatch> ResolveOverlaps(List<SpanMatch> matches)
    {
        matches.Sort(static (left, right) =>
        {
            int byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : right.Length.CompareTo(left.Length);
        });

        var accepted = new List<SpanMatch>();
        int index = 0;
        while (index < matches.Count)
        {
            var winner = matches[index];
            int start = winner.Start;
            int end = winner.Start + winner.Length;
            int next = index + 1;
            // Transitively connected matches form one span: a chain of
            // overlaps is one contiguous region of secret-bearing bytes.
            while (next < matches.Count && matches[next].Start < end)
            {
                end = Math.Max(end, matches[next].Start + matches[next].Length);
                winner = Outrank(winner, matches[next]);
                next++;
            }
            accepted.Add(winner with { Start = start, Length = end - start });
            index = next;
        }
        return accepted;
    }

    private static SpanMatch Outrank(SpanMatch left, SpanMatch right) =>
        MatchRanking.Wins(left.Rank, right.Rank) ? left : right;

    private enum DecoderKind { Percent, Hex, Base64 }

    private readonly record struct SpanMatch(
        int Start, int Length, string RuleId, string Category, int Priority)
    {
        public Ranked Rank => new(Priority, Length, RuleId);
    }
}

/// <summary>The result of scanning one leaf: either a sanitized value or an omission.</summary>
internal sealed record LeafOutcome(
    string? Value,
    string? OmissionReason,
    IReadOnlyCollection<string> RuleIds,
    IReadOnlyCollection<string> Categories,
    int RedactionCount,
    PrimaryMatch? Primary)
{
    public bool IsOmitted => OmissionReason is not null;

    public static LeafOutcome Scanned(
        string value,
        IReadOnlyCollection<string> ruleIds,
        IReadOnlyCollection<string> categories,
        int redactionCount,
        PrimaryMatch? primary) => new(value, null, ruleIds, categories, redactionCount, primary);

    public static LeafOutcome Omitted(string reason) => new(null, reason, [], [], 0, null);
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
