using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

/// <summary>
/// The narrow Codex JSONL adapter used by the disabled synthetic tracer.
/// Unknown records remain opaque and all optional evidence stays optional.
/// </summary>
public sealed class CodexJsonlAdapter : ICaptureSourceAdapter
{
    public string Harness => "codex";
    public CaptureAdapter Identity { get; } = new("codex-synthetic-jsonl", "4");

    public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
    {
        if (!source.IsTerminal)
        {
            return new CaptureSourcePositionOutcome.Incomplete(
                source.SourcePosition, "source record may still be extended");
        }

        JsonElement record = source.SourcePayload;
        bool isObjectRecord = record.ValueKind == JsonValueKind.Object;
        string? recordType = isObjectRecord
            ? JsonAdapterHelpers.NullableString(record, "type")
                ?? JsonAdapterHelpers.NullableString(record, "hook_event_name")
            : null;
        string? harnessVersion = isObjectRecord
            ? UsableString(record, "cli_version")
                ?? UsableString(record, "version")
            : null;
        string? model = isObjectRecord
            ? JsonAdapterHelpers.NullableString(record, "model")
            : null;
        string? provider = isObjectRecord
            ? JsonAdapterHelpers.NullableString(record, "model_provider")
                ?? JsonAdapterHelpers.NullableString(record, "provider")
            : null;
        JsonElement nested = default;
        bool hasPayload = isObjectRecord
            && record.TryGetProperty("payload", out nested);
        JsonElement payload = hasPayload ? nested : record;
        bool isUnsupportedShape = !isObjectRecord
            || (hasPayload && payload.ValueKind != JsonValueKind.Object);
        if (!isUnsupportedShape)
        {
            model ??= JsonAdapterHelpers.NullableString(payload, "model");
            provider ??=
                JsonAdapterHelpers.NullableString(payload, "model_provider")
                ?? JsonAdapterHelpers.NullableString(payload, "provider");
            if (string.Equals(recordType, "session_meta", StringComparison.Ordinal))
            {
                harnessVersion ??= UsableString(payload, "cli_version");
            }
        }

        IReadOnlyList<CaptureEvent> events = isUnsupportedShape
            ? [Opaque(recordType, null, payload, "record:opaque")]
            : Interpret(recordType, payload, source.SourcePosition);
        var request = new CaptureObservationRequest(
            ContractVersion: 1,
            source.SourceSessionId,
            source.SourcePosition,
            ToWireLocator(source.Locator),
            isObjectRecord ? JsonAdapterHelpers.SourceTimestamp(record) : null,
            new CaptureSource(
                Harness,
                harnessVersion,
                recordType,
                model,
                provider,
                MaterialKind(source.MaterialKind)),
            Identity,
            record.Clone(),
            events);
        return new CaptureSourcePositionOutcome.Terminal(source.SourcePosition, request);
    }

    private static IReadOnlyList<CaptureEvent> Interpret(
        string? recordType, JsonElement payload, long position)
    {
        string? payloadType = JsonAdapterHelpers.NullableString(payload, "type");
        if (string.Equals(recordType, "response_item", StringComparison.Ordinal))
        {
            return payloadType switch
            {
                "message" => MessageContent(payload),
                "function_call" => [ToolCall(payload, position)],
                "function_call_output" => [ToolResult(payload, position)],
                "compacted" => [Compaction(payload, position)],
                _ => [Opaque(recordType, payloadType, payload, position)]
            };
        }

        if (string.Equals(recordType, "compacted", StringComparison.Ordinal)
            || string.Equals(payloadType, "compacted", StringComparison.Ordinal))
        {
            return [Compaction(payload, position)];
        }

        if (string.Equals(recordType, "event_msg", StringComparison.Ordinal))
        {
            return payloadType switch
            {
                "user_message" or "agent_message" =>
                    [MessageView(payload, payloadType)],
                "context_compacted" => [CompactionAnnotation(payload)],
                "error" or "turn_aborted" => [Error(payload, position)],
                "subagent_start" => [Subagent(payload, position)],
                _ => [Opaque(recordType, payloadType, payload, position)]
            };
        }

        if (string.Equals(recordType, "PreCompact", StringComparison.Ordinal))
        {
            return [CompactionHook(payload, position, "request")];
        }

        if (string.Equals(recordType, "PostCompact", StringComparison.Ordinal))
        {
            return [CompactionHook(payload, position, "completion")];
        }

        if (string.Equals(recordType, "session_meta", StringComparison.Ordinal))
        {
            return [Context(payload, "session")];
        }

        if (string.Equals(recordType, "turn_context", StringComparison.Ordinal))
        {
            return [Context(payload, "turn")];
        }

        return [Opaque(recordType, payloadType, payload, position)];
    }

