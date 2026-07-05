using System.Text.Json;
using System.Text.RegularExpressions;
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

    public NeverStoreGate(string path)
    {
        _rules = File.Exists(path) ? Load(path) : [];
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
    {
        var redacted = text;
        foreach (var rule in _rules)
        {
            redacted = rule.Regex.Replace(redacted, $"[REDACTED:{rule.Name}]");
        }

        return redacted;
    }

    public string RedactObject(object value) =>
        Redact(JsonSerializer.Serialize(value));

    private static IReadOnlyList<Rule> Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<NeverStoreConfig>(File.ReadAllText(path)) ?? new NeverStoreConfig();
        return config.Rules
            .Select(rule => new Rule(rule.Name, new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant)))
            .ToArray();
    }

    private sealed record Rule(string Name, Regex Regex);
    private sealed class NeverStoreConfig
    {
        public List<NeverStoreRule> Rules { get; set; } = [];
    }

    private sealed class NeverStoreRule
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
    }
}
