using System.Text;
using System.Text.Json;

namespace MemSrv.Core;

public sealed class NeverStoreException(string ruleName) : Exception($"Write rejected by never-store rule '{ruleName}'.")
{
    public string RuleName { get; } = ruleName;
}

/// <summary>
/// The single governed policy point every write path crosses: memory writes,
/// trace writes, capture enrollment, capture ingestion, and the disabled
/// capture runtime. Callers pass a value and get either a sanitized value or a
/// refusal; they never see rules, budgets, decoders, or spans.
///
/// Construction never throws, because a server whose rule file is broken must
/// still start, still reject an unknown credential first, and still refuse
/// every write with a reason. A gate whose configuration is missing, empty,
/// invalid, duplicated, unsupported, or un-loadable reports
/// <see cref="IsConfigured"/> <c>== false</c> plus a safe
/// <see cref="FailureReason"/>, and throws <see cref="SafetyConfigurationException"/>
/// from every scan, assert, and redact call.
/// </summary>
public sealed class NeverStoreGate
{
    private readonly string _rulesPath;
    private readonly string? _literalsPath;
    private readonly SafetyBudgets _budgets;
    // Atomic reload: a failed reload leaves the previously loaded state in
    // force, and a successful one swaps the whole state in a single reference
    // assignment that a concurrent scan either sees entirely or not at all.
    private volatile State _state;

    public NeverStoreGate(string rulesPath, string? literalsPath = null, SafetyBudgets? budgets = null)
    {
        _rulesPath = rulesPath;
        _literalsPath = literalsPath;
        _budgets = budgets ?? SafetyBudgets.Default;
        _state = Load(_rulesPath, _literalsPath, _budgets);
    }

    public bool IsConfigured => _state.RuleSet is not null;

    /// <summary>Safe reason the gate is unusable, or null. Never contains a candidate value.</summary>
    public string? FailureReason => _state.FailureReason;

    public SafetyBudgets Budgets => _budgets;

    public string RuleSetVersion => _state.RuleSet?.Version ?? "";

    /// <summary>
    /// Re-reads the rule and literal files. Returns false and keeps the
    /// previously loaded rule set in force when the new configuration is
    /// invalid; the reason is safe to log.
    /// </summary>
    public bool TryReload(out string? failureReason)
    {
        var next = Load(_rulesPath, _literalsPath, _budgets);
        if (next.RuleSet is null)
        {
            failureReason = next.FailureReason;
            // Only swap in a failure when there was nothing to preserve.
            if (_state.RuleSet is null)
            {
                _state = next;
            }
            return false;
        }

        _state = next;
        failureReason = null;
        return true;
    }

    /// <summary>
    /// The versioned per-observation ceiling. The Kestrel transport cap on
    /// <c>/capture/v1/observations</c> is deliberately far below this: it is a
    /// separate DoS guard, not the safety limit.
    /// </summary>
    public void AssertObservationWithinBudget(string serializedObservation)
    {
        RequireConfigured();
        long bytes = Encoding.UTF8.GetByteCount(serializedObservation);
        if (bytes > _budgets.MaxObservationBytes)
        {
            throw new SafetyScanException(
                $"the observation budget of {_budgets.MaxObservationBytes} bytes was exceeded");
        }
    }

    // --- free text -------------------------------------------------------

    /// <summary>Scans one free-text value. Structured field rules do not apply here.</summary>
    public NeverStoreScan Scan(string text)
    {
        var scanner = RequireConfigured();
        var state = new ScanBudgetState(_budgets);
        var outcome = scanner.ScanLeaf(text, null, state);
        return outcome.IsOmitted
            ? new NeverStoreScan(
                SafetyMarkers.Omission(outcome.OmissionReason!),
                [], [], 0, [outcome.OmissionReason!], null)
            : new NeverStoreScan(
                outcome.Value!,
                [.. outcome.RuleIds],
                [.. outcome.Categories],
                outcome.RedactionCount,
                [],
                outcome.Primary?.RuleId);
    }

    public string Redact(string text) => Scan(text).Redacted;

