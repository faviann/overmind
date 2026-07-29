using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MemSrv.Core;

/// <summary>
/// Versioned whole-observation fidelity outcomes shared by the local capture
/// runtime and canonical ingestion.
/// </summary>
public static class CaptureFidelityPolicy
{
    public const string CurrentVersion = "capture-fidelity/2026-07-29.3";
    public const int ProductionTransportBytes = 1_000_000;
    public const string TransportLimitReason = "observation_exceeds_transport_limit";
    public const string ContentLimitReason = "observation_exceeds_content_limit";

    public static BoundedCaptureRepresentation<CaptureObservationRequest>
        SerializeForTransport(
        CaptureObservationRequest observation,
        int maxTransportBytes = ProductionTransportBytes)
    {
        if (maxTransportBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTransportBytes),
                maxTransportBytes,
                "The transport bound must be positive.");
        }

        int effectiveBound = Math.Min(
            maxTransportBytes,
            ProductionTransportBytes);
        CaptureObservationCommand validated =
            CaptureObservationCommand.FromRequest(observation);
        return SerializeWithinLimit(
            observation,
            effectiveBound,
            (request, originalByteCount) => OmitForTransport(
                request,
                originalByteCount,
                validated.SourceIdentity),
            omittedByteCount => new InvalidOperationException(
                "The required capture source identity and locator cannot fit " +
                $"within the {effectiveBound}-byte transport limit " +
                $"({omittedByteCount} bytes required)."));
    }

    public static BoundedCaptureRepresentation<CaptureObservationCommand>
        SerializeForContent(
        CaptureObservationCommand observation,
        long maxContentBytes)
    {
        if (maxContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxContentBytes),
                maxContentBytes,
                "The content bound must be positive.");
        }

        return SerializeWithinLimit(
            observation,
            maxContentBytes,
            OmitForContentLimit,
            _ => new SafetyScanException(
                $"the observation budget of {maxContentBytes} bytes was exceeded"));
    }

    private static CaptureObservationCommand OmitForContentLimit(
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

    private static CaptureObservationRequest OmitForTransport(
        CaptureObservationRequest observation,
        long originalByteCount,
        CaptureSourceIdentity canonicalIdentity)
    {
        JsonElement provenance = Provenance(
            TransportLimitReason,
            originalByteCount,
            canonicalIdentity,
            observation.SourcePosition,
            observation.Locator.Kind);
        return observation with
        {
            SourceSessionId = null,
            SourceIdentity = canonicalIdentity,
            SourceTimestamp = null,
            Source = CompactSource(observation.Source.Harness),
            Adapter = CompactAdapter(),
            SourcePayload = provenance,
            Events = [OmissionEvent()],
            RouteEvidence = null
        };
    }

    private static BoundedCaptureRepresentation<T> SerializeWithinLimit<T>(
        T observation,
        long byteLimit,
        Func<T, long, T> omit,
        Func<long, Exception> compactOverflow)
    {
        long originalByteCount = CountSerializedBytes(observation);
        if (originalByteCount <= byteLimit)
        {
            string originalJson = JsonSerializer.Serialize(
                observation,
                CaptureLedger.JsonOptions);
            return new(
                observation,
                originalJson,
                originalByteCount,
                WasOmitted: false);
        }

        T omitted = omit(observation, originalByteCount);
        string omittedJson = JsonSerializer.Serialize(
            omitted,
            CaptureLedger.JsonOptions);
        long omittedByteCount = Encoding.UTF8.GetByteCount(omittedJson);
        if (omittedByteCount > byteLimit)
        {
            throw compactOverflow(omittedByteCount);
        }

        return new(
            omitted,
            omittedJson,
            originalByteCount,
            WasOmitted: true);
    }

    private static long CountSerializedBytes<T>(T observation)
    {
        using var counter = new SerializationCountingStream(
            SafetyBudgets.Default.MaxScanTime);
        JsonSerializer.Serialize(
            counter,
            observation,
            CaptureLedger.JsonOptions);
        counter.AssertWithinDeadline();
        return counter.BytesWritten;
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

    private sealed class SerializationCountingStream(TimeSpan deadline) : Stream
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public void AssertWithinDeadline()
        {
            if (_clock.Elapsed > deadline)
            {
                throw new SafetyScanException(
                    "capture serialization exceeded the governed " +
                    $"{deadline.TotalSeconds:0}-second deadline");
            }
        }

        public override void Flush() => AssertWithinDeadline();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("The buffer range is invalid.");
            }
            Add(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);

        public override void WriteByte(byte value) => Add(1);

        private void Add(int count)
        {
            AssertWithinDeadline();
            BytesWritten = checked(BytesWritten + count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }
}

public sealed record BoundedCaptureRepresentation<T>(
    T Observation,
    string Serialized,
    long OriginalByteCount,
    bool WasOmitted);
