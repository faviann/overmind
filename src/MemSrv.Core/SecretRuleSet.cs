using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MemSrv.Core;

internal enum SecretMatcherKind
{
    /// <summary>Regex applied to a decoded leaf value.</summary>
    Regex,

    /// <summary>Regex applied to a structured property name; matches the whole value.</summary>
    SensitiveField
}

/// <summary>
/// One governed rule. Compiled once at load; never recompiled per scan.
/// </summary>
internal sealed record SecretRule(
    string Id,
    string Category,
    int Priority,
    IReadOnlyList<string> Prefilters,
    SecretMatcherKind Matcher,
    Regex Compiled)
{
    /// <summary>
    /// Every rule that reads TEXT runs against decoded candidates. The one kind
    /// that cannot is <see cref="SecretMatcherKind.SensitiveField"/>: it reads a
    /// structured property NAME, and a decoded blob has no structure to take a
    /// name from. Gating this on <see cref="Category"/> instead was a live false
    /// negative — `sensitive-assignment` is an ordinary free-text
    /// <c>NAME=value</c> regex that merely carries the `structured_field`
    /// category, so a Base64'd credentials file, the exact shape
    /// docs/capture-safety-budgets.md says the decoder exists for, was stored
    /// unredacted unless it also happened to hold a provider-prefixed value.
    /// </summary>
    public bool DecodeEligible => Matcher != SecretMatcherKind.SensitiveField;
}

internal static class SecretCategories
{
    public const string PrivateKey = "private_key";
    public const string AuthHeader = "auth_header";
    public const string CredentialUrl = "credential_url";
    public const string ProviderToken = "provider_token";
    public const string StructuredField = "structured_field";
    public const string ConfiguredCredential = "configured_credential";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PrivateKey, AuthHeader, CredentialUrl, ProviderToken, StructuredField, ConfiguredCredential
    };
}

/// <summary>
/// A validated, versioned, compiled rule set plus the operator-provisioned
/// exact credential values. Construction is all-or-nothing: <see cref="Load"/>
/// either returns a usable set or a safe failure reason that never contains a
/// candidate value or an operator literal.
/// </summary>
internal sealed class SecretRuleSet
{
    /// <summary>Shortest operator literal accepted; shorter values would redact ordinary prose.</summary>
    public const int MinimumLiteralLength = 8;

    public required IReadOnlyList<SecretRule> Rules { get; init; }

    /// <summary>Exact operator-provisioned values. Never logged, never versioned reversibly.</summary>
    public required IReadOnlyList<string> Literals { get; init; }

    public required string Version { get; init; }

    public static bool TryLoad(
        string rulesPath,
        string? literalsPath,
        TimeSpan matchTimeout,
        out SecretRuleSet? ruleSet,
        out string? failureReason)
    {
        ruleSet = null;
        failureReason = null;

        if (string.IsNullOrWhiteSpace(rulesPath))
        {
            failureReason = "no rule configuration path is configured";
            return false;
        }
        if (!File.Exists(rulesPath))
        {
            failureReason = "the rule configuration file is missing";
            return false;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(rulesPath);
        }
        catch (IOException)
        {
            failureReason = "the rule configuration file could not be read";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureReason = "the rule configuration file could not be read";
            return false;
        }

        if (string.IsNullOrWhiteSpace(contents))
        {
            failureReason = "the rule configuration file is empty";
            return false;
        }

        RuleSetFile? file;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            file = deserializer.Deserialize<RuleSetFile>(contents);
        }
        catch (YamlException)
        {
            failureReason = "the rule configuration file is not valid YAML";
            return false;
        }

        if (file is null)
        {
            failureReason = "the rule configuration file is empty";
            return false;
        }
        if (string.IsNullOrWhiteSpace(file.Version))
        {
            failureReason = "the rule set declares no version";
            return false;
        }
        if (file.Rules.Count == 0)
        {
            failureReason = "the rule set contains no rules";
            return false;
        }