    /// <summary>
    /// Memory-write policy: reject. Throws <see cref="NeverStoreException"/> on
    /// a match, and <see cref="SafetyScanException"/> when the value could not
    /// be inspected completely — a required value that cannot be scanned is a
    /// fail-closed condition, not an omission.
    /// </summary>
    public void AssertAllowed(string text)
    {
        var result = Scan(text);
        RequireInspectable(result);
        if (result.RedactionCount > 0)
        {
            throw new NeverStoreException(RefusalRule(result));
        }
    }

    // Spec §5: "return an error naming the rule". The rule that actually
    // decided the refusal is the highest-priority accepted match, not whichever
    // id happens to sort first.
    private static string RefusalRule(NeverStoreScan result) =>
        result.PrimaryRuleId ?? result.RuleIds[0];

    // --- structured ------------------------------------------------------

    /// <summary>
    /// Scans decoded structured leaf values and rebuilds the document. The
    /// serialized JSON itself is never regex-rewritten.
    /// </summary>
    public NeverStoreScan ScanJson(string json)
    {
        var scanner = RequireConfigured();
        var state = new ScanBudgetState(_budgets);
        using var document = JsonDocument.Parse(json);
        var ledger = new SanitizationLedger();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitized(document.RootElement, null, scanner, state, writer, ledger);
        }

