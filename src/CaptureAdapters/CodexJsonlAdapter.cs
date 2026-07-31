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
    public CaptureAdapter Identity { get; } = new("codex-synthetic-jsonl", "8");

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
            ? [Opaque(recordType, null, record, "record:opaque")]
            : Interpret(record, recordType, payload, source.SourcePosition);
        var request = new CaptureObservationRequest(
            ContractVersion: 1,
            source.SourceIdentity.ExternalSessionId,
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
            events,
            SourceIdentity: source.SourceIdentity);
        return new CaptureSourcePositionOutcome.Terminal(source.SourcePosition, request);
    }

    private static IReadOnlyList<CaptureEvent> Interpret(
        JsonElement record, string? recordType, JsonElement payload, long position)
    {
        string? payloadType = JsonAdapterHelpers.NullableString(payload, "type");
        if (string.Equals(recordType, "response_item", StringComparison.Ordinal))
        {
            return payloadType switch
            {
                "message" => MessageContent(payload),
                "reasoning" => ReasoningContent(payload),
                "function_call" => [ToolCall(payload, position, "name", "arguments")],
                "function_call_output" => [ToolResult(payload, position, "output")],
                "custom_tool_call" => [ToolCall(payload, position, "name", "input")],
                "custom_tool_call_output" => [ToolResult(payload, position, "output")],
                "local_shell_call" =>
                    TerminalSpecializedItem(
                        payload, position, "local_shell", "action", "output"),
                "tool_search_call" =>
                    [ToolCall(payload, position, null, "arguments", "tool_search")],
                "tool_search_output" => [ToolResult(payload, position, "tools")],
                "web_search_call" =>
                    TerminalSpecializedItem(
                        payload, position, "web_search", "action", null),
                "image_generation_call" =>
                    TerminalSpecializedItem(
                        payload, position, "image_generation", null, "result"),
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
                "turn_started" or "agent_reasoning" or "context_compacted" =>
                    [AnnotationView(payload, payloadType)],
                "exec_command_begin" or "patch_apply_begin"
                    or "exec_command_end" or "patch_apply_end" =>
                    [AnnotationView(payload, payloadType)],
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
            return [Context(payload, "session", SessionRelationships(payload))];
        }

        if (string.Equals(recordType, "turn_context", StringComparison.Ordinal))
        {
            return [Context(payload, "turn")];
        }

        return [Opaque(recordType, payloadType, record, position)];
    }

    private static IReadOnlyList<CaptureEvent> ReasoningContent(JsonElement payload)
    {
        string actor = MessageActor(JsonAdapterHelpers.NullableString(payload, "role"));
        var events = new List<CaptureEvent>();
        AddReasoningParts(payload, "summary", "summary_text", actor, events);
        AddReasoningParts(payload, "content", "reasoning_text", actor, events);
        return events.Count == 0
            ? [Opaque("response_item", "reasoning", payload, "reasoning:opaque", actor)]
            : events;
    }

    private static void AddReasoningParts(
        JsonElement payload,
        string propertyName,
        string partType,
        string actor,
        List<CaptureEvent> events)
    {
        if (!payload.TryGetProperty(propertyName, out JsonElement parts))
        {
            return;
        }

        if (parts.ValueKind != JsonValueKind.Array)
        {
            events.Add(Event(
                $"{propertyName}:opaque",
                "opaque",
                actor,
                new
                {
                    contentType = parts.ValueKind == JsonValueKind.Object
                        ? JsonAdapterHelpers.NullableString(parts, "type")
                        : null,
                    source = parts.Clone()
                },
                partOrder: events.Count));
            return;
        }

        int index = 0;
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && string.Equals(
                    JsonAdapterHelpers.NullableString(part, "type"),
                    partType,
                    StringComparison.Ordinal)
                && part.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
                events.Add(Event(
                    $"{propertyName}/{index}:reasoning",
                    "reasoning",
                    actor,
                    new { text = text.GetString() },
                    partOrder: events.Count));
            }
            else
            {
                events.Add(Event(
                    $"{propertyName}/{index}:opaque",
                    "opaque",
                    actor,
                    new
                    {
                        contentType = part.ValueKind == JsonValueKind.Object
                            ? JsonAdapterHelpers.NullableString(part, "type")
                            : null,
                        source = part.Clone()
                    },
                    partOrder: events.Count));
            }
            index++;
        }
    }

    private static CaptureEvent Context(
        JsonElement payload,
        string scope,
        IReadOnlyList<CaptureRelationship>? relationships = null)
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
            relationships,
            occurredAt: occurredAt);
    }

    private static IReadOnlyList<CaptureRelationship> SessionRelationships(JsonElement payload)
    {
        var relationships = new List<CaptureRelationship>();
        AddRelationship(payload, "parent_thread_id", "parent_session", relationships);
        AddRelationship(payload, "forked_from_id", "forked_from", relationships);

        if (payload.TryGetProperty("source", out JsonElement source)
            && source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("subagent", out JsonElement subagent)
            && subagent.ValueKind == JsonValueKind.Object
            && subagent.TryGetProperty("thread_spawn", out JsonElement spawn)
            && spawn.ValueKind == JsonValueKind.Object)
        {
            AddRelationship(spawn, "parent_thread_id", "spawned_by", relationships);
        }

        string? childId = UsableString(payload, "id");
        if (childId is not null && ExplicitSubagentSource(payload))
        {
            relationships.Add(Relationship("source_classification", childId, "session"));
        }
        if (childId is not null
            && string.Equals(
                UsableString(payload, "thread_source"),
                "subagent",
                StringComparison.OrdinalIgnoreCase))
        {
            relationships.Add(
                Relationship("thread_source_classification", childId, "session"));
        }
        return relationships;
    }

    private static void AddRelationship(
        JsonElement source,
        string propertyName,
        string type,
        ICollection<CaptureRelationship> relationships)
    {
        if (UsableString(source, propertyName) is { } target)
        {
            relationships.Add(Relationship(type, target, "session"));
        }
    }

    private static bool ExplicitSubagentSource(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out JsonElement source))
        {
            return false;
        }
        if (source.ValueKind == JsonValueKind.String)
        {
            return string.Equals(
                source.GetString(), "subagent", StringComparison.OrdinalIgnoreCase);
        }
        return source.ValueKind == JsonValueKind.Object
            && (source.TryGetProperty("subagent", out _)
                || source.TryGetProperty("sub_agent", out _));
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

    private static CaptureEvent AnnotationView(JsonElement payload, string view) =>
        Event(
            $"view:{view}",
            "annotation",
            "harness",
            new { view, source = payload.Clone() });

    private static string MessageActor(string? role) =>
        role is "user" or "assistant" or "developer" or "system"
            ? role
            : "unknown";

    private static CaptureEvent ToolCall(
        JsonElement payload,
        long position,
        string? toolProperty,
        string? argumentsProperty,
        string? fixedTool = null,
        int partOrder = 0)
    {
        string? callId = NativeCallId(payload);
        JsonElement arguments = argumentsProperty is not null
            && payload.TryGetProperty(argumentsProperty, out var value)
            ? JsonAdapterHelpers.ObjectOrParsedString(value)
            : JsonAdapterHelpers.Json((object?)null);
        return Event(
            ToolPartKey("tool_call", callId, position),
            "tool_call",
            MessageActor(JsonAdapterHelpers.NullableString(payload, "role")),
            new
            {
                callId,
                tool = fixedTool ?? (toolProperty is null
                    ? null
                    : JsonAdapterHelpers.NullableString(payload, toolProperty)),
                arguments
            },
            partOrder: partOrder);
    }

    private static CaptureEvent ToolResult(
        JsonElement payload,
        long position,
        string? outputProperty,
        int partOrder = 0)
    {
        string? callId = NativeCallId(payload);
        string outcome = ExplicitToolOutcome(payload);
        JsonElement output = outputProperty is not null
            && payload.TryGetProperty(outputProperty, out var value)
            ? value.Clone()
            : JsonAdapterHelpers.Json((object?)null);
        IReadOnlyList<CaptureRelationship> relationships = callId is null
            ? []
            : [Relationship("result_for", callId, "tool_call")];
        return Event(
            ToolPartKey("tool_result", callId, position),
            "tool_result",
            MessageActor(JsonAdapterHelpers.NullableString(payload, "role")),
            new { callId, outcome, output },
            relationships,
            partOrder);
    }

    private static IReadOnlyList<CaptureEvent> TerminalSpecializedItem(
        JsonElement payload,
        long position,
        string tool,
        string? argumentsProperty,
        string? outputProperty)
    {
        CaptureEvent call = ToolCall(
            payload,
            position,
            null,
            argumentsProperty,
            tool);
        return ExplicitToolOutcome(payload) == "unknown"
            ? [call]
            : [call, ToolResult(payload, position, outputProperty, partOrder: 1)];
    }

    private static string? NativeCallId(JsonElement payload) =>
        UsableString(payload, "call_id") ?? UsableString(payload, "id");

    private static string ToolPartKey(string kind, string? callId, long position) =>
        callId is null ? $"{kind}/{position}" : $"{kind}:{callId}";

    private static string ExplicitToolOutcome(JsonElement payload)
    {
        string? outcome = JsonAdapterHelpers.NullableString(payload, "outcome");
        if (outcome is "succeeded" or "failed" or "denied" or "interrupted" or "unknown")
        {
            return outcome;
        }

        string statusOutcome = JsonAdapterHelpers.NullableString(payload, "status") switch
        {
            "completed" or "succeeded" => "succeeded",
            "failed" => "failed",
            "declined" or "denied" => "denied",
            "interrupted" or "cancelled" or "canceled" => "interrupted",
            _ => "unknown"
        };
        if (statusOutcome != "unknown")
        {
            return statusOutcome;
        }

        return payload.TryGetProperty("success", out JsonElement success)
            && success.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? success.GetBoolean() ? "succeeded" : "failed"
                : "unknown";
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
                    : payload.TryGetProperty("reason", out var reason)
                        ? JsonAdapterHelpers.Text(reason)
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
                summary = Evidence(payload, "message") ?? Evidence(payload, "summary"),
                replacementHistory = Evidence(payload, "replacement_history"),
                metrics = Evidence(payload, "metrics"),
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
                summary = Evidence(payload, "summary"),
                replacementHistory = Evidence(payload, "replacement_history"),
                metrics = Evidence(payload, "metrics"),
                windowMetrics = WindowMetrics(payload)
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
        string? recordType,
        string? payloadType,
        JsonElement payload,
        string partKey,
        string actor = "unknown") =>
        Event(
            partKey,
            "opaque",
            actor,
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
