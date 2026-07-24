using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MemSrv.Core;

public sealed class NeverStoreException(string ruleName) : Exception($"Write rejected by never-store rule '{ruleName}'.")
{
    public string RuleName { get; } = ruleName;
}

public sealed class NeverStoreGate
{
    private readonly IReadOnlyList<Rule> _rules;
    private readonly string _ruleSetVersion;

    public bool IsConfigured => _rules.Count > 0;
    public string RuleSetVersion => _ruleSetVersion;

    public NeverStoreGate(string path)
    {
        string contents = File.Exists(path) ? File.ReadAllText(path) : "";
        _rules = string.IsNullOrEmpty(contents) ? [] : Load(contents);
        _ruleSetVersion = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
    }

    public void AssertAllowed(string text)
    {
        foreach (var rule in _rules)
        {
            if (rule.Regex.IsMatch(text))
            {
                throw new NeverStoreException(rule.Name);
            }
        }
    }

    public void AssertAllowedObject(object value) =>
        AssertAllowed(JsonSerializer.Serialize(value));

    public string Redact(string text)
        => Scan(text).Redacted;

    public NeverStoreScan Scan(string text)
    {
        var redacted = text;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var rule in _rules)
        {
            redacted = rule.Regex.Replace(redacted, match =>
            {
                ids.Add(rule.Name);
                categories.Add(rule.Category);
                count++;
                return $"[REDACTED:{rule.Name}]";
            });
        }

        return new NeverStoreScan(
            redacted,
            ids.Order(StringComparer.Ordinal).ToArray(),
            categories.Order(StringComparer.Ordinal).ToArray(),
            count);
    }

    public string RedactObject(object value) =>
        Redact(JsonSerializer.Serialize(value));

    private static IReadOnlyList<Rule> Load(string contents)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<NeverStoreConfig>(contents) ?? new NeverStoreConfig();
        return config.Rules
            .Select(rule => new Rule(
                rule.Name,
                rule.Category,
                new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant)))
            .ToArray();
    }

    private sealed record Rule(string Name, string Category, Regex Regex);
    private sealed class NeverStoreConfig
    {
        public List<NeverStoreRule> Rules { get; set; } = [];
    }

    private sealed class NeverStoreRule
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "secret";
        public string Pattern { get; set; } = "";
    }
}

public sealed record NeverStoreScan(
    string Redacted,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> Categories,
    int RedactionCount);