    private static CaptureEvent Context(JsonElement payload, string scope)
    {
        string? scopeId = scope == "session"
            ? JsonAdapterHelpers.NullableString(payload, "id")
                ?? JsonAdapterHelpers.NullableString(payload, "session_id")
            : JsonAdapterHelpers.NullableString(payload, "turn_id");
        string baseEvidence = payload.TryGetProperty("base_instructions", out var instructions)
            && instructions.ValueKind is not JsonValueKind.Null
                and not JsonValueKind.Undefined
            ? "exposed"
            : "unavailable";
        DateTimeOffset? occurredAt = scope == "session"
            ? JsonAdapterHelpers.SourceTimestamp(payload)?.Parsed
            : null;
        return Event(
            $"context:{scope}",
            "context",
            "harness",
            new
            {
                scope,
                scopeId,
                values = payload.Clone(),
                instructionEvidence = new
                {
                    @base = baseEvidence,
                    builtIn = "unavailable",
                    loaded = "unavailable"
                }
            },
            occurredAt: occurredAt);
    }

    private static IReadOnlyList<CaptureEvent> MessageContent(JsonElement payload)
    {
        string actor = MessageActor(JsonAdapterHelpers.NullableString(payload, "role"));
        if (!payload.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return
                [
                    Event(
                        "content:message",
                        "message",
                        actor,
                        new { text = content.GetString() })
                ];
            }

            JsonElement sourceEvidence = content.ValueKind == JsonValueKind.Undefined
                ? payload.Clone()
                : content.Clone();
            return
            [
                Event(
                    "content:opaque",
                    "opaque",
                    "unknown",
                    new { contentType = "message_content", source = sourceEvidence })
            ];
        }

        var events = new List<CaptureEvent>();
        int index = 0;
        foreach (JsonElement part in content.EnumerateArray())
        {
            string? partType = part.ValueKind == JsonValueKind.Object
                ? JsonAdapterHelpers.NullableString(part, "type")
                : null;
            bool isMessagePart = part.ValueKind == JsonValueKind.String
                || (partType is "input_text" or "output_text" or "text"
                    && part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String);
            events.Add(isMessagePart
                ? Event(
                    $"content/{index}:message",
                    "message",
                    actor,
                    new { text = JsonAdapterHelpers.Text(part) },
                    partOrder: index)
                : Event(
                    $"content/{index}:opaque",
                    "opaque",
                    "unknown",
                    new { contentType = partType, source = part.Clone() },
                    partOrder: index));
            index++;
        }

