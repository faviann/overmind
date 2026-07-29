using System.Text;
using System.Text.Json;

namespace MemSrv.Core;

/// <summary>
/// Versioned whole-observation fidelity outcomes shared by the local capture
/// runtime and canonical ingestion.
/// </summary>
public static class CaptureFidelityPolicy
{
    public const string CurrentVersion = "capture-fidelity/2026-07-29.2";
    public const int ProductionTransportBytes = 1_000_000;
    public const string TransportLimitReason = "observation_exceeds_transport_limit";
    public const string ContentLimitReason = "observation_exceeds_content_limit";

    public static CaptureObservationRequest ApplyTransportLimit(
        CaptureObservationRequest observation,
        long originalByteCount,
        int maxTransportBytes = ProductionTransportBytes)
    {
        if (originalByteCount <= maxTransportBytes)
        {
            return observation;
        }

        CaptureObservationRequest omitted = Omit(
            observation, TransportLimitReason, originalByteCount);
        int omittedByteCount = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(omitted, CaptureLedger.JsonOptions));
        if (omittedByteCount > maxTransportBytes)
        {
            throw new InvalidOperationException(
                "The required capture source identity and locator cannot fit " +
                $"within the {maxTransportBytes}-byte transport limit " +
                $"({omittedByteCount} bytes required).");
        }
        return omitted;
    }

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
            SourceTimestamp = null,
            Source = CompactSource(observation.Source.Harness),
            Adapter = CompactAdapter(),
            SourcePayload = provenance,
            Events = [OmissionEvent()],
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
            SourceSessionId = null,
            SourceTimestamp = null,
            Source = CompactSource(observation.Source.Harness),
            Adapter = CompactAdapter(),
            SourcePayload = provenance,
            Events = [OmissionEvent()],
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

    private static CaptureSource CompactSource(string harness) =>
        new(harness, null, null, null, null, null);

    private static CaptureAdapter CompactAdapter() =>
        new("capture-fidelity-policy", CurrentVersion);

    private static CaptureEvent OmissionEvent() =>
        new(
            "observation/omitted",
            0,
            "opaque",
            "harness",
            JsonSerializer.SerializeToElement(
                new { },
                CaptureLedger.JsonOptions),
            null,
            []);
}
