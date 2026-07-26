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
    public CaptureAdapter Identity { get; } = new("codex-synthetic-jsonl", "1");

    public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
    {
        if (!source.IsTerminal)
        {
            return new CaptureSourcePositionOutcome.Incomplete(
                source.SourcePosition, "source record may still be extended");
        }

        JsonElement record = source.SourcePayload;
        string? recordType = JsonAdapterHelpers.NullableString(record, "type");
        string? harnessVersion =
            JsonAdapterHelpers.NullableString(record, "cli_version")
            ?? JsonAdapterHelpers.NullableString(record, "version");
        string? model = JsonAdapterHelpers.NullableString(record, "model");
        string? provider =
            JsonAdapterHelpers.NullableString(record, "model_provider")
            ?? JsonAdapterHelpers.NullableString(record, "provider");
        JsonElement payload = record.TryGetProperty("payload", out var nested)
            ? nested
            : record;
        model ??= JsonAdapterHelpers.NullableString(payload, "model");
        provider ??=
            JsonAdapterHelpers.NullableString(payload, "model_provider")
            ?? JsonAdapterHelpers.NullableString(payload, "provider");

        IReadOnlyList<CaptureEvent> events = Interpret(
            recordType, payload, source.SourcePosition);
        var request = new CaptureObservationRequest(
            ContractVersion: 1,
            source.SourceSessionId,
            source.SourcePosition,
            ToWireLocator(source.Locator),
            JsonAdapterHelpers.SourceTimestamp(record),
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
                "message" => [Message(payload, position)],
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
                "error" or "turn_aborted" => [Error(payload, position)],
                "subagent_start" => [Subagent(payload, position)],
                _ => [Opaque(recordType, payloadType, payload, position)]
            };
        }

        return [Opaque(recordType, payloadType, payload, position)];
    }

    private static CaptureEvent Message(JsonElement payload, long position)
    {
        string actor = JsonAdapterHelpers.NullableString(payload, "role") ?? "unknown";
        JsonElement content = payload.TryGetProperty("content", out var value)
            ? value
            : default;
        return Event(
            $"message/{position}",
            "message",
            actor,
            new { text = content.ValueKind == JsonValueKind.Undefined ? null : JsonAdapterHelpers.Text(content) });
    }

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
                trigger = JsonAdapterHelpers.NullableString(payload, "trigger"),
                outcome = JsonAdapterHelpers.NullableString(payload, "outcome") ?? "unknown",
                summary = payload.TryGetProperty("summary", out var summary)
                    ? JsonAdapterHelpers.Text(summary)
                    : null,
                metrics = payload.TryGetProperty("metrics", out var metrics)
                    ? metrics.Clone()
                    : (JsonElement?)null
            });

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
        Event(
            $"opaque/{position}",
            "opaque",
            "unknown",
            new { recordType, payloadType, source = payload.Clone() });

    private static CaptureEvent Event(
        string partKey,
        string kind,
        string actor,
        object payload,
        IReadOnlyList<CaptureRelationship>? relationships = null) =>
        new(
            partKey,
            PartOrder: 0,
            kind,
            actor,
            JsonAdapterHelpers.Json(payload),
            OccurredAt: null,
            relationships ?? []);

    private static CaptureRelationship Relationship(string type, string nativeId, string kind) =>
        new(type, new CaptureRelationshipTarget(null, nativeId, kind));

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
