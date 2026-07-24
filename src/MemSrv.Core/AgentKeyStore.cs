using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MemSrv.Core;

/// <summary>
/// One Ansible-provisioned bearer-key entry: the plaintext key and the agent
/// identity, default namespace, and namespace allowlist it resolves to. Secrets
/// are owned by provisioning (vault); rotation is a redeploy, so there is no key
/// CRUD in the app.
/// </summary>
public sealed record AgentKey(
    string Key,
    string AgentId,
    string DefaultNamespace,
    IReadOnlyList<string> AllowedNamespaces);

/// <summary>
/// Resolves a presented bearer key to its <see cref="AgentKey"/> entry. Loaded
/// once from the YAML key file mounted by provisioning; an unknown or missing
/// key resolves to nothing so the HTTP layer can reject it with 401 before any
/// tool runs.
/// </summary>
public sealed class AgentKeyStore
{
    private readonly IReadOnlyDictionary<string, AgentKey> _byKey;

    public AgentKeyStore(IEnumerable<AgentKey> keys)
    {
        var entries = keys.ToArray();
        foreach (var entry in entries)
        {
            string? invalid = ValidationError(
                entry.Key, entry.AgentId, entry.DefaultNamespace, entry.AllowedNamespaces);
            if (invalid is not null)
            {
                throw new InvalidOperationException($"Agent key is invalid: {invalid}.");
            }
        }
        _byKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
    }

    public int Count => _byKey.Count;

    public bool TryResolve(string presentedKey, out AgentKey agentKey) =>
        _byKey.TryGetValue(presentedKey, out agentKey!);

    public static AgentKeyStore Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Agent key file not found at '{path}'.", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var file = deserializer.Deserialize<KeyFile>(File.ReadAllText(path)) ?? new KeyFile();
        var keys = file.Keys ?? [];

        // Fail closed on a malformed provisioning file: a bearer key is a
        // credential, so a blank key, agent id, default namespace, or allowlist
        // is a provisioning error that must stop the server rather than silently
        // drop the entry (which hides the typo) or accept an identity-less key
        // (which authenticates into namespace ""). Reject loudly at load, naming
        // the offending entry so the operator can fix the file.
        var entries = new List<AgentKey>(keys.Count);
        for (int index = 0; index < keys.Count; index++)
        {
            KeyEntry entry = keys[index];
            string key = entry.Key ?? "";
            string agentId = entry.AgentId ?? "";
            string defaultNamespace = entry.DefaultNamespace ?? "";
            IReadOnlyList<string> allowed = entry.AllowedNamespaces ?? [];

            string? invalid = ValidationError(key, agentId, defaultNamespace, allowed);
            if (invalid is not null)
            {
                throw new InvalidOperationException(
                    $"Agent key file '{path}' entry #{index + 1} is invalid: {invalid}.");
            }

            entries.Add(new AgentKey(key, agentId, defaultNamespace, allowed));
        }

        return new AgentKeyStore(entries);
    }

    /// <summary>
    /// Returns a human-readable reason the entry is unusable as a credential, or
    /// <c>null</c> if it is complete. Shared by the loader (which throws) and the
    /// authentication boundary (which rejects with 401) so both fail closed on
    /// the same definition of "valid".
    /// </summary>
    public static string? ValidationError(
        string key, string agentId, string defaultNamespace, IReadOnlyList<string> allowedNamespaces)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "key is blank";
        }
        if (CaptureCredential.IsCaptureForm(key))
        {
            return "mcap_ credential format is reserved for capture credentials";
        }
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "agent_id is blank";
        }
        if (string.IsNullOrWhiteSpace(defaultNamespace))
        {
            return "default_namespace is blank";
        }
        if (allowedNamespaces.Count == 0)
        {
            return "allowed_namespaces is empty";
        }
        if (allowedNamespaces.Any(string.IsNullOrWhiteSpace))
        {
            return "allowed_namespaces contains a blank entry";
        }
        if (!allowedNamespaces.Contains(defaultNamespace, StringComparer.Ordinal))
        {
            // Unqualified calls land in the default namespace without an allowlist
            // check, so a default outside the allowlist is almost always a
            // provisioning typo — reject it rather than silently make the default
            // reachable while every qualified call to it would look foreign.
            return $"default_namespace '{defaultNamespace}' is not in allowed_namespaces";
        }
        return null;
    }

    private sealed class KeyFile
    {
        public List<KeyEntry>? Keys { get; set; }
    }

    private sealed class KeyEntry
    {
        public string? Key { get; set; }
        public string? AgentId { get; set; }
        public string? DefaultNamespace { get; set; }
        public List<string>? AllowedNamespaces { get; set; }
    }
}

public static class CaptureCredential
{
    private const string Prefix = "mcap_";
    private const int MinimumMaterialLength = 32;

    public static bool IsCaptureForm(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length < Prefix.Length + MinimumMaterialLength)
        {
            return false;
        }

        return value.AsSpan(Prefix.Length).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-") < 0;
    }

    public static void RequireCaptureForm(string value)
    {
        if (!IsCaptureForm(value))
        {
            throw new ArgumentException(
                "Capture credential must use mcap_ followed by at least 32 URL-safe random characters.");
        }
    }
}
