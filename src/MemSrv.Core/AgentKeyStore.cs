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
        _byKey = keys.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
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
        var entries = (file.Keys ?? [])
            .Select(entry => new AgentKey(
                entry.Key ?? "",
                entry.AgentId ?? "",
                entry.DefaultNamespace ?? "",
                entry.AllowedNamespaces ?? []))
            .Where(entry => entry.Key.Length > 0);

        return new AgentKeyStore(entries);
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
