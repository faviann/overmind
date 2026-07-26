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
            ? new NeverStoreScan($"[OMITTED:{outcome.OmissionReason}]", [], [], 0, [outcome.OmissionReason!])
            : new NeverStoreScan(
                outcome.Value!,
                [.. outcome.RuleIds],
                [.. outcome.Categories],
                outcome.RedactionCount,
                []);
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
            throw new NeverStoreException(result.RuleIds[0]);
        }
    }

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
        var ruleIds = new SortedSet<string>(StringComparer.Ordinal);
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        var omissions = new SortedSet<string>(StringComparer.Ordinal);
        int count = 0;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitized(
                document.RootElement, null, scanner, state, writer,
                ruleIds, categories, omissions, ref count);
        }

        return new NeverStoreScan(
            // GetBuffer, not ToArray: a payload may be large and the copy is
            // pure waste.
            Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length),
            [.. ruleIds], [.. categories], count, [.. omissions]);
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
            throw new NeverStoreException(result.RuleIds[0]);
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
        SortedSet<string> ruleIds,
        SortedSet<string> categories,
        SortedSet<string> omissions,
        ref int count)
    {
        // A sensitive property name carrying a subtree has no exact span to
        // map, so the whole value is omitted rather than partially rewritten.
        if (propertyName is not null
            && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            && scanner.IsSensitiveField(propertyName))
        {
            omissions.Add(OmissionReasons.SensitiveFieldSubtree);
            writer.WriteStringValue($"[OMITTED:{OmissionReasons.SensitiveFieldSubtree}]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitized(
                        property.Value, property.Name, scanner, state, writer,
                        ruleIds, categories, omissions, ref count);
                }
                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    // Elements inherit the array's field name so
                    // "tokens": ["..."] is still recognized as sensitive.
                    WriteSanitized(
                        item, propertyName, scanner, state, writer,
                        ruleIds, categories, omissions, ref count);
                }
                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                var outcome = scanner.ScanLeaf(element.GetString() ?? "", propertyName, state);
                if (outcome.IsOmitted)
                {
                    omissions.Add(outcome.OmissionReason!);
                    writer.WriteStringValue($"[OMITTED:{outcome.OmissionReason}]");
                    return;
                }
                foreach (string id in outcome.RuleIds) { ruleIds.Add(id); }
                foreach (string category in outcome.Categories) { categories.Add(category); }
                count += outcome.RedactionCount;
                writer.WriteStringValue(outcome.Value);
                return;

            default:
                // A sensitive field carrying a number or boolean likewise has
                // no span; a low-entropy numeric PIN is still a credential.
                if (propertyName is not null
                    && element.ValueKind is not JsonValueKind.Null
                    && scanner.IsSensitiveField(propertyName))
                {
                    omissions.Add(OmissionReasons.SensitiveFieldScalar);
                    writer.WriteStringValue($"[OMITTED:{OmissionReasons.SensitiveFieldScalar}]");
                    return;
                }
                element.WriteTo(writer);
                return;
        }
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
    IReadOnlyList<string> OmissionReasons);
