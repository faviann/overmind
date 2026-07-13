using System.Text.Json.Serialization;
using System.Text.Json;

namespace MemSrv.Core;

public sealed record ToolEnvelope<T>(
    [property: JsonPropertyName("data")] T Data,
    [property: JsonPropertyName("next")] string Next);

// SessionId echoes the server-derived session so agents can reference their
// own run and legacy callers can observe the substitution.
public sealed record TraceResult(Guid TraceUuid, string SessionId);

public sealed record MemoryWriteResult(Guid Uuid, string Status);

public sealed record LaneScore(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("score")] double Score);

public sealed record SearchMemoryResult(
    Guid Uuid,
    string Type,
    string Tier,
    string Status,
    string Preview,
    string SourceType,
    string? SourceId,
    int Version,
    IReadOnlyDictionary<string, LaneScore> LaneScores,
    double FusedScore);

public sealed record MemoryRecord(
    Guid Uuid,
    string Namespace,
    string Type,
    string Visibility,
    string Status,
    string Tier,
    string Content,
    string SourceType,
    string? SourceId,
    string AgentId,
    string? SessionId,
    int Version,
    Guid? Supersedes,
    Guid? SupersededBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RetiredAt,
    string ContentHash,
    JsonElement Metadata);

public sealed record WorkstreamRecord(
    Guid Uuid,
    string Namespace,
    string Title,
    string Status,
    string? OwnerAgent,
    string? SessionId,
    string? Notes,
    Guid[]? Refs,
    DateTimeOffset UpdatedAt);

public sealed record WorkstreamCheckoutResult(WorkstreamRecord Workstream, bool Created);

// One read in a session's consumed set. Kind is "memory" (memory_consumed;
// Type/SourceType/SourceId describe the memory) or "trace" (trace_consumed;
// Type carries the read trace's event_type, source columns are null).
public sealed record ConsumedEntry(
    DateTimeOffset Ts,
    string Kind,
    Guid Uuid,
    string Type,
    string? SourceType,
    string? SourceId);

public sealed record WhyStep(
    Guid Uuid,
    int Version,
    string Status,
    string SourceType,
    string? SourceId,
    Guid? Supersedes,
    TraceRecord? SourceTrace);

// The agent-facing retrieve_trace response. Distinct from TraceRecord: content
// is real JSON on the wire (not a double-encoded string) and the timestamp
// serializes as createdAt per the §8 camelCase wire convention.
public sealed record RetrievedTraceRecord(
    Guid TraceUuid,
    string SessionId,
    string AgentId,
    string Namespace,
    string EventType,
    JsonElement Content,
    Guid[]? Refs,
    DateTimeOffset CreatedAt);

public sealed class TraceRecord
{
    public Guid TraceUuid { get; set; }
    public string SessionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Content { get; set; } = "";
    public Guid[]? Refs { get; set; }
    public DateTimeOffset Ts { get; set; }
}
