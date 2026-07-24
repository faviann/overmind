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
    string SourceLocator,
    CaptureSource Source,
    CaptureAdapter Adapter,
    JsonElement SourcePayload,
    IReadOnlyList<CaptureEvent> Events);
public sealed record CaptureEventReceipt(Guid TraceUuid, string PartKey);
public sealed record CaptureReceipt(
    Guid ObservationUuid,
    string Status,
    string EffectiveNamespace,
    string RouteBasis,
    IReadOnlyList<CaptureEventReceipt> Events);
public sealed record CaptureReceiptRecord(
    Guid ObservationUuid,
    string StableName,
    string Harness,
    string SourceSessionId,
    string SourceLocator,
    string EffectiveNamespace,
    string RouteBasis,
    string Status,
    string SafeSourcePayload,
    DateTimeOffset CapturedAt,
    IReadOnlyList<CaptureEventReceipt> Events);
