using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemSrv.Core;

public sealed record CaptureSource(string Harness, string? HarnessVersion, string? RecordType);
public sealed record CaptureAdapter(string Name, string Version);
public sealed record CaptureLocator(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NativeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ByteOffset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ByteLength);
public sealed record CaptureSourceTimestamp(string Raw, DateTimeOffset? Parsed);
public sealed record CaptureRelationshipTarget(
    Guid? SourceStreamUuid,
    string NativeId,
    string? Kind);
public sealed record CaptureRelationship(string Type, CaptureRelationshipTarget Target);
public sealed record CaptureEvent(
    string PartKey,
    int PartOrder,
    string Kind,
    string Actor,
    JsonElement Payload,
    DateTimeOffset? OccurredAt,
    IReadOnlyList<CaptureRelationship>? Relationships);
public sealed record CaptureObservationRequest(
    int ContractVersion,
    string SourceSessionId,
    long SourcePosition,
    CaptureLocator Locator,
    CaptureSourceTimestamp? SourceTimestamp,
    CaptureSource Source,
    CaptureAdapter Adapter,
    JsonElement SourcePayload,
    IReadOnlyList<CaptureEvent> Events);
public sealed record CaptureScanReceipt(
    string Status,
    string RuleSetVersion,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> Categories,
    int RedactionCount);
public sealed record CaptureObservationReceipt(
    Guid ObservationUuid,
    Guid SourceStreamUuid,
    CaptureSource Source,
    CaptureLocator Locator,
    CaptureSourceTimestamp? SourceTimestamp,
    CaptureAdapter Adapter,
    JsonElement SafeSourcePayload,
    CaptureScanReceipt Scan,
    DateTimeOffset CapturedAt);
public sealed record CaptureEventReceipt(
    Guid TraceUuid,
    string SessionId,
    string AgentId,
    string Namespace,
    string PartKey,
    int PartOrder,
    string Kind,
    string Actor,
    DateTimeOffset? OccurredAt,
    int PayloadVersion,
    JsonElement Payload,
    IReadOnlyList<CaptureRelationship> Relationships);
public sealed record CanonicalCapturedEvent(
    Guid TraceUuid,
    string SessionId,
    string AgentId,
    string Namespace,
    string PartKey,
    int PartOrder,
    string Kind,
    string Actor,
    DateTimeOffset? OccurredAt,
    int PayloadVersion,
    JsonElement Payload);
public sealed record CapturedEventEnvelope(
    int ContractVersion,
    CaptureObservationReceipt Observation,
    CanonicalCapturedEvent Event,
    IReadOnlyList<CaptureRelationship> Relationships);
public sealed record CaptureReceipt(
    Guid ObservationUuid,
    string Status,
    long SourcePosition,
    string EffectiveNamespace,
    string RouteBasis,
    CaptureObservationReceipt Observation,
    IReadOnlyList<CaptureEventReceipt> Events);
public sealed record CaptureReceiptRecord(
    Guid ObservationUuid,
    string StableName,
    string Harness,
    string SourceSessionId,
    long SourcePosition,
    string EffectiveNamespace,
    string RouteBasis,
    string Status,
    string ScanStatus,
    string ScanRuleSetVersion,
    IReadOnlyList<string> ScanRuleIds,
    IReadOnlyList<string> ScanCategories,
    int ScanRedactionCount,
    string SafeSourcePayload,
    DateTimeOffset CapturedAt,
    CaptureObservationReceipt Observation,
    IReadOnlyList<CaptureEventReceipt> Events);