        return events.Count == 0
            ?
            [
                Event(
                    "content:opaque",
                    "opaque",
                    "unknown",
                    new { contentType = "message_content", source = content.Clone() })
            ]
            : events;
    }

    private static CaptureEvent MessageView(JsonElement payload, string view)
    {
        if (!payload.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String)
        {
            return Opaque("event_msg", view, payload, $"view:{view}:opaque");
        }

        return Event(
            $"view:{view}",
            "annotation",
            "harness",
            new { view, text = message.GetString() });
    }

    private static string MessageActor(string? role) =>
        role is "user" or "assistant" or "developer" or "system"
            ? role
            : "unknown";

    private static CaptureEvent ToolCall(JsonElement payload, long position)
    {
        string? callId = JsonAdapterHelpers.NullableString(payload, "call_id");
        JsonElement arguments = payload.TryGetProperty("arguments", out var value)
            ? JsonAdapterHelpers.ObjectOrParsedString(value)
            : JsonAdapterHelpers.Json((object?)null);
        return Event(
            $"tool/{position}",
            "tool_call",
            "assistant",
            new
            {
                callId,
                tool = JsonAdapterHelpers.NullableString(payload, "name"),
                arguments
            });
    }

    private static CaptureEvent ToolResult(JsonElement payload, long position)
    {
        string? callId = JsonAdapterHelpers.NullableString(payload, "call_id");
        string outcome = JsonAdapterHelpers.NullableString(payload, "outcome") ?? "unknown";
        JsonElement output = payload.TryGetProperty("output", out var value)
            ? JsonAdapterHelpers.Json(JsonAdapterHelpers.Text(value))
            : JsonAdapterHelpers.Json((object?)null);
        IReadOnlyList<CaptureRelationship> relationships = callId is null
            ? []
            : [Relationship("result_for", callId, "tool_call")];
        return Event(
            $"tool/{position}",
            "tool_result",
            "tool",
            new { callId, outcome, output },
            relationships);
    }

    private static CaptureEvent Error(JsonElement payload, long position) =>
        Event(
            $"error/{position}",
            "error",
            "harness",
            new
            {
                error = payload.TryGetProperty("message", out var message)
                    ? JsonAdapterHelpers.Text(message)
                    : null,
                outcome = JsonAdapterHelpers.NullableString(payload, "outcome") ?? "unknown"
            });

    private static CaptureEvent Compaction(JsonElement payload, long position) =>
        Event(
            $"compaction/{position}",
            "compaction",
            "harness",
            new
            {
                phase = "completion",
                contextBoundary = true,
                trigger = JsonAdapterHelpers.NullableString(payload, "trigger"),
                outcome = JsonAdapterHelpers.NullableString(payload, "outcome") ?? "unknown",
                instructions = Evidence(payload, "instructions"),
                inputContext = Evidence(payload, "input_context"),
                summary = Evidence(payload, "message") ?? Evidence(payload, "summary"),
                replacementHistory = Evidence(payload, "replacement_history"),
                metrics = payload.TryGetProperty("metrics", out var metrics)
                    ? metrics.Clone()
                    : (JsonElement?)null,
                windowMetrics = WindowMetrics(payload)
            });

    private static CaptureEvent CompactionHook(
        JsonElement payload, long position, string phase) =>
        Event(
            $"compaction/{position}",
            "compaction",
            "harness",
            new
            {
                phase,
                contextBoundary = phase == "completion",
                trigger = JsonAdapterHelpers.NullableString(payload, "trigger"),
                outcome = JsonAdapterHelpers.NullableString(payload, "outcome") ?? "unknown",
                instructions = Evidence(payload, "instructions"),
                inputContext = Evidence(payload, "input_context"),
                summary = Evidence(payload, "summary"),
                replacementHistory = Evidence(payload, "replacement_history"),
                metrics = Evidence(payload, "metrics"),
                windowMetrics = WindowMetrics(payload)
            });

    private static CaptureEvent CompactionAnnotation(JsonElement payload) =>
        Event(
            "view:context_compacted",
            "annotation",
            "harness",
            new
            {
                view = "context_compacted",
                evidence = payload.Clone()
            });

    private static JsonElement? Evidence(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var value)
            ? value.Clone()
            : null;

    private static JsonElement? WindowMetrics(JsonElement payload)
    {
        var values = new Dictionary<string, JsonElement>();
        AddWindowMetric(payload, values, "first_window_id", "firstWindowId");
        AddWindowMetric(payload, values, "previous_window_id", "previousWindowId");
        AddWindowMetric(payload, values, "window_id", "windowId");
        AddWindowMetric(payload, values, "window_number", "windowNumber");
        return values.Count == 0 ? null : JsonAdapterHelpers.Json(values);
    }

    private static void AddWindowMetric(
        JsonElement payload,
        IDictionary<string, JsonElement> values,
        string sourceName,
        string canonicalName)
    {
        if (payload.TryGetProperty(sourceName, out var value))
        {
            values.Add(canonicalName, value.Clone());
        }
    }

    private static CaptureEvent Subagent(JsonElement payload, long position)
    {
        var relationships = new List<CaptureRelationship>();
        if (JsonAdapterHelpers.NullableString(payload, "call_id") is { } callId)
        {
            relationships.Add(Relationship("spawned_by", callId, "tool_call"));
        }
        if (JsonAdapterHelpers.NullableString(payload, "parent_thread_id") is { } parentId)
        {
            relationships.Add(Relationship("parent_session", parentId, "session"));
        }
        return Event(
            $"subagent/{position}",
            "lifecycle",
            "harness",
            new
            {
                action = "subagent_start",
                childId = JsonAdapterHelpers.NullableString(payload, "agent_id"),
                agentType = JsonAdapterHelpers.NullableString(payload, "agent_type")
            },
            relationships);
    }

    private static CaptureEvent Opaque(
        string? recordType, string? payloadType, JsonElement payload, long position) =>
        Opaque(recordType, payloadType, payload, $"opaque/{position}");

    private static CaptureEvent Opaque(
        string? recordType, string? payloadType, JsonElement payload, string partKey) =>
        Event(
            partKey,
            "opaque",
            "unknown",
            new { recordType, payloadType, source = payload.Clone() });

    private static CaptureEvent Event(
        string partKey,
        string kind,
        string actor,
        object payload,
        IReadOnlyList<CaptureRelationship>? relationships = null,
        int partOrder = 0,
        DateTimeOffset? occurredAt = null) =>
        new(
            partKey,
            partOrder,
            kind,
            actor,
            JsonAdapterHelpers.Json(payload),
            OccurredAt: occurredAt,
            relationships ?? []);

    private static CaptureRelationship Relationship(string type, string nativeId, string kind) =>
        new(type, new CaptureRelationshipTarget(null, nativeId, kind));

    private static string? UsableString(JsonElement owner, string name)
    {
        string? value = JsonAdapterHelpers.NullableString(owner, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static CaptureLocator ToWireLocator(CaptureSourceLocator locator) =>
        locator switch
        {
            CaptureSourceLocator.NativeId native =>
                new CaptureLocator("native_id", native.Value, null, null, null),
            CaptureSourceLocator.ByteRange range =>
                new CaptureLocator(
                    "byte_range", null, range.Offset, range.Length, range.SourceContentSha256),
            _ => throw new InvalidOperationException("Unknown source locator variant.")
        };

    private static string MaterialKind(CaptureSourceMaterialKind kind) =>
        kind switch
        {
            CaptureSourceMaterialKind.PersistedRecord => "persisted_record",
            CaptureSourceMaterialKind.HookFact => "hook_fact",
            _ => throw new InvalidOperationException("Unknown source material kind.")
        };
}