        return new NeverStoreScan(
            // GetBuffer, not ToArray: a payload may be large and the copy is
            // pure waste.
            Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length),
            [.. ledger.RuleIds], [.. ledger.Categories], ledger.RedactionCount,
            [.. ledger.Omissions], ledger.Primary?.RuleId);
    }

    public string RedactJson(string json) => ScanJson(json).Redacted;

    public string RedactObject(object value) =>
        RedactJson(JsonSerializer.Serialize(value));

    public void AssertAllowedObject(object value)
    {
        var result = ScanJson(JsonSerializer.Serialize(value));
        RequireInspectable(result);
        if (result.RedactionCount > 0)
        {
            throw new NeverStoreException(RefusalRule(result));
        }
    }

    private static void RequireInspectable(NeverStoreScan result)
    {
        if (result.OmissionReasons.Count > 0)
        {
            throw new SafetyScanException(
                $"a required value could not be inspected completely ({result.OmissionReasons[0]})");
        }
    }

    private static void WriteSanitized(
        JsonElement element,
        string? propertyName,
        SecretScanner scanner,
        ScanBudgetState state,
        Utf8JsonWriter writer,
        SanitizationLedger ledger)
    {
        // A sensitive property name carrying a subtree has no exact span to
        // map, so the whole value is omitted rather than partially rewritten.
        if (propertyName is not null
            && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            && scanner.IsSensitiveField(propertyName, state))
        {
            WriteOmitted(writer, ledger, OmissionReasons.SensitiveFieldSubtree);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // Property NAMES cross the same rule set as values: a
                // credential used as a map key, or an environment dump keyed by
                // its value, would otherwise persist verbatim forever.
                var properties = new List<(string SafeName, JsonProperty Property)>();
                // Value: whether any name written as this key was CHANGED by
                // sanitization. Duplicate keys are legal JSON the source may
                // already contain; only a collision this gate CAUSED is a loss
                // it has to answer for.
                var safeNames = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    string safeName = SanitizeName(property.Name, scanner, state, ledger);
                    bool changed = !string.Equals(safeName, property.Name, StringComparison.Ordinal);
                    if (safeNames.TryGetValue(safeName, out bool earlierChanged))
                    {
                        if (changed || earlierChanged)
                        {
                            // Two siblings collapsed to one key. Emitting both
                            // would write a duplicate key and lose a value on
                            // re-parse, so the object goes as a whole with a
                            // stated reason.
                            WriteOmitted(writer, ledger, OmissionReasons.RedactedNameCollision);
                            return;
                        }
                    }
                    else
                    {
                        safeNames.Add(safeName, changed);
                    }
                    properties.Add((safeName, property));
                }

                writer.WriteStartObject();
                foreach (var (safeName, property) in properties)
                {
                    writer.WritePropertyName(safeName);
                    // The ORIGINAL name governs sensitive-field recognition:
                    // redacting the name must not change what the value means.
                    WriteSanitized(property.Value, property.Name, scanner, state, writer, ledger);
                }
                writer.WriteEndObject();
                return;
            }

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    // Elements inherit the array's field name so
                    // "tokens": ["..."] is still recognized as sensitive.
                    WriteSanitized(item, propertyName, scanner, state, writer, ledger);
                }
                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                var outcome = scanner.ScanLeaf(element.GetString() ?? "", propertyName, state);
                if (outcome.IsOmitted)
                {
                    WriteOmitted(writer, ledger, outcome.OmissionReason!);
                    return;
                }
                ledger.Record(outcome);
                writer.WriteStringValue(outcome.Value);
                return;

            default:
                // A sensitive field carrying a number or boolean likewise has
                // no span; a low-entropy numeric PIN is still a credential.
                if (propertyName is not null
                    && element.ValueKind is not JsonValueKind.Null
                    && scanner.IsSensitiveField(propertyName, state))
                {
                    WriteOmitted(writer, ledger, OmissionReasons.SensitiveFieldScalar);
                    return;
                }
                element.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// Sanitizes one property name and returns the text that will be written as
    /// the JSON key. An unscannable name becomes its omission marker, which
    /// takes part in the same collision check as any other key.
    /// </summary>
    private static string SanitizeName(
        string name, SecretScanner scanner, ScanBudgetState state, SanitizationLedger ledger)
    {
        var outcome = scanner.ScanLeaf(name, null, state);
        if (outcome.IsOmitted)
        {
            ledger.Omit(outcome.OmissionReason!);
            return SafetyMarkers.Omission(outcome.OmissionReason!);
        }
        ledger.Record(outcome);
        return outcome.Value!;
    }

    private static void WriteOmitted(
        Utf8JsonWriter writer, SanitizationLedger ledger, string reason)
    {
        ledger.Omit(reason);
        writer.WriteStringValue(SafetyMarkers.Omission(reason));
    }

    /// <summary>
    /// Everything one structured sanitization pass accumulates. It exists so
    /// the function that decides what persists takes one accumulator instead of
    /// four separate out-parameters threaded through every recursion.
    /// </summary>
    private sealed class SanitizationLedger
    {
        public SortedSet<string> RuleIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Categories { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Omissions { get; } = new(StringComparer.Ordinal);
        public int RedactionCount { get; private set; }
        public PrimaryMatch? Primary { get; private set; }

        public void Record(LeafOutcome outcome)
        {
            RuleIds.UnionWith(outcome.RuleIds);
            Categories.UnionWith(outcome.Categories);
            RedactionCount += outcome.RedactionCount;
            Primary = PrimaryMatch.Best(Primary, outcome.Primary);
        }

        public void Omit(string reason) => Omissions.Add(reason);
    }

    private SecretScanner RequireConfigured()
    {
        var state = _state;
        if (state.Scanner is null)
        {
            throw new SafetyConfigurationException(state.FailureReason ?? "unknown reason");
        }
        return state.Scanner;
    }

    private static State Load(string rulesPath, string? literalsPath, SafetyBudgets budgets)
    {
        if (SecretRuleSet.TryLoad(
                rulesPath, literalsPath, budgets.MaxRuleTime, out var ruleSet, out string? reason))
        {
            return new State(ruleSet, new SecretScanner(ruleSet!, budgets), null);
        }
        return new State(null, null, reason);
    }

    private sealed record State(SecretRuleSet? RuleSet, SecretScanner? Scanner, string? FailureReason);
}

public sealed record NeverStoreScan(
    string Redacted,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> Categories,
    int RedactionCount,
    IReadOnlyList<string> OmissionReasons,
    /// <summary>
    /// The rule a refusal names: the highest-priority accepted match, not the
    /// ordinal-first id. Null when nothing matched.
    /// </summary>
    string? PrimaryRuleId);
