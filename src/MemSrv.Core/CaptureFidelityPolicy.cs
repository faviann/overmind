using System.Text.Json;

namespace MemSrv.Core;

/// <summary>
/// Versioned whole-observation fidelity outcomes shared by the local capture
/// runtime and canonical ingestion.
/// </summary>
public static class CaptureFidelityPolicy
{
    public const string CurrentVersion = "capture-fidelity/2026-07-29.1";
    public const int ProductionTransportBytes = 1_000_000;
    public const string TransportLimitReason = "observation_exceeds_transport_limit";
    public const string ContentLimitReason = "observation_exceeds_content_limit";

    public static CaptureObservationRequest ApplyTransportLimit(
        CaptureObservationRequest observation,
        long originalByteCount,
        int maxTransportBytes = ProductionTransportBytes) =>
        originalByteCount > maxTransportBytes
            ? Omit(observation, TransportLimitReason, originalByteCount)
            : observation;

    public static CaptureObservationCommand OmitForContentLimit(
        CaptureObservationCommand observation,
        long originalByteCount)
    {
        JsonElement provenance = Provenance(
            ContentLimitReason,
            originalByteCount,
            observation.SourceIdentity,
            observation.SourcePosition,
            observation.Locator.Kind);
        return observation with
        {
            SourcePayload = provenance,
            Events = [OmissionEvent(provenance)],
            RouteEvidence = null
        };
    }

    private static CaptureObservationRequest Omit(
        CaptureObservationRequest observation,
        string reason,
        long originalByteCount)
    {
        JsonElement provenance = Provenance(
            reason,
            originalByteCount,
            observation.SourceIdentity
                ?? new CaptureSourceIdentity(observation.SourceSessionId ?? ""),
            observation.SourcePosition,
            observation.Locator.Kind);
        return observation with
        {
            SourcePayload = provenance,
            Events = [OmissionEvent(provenance)],
            RouteEvidence = null
        };
    }

    private static JsonElement Provenance(
        string reason,
        long originalByteCount,
        CaptureSourceIdentity source,
        long sourcePosition,
        string locatorKind) =>
        JsonSerializer.SerializeToElement(
            new
            {
                omission = new
                {
                    reason,
                    originalByteCount,
                    policyVersion = CurrentVersion,
                    sourceIdentity = new
                    {
                        externalSessionId = source.ExternalSessionId,
                        childId = source.ChildId,
                        sourcePosition,
                        locatorKind
                    }
                }
            },
            CaptureLedger.JsonOptions);

    private static CaptureEvent OmissionEvent(JsonElement provenance) =>
        new(
            "observation/omitted",
            0,
            "opaque",
            "harness",
            provenance,
            null,
            []);
}
