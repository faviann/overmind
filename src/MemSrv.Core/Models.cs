using System.Text.Json.Serialization;
using System.Text.Json;

namespace MemSrv.Core;

public sealed record ToolEnvelope<T>(
    [property: JsonPropertyName("data")] T Data,
    [property: JsonPropertyName("next")] string Next);

public sealed record TraceResult(Guid TraceUuid);

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

public sealed record ConsumedEntry(
    DateTimeOffset Ts,
    Guid MemoryUuid,
    string Type,
    string SourceType,
    string? SourceId);

public sealed record WhyStep(
    Guid Uuid,
    int Version,
    string Status,
    string SourceType,
    string? SourceId,
    Guid? Supersedes,
    TraceRecord? SourceTrace);

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
