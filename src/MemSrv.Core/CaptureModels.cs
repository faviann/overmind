using System.Text.Json;

namespace MemSrv.Core;

public sealed record CaptureSource(string Harness, string? HarnessVersion, string? RecordType);
public sealed record CaptureAdapter(string Name, string Version);
public sealed record CaptureRelationship(string Type, string TargetNativeId, string? TargetKind);
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
    string SourceLocator,
    CaptureSource Source,
    CaptureAdapter Adapter,
    JsonElement SourcePayload,
    IReadOnlyList<CaptureEvent> Events);
public sealed record CaptureEventReceipt(
    Guid TraceUuid,
    string PartKey,
    IReadOnlyList<CaptureRelationship> Relationships);
public sealed record CaptureReceipt(
    Guid ObservationUuid,
    string Status,
    long SourcePosition,
    string EffectiveNamespace,
    string RouteBasis,
    IReadOnlyList<CaptureEventReceipt> Events);
public sealed record CaptureReceiptRecord(
    Guid ObservationUuid,
    string StableName,
    string Harness,
    string SourceSessionId,
    long SourcePosition,
    string SourceLocator,
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
    IReadOnlyList<CaptureEventReceipt> Events);