        var rules = new List<SecretRule>(file.Rules.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.Rules)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                failureReason = "a rule has a blank id";
                return false;
            }
            if (!seen.Add(entry.Id))
            {
                failureReason = $"rule id '{entry.Id}' is duplicated";
                return false;
            }
            if (!SecretCategories.All.Contains(entry.Category ?? ""))
            {
                failureReason = $"rule '{entry.Id}' has unknown category '{entry.Category}'";
                return false;
            }
            if (entry.Priority is null)
            {
                failureReason = $"rule '{entry.Id}' has no priority";
                return false;
            }

            var prefilters = (entry.Prefilter ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            if (prefilters.Length == 0)
            {
                failureReason = $"rule '{entry.Id}' has no prefilter";
                return false;
            }

            SecretMatcherKind matcher;
            switch (entry.Matcher)
            {
                case "regex":
                    matcher = SecretMatcherKind.Regex;
                    break;
                case "sensitive_field":
                    matcher = SecretMatcherKind.SensitiveField;
                    break;
                default:
                    failureReason =
                        $"rule '{entry.Id}' uses unsupported matcher '{entry.Matcher}'";
                    return false;
            }

            if (string.IsNullOrEmpty(entry.Pattern))
            {
                failureReason = $"rule '{entry.Id}' has an empty pattern";
                return false;
            }

            Regex compiled;
            try
            {
                // NonBacktracking guarantees linear time over untrusted input.
                // A pattern it cannot express is rejected here rather than
                // silently downgraded to the backtracking engine.
                compiled = new Regex(
                    entry.Pattern,
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    matchTimeout);
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException)
            {
                // NonBacktracking rejects unsupported constructs (lookaround,
                // backreferences) and patterns whose automaton exceeds its node
                // limit. Both are configuration errors: reject at load rather
                // than silently downgrading to the backtracking engine.
                failureReason =
                    $"rule '{entry.Id}' has a pattern the non-backtracking matcher cannot compile";
                return false;
            }

            rules.Add(new SecretRule(
                entry.Id, entry.Category!, entry.Priority.Value, prefilters, matcher, compiled));
        }

        if (!TryLoadLiterals(literalsPath, out var literals, out failureReason))
        {
            return false;
        }

        ruleSet = new SecretRuleSet
        {
            Rules = rules,
            Literals = literals,
            // The version fingerprints the tracked rule file plus the COUNT of
            // operator literals. It never mixes literal bytes into a digest:
            // an unkeyed digest of a low-entropy configured credential is
            // itself a disclosure (research note, constraint 2).
            Version = $"{file.Version}+sha256:" +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)))
                    .ToLowerInvariant()[..16] +
                (literals.Count > 0 ? $"+literals:{literals.Count}" : "")
        };
        return true;
    }

    private static bool TryLoadLiterals(
        string? literalsPath, out IReadOnlyList<string> literals, out string? failureReason)
    {
        failureReason = null;
        literals = [];
        // An absent or empty operator literals file is valid: an installation
        // may have no known credential values. Only the RULE file must be
        // nonempty.
        if (string.IsNullOrWhiteSpace(literalsPath) || !File.Exists(literalsPath))
        {
            return true;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(literalsPath);
        }
        catch (IOException)
        {
            failureReason = "the operator literal file could not be read";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureReason = "the operator literal file could not be read";
            return false;
        }

        var values = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (line.Length < MinimumLiteralLength)
            {
                // The value itself never enters the reason.
                failureReason =
                    $"the operator literal on line {index + 1} is shorter than " +
                    $"{MinimumLiteralLength} characters";
                return false;
            }
            values.Add(line);
        }

        // Longest first, so a literal that contains another still redacts wholly.
        literals = values
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private sealed class RuleSetFile
    {
        public string Version { get; set; } = "";
        public List<RuleEntry> Rules { get; set; } = [];
    }

    private sealed class RuleEntry
    {
        public string? Id { get; set; }
        public string? Category { get; set; }
        public int? Priority { get; set; }
        public string? Prefilter { get; set; }
        public string? Matcher { get; set; }
        public string? Pattern { get; set; }
    }
}
