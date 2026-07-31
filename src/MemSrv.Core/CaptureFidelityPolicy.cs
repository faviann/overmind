using System.Text;
using System.Text.Json;

namespace MemSrv.Core;

/// <summary>
/// Versioned whole-observation fidelity outcomes shared by the local capture
/// runtime and canonical ingestion.
/// </summary>
public static class CaptureFidelityPolicy
{
    public const string CurrentVersion = "capture-fidelity/2026-07-31.11";
    public const int ProductionTransportBytes = 1_000_000;
    public const string TransportLimitReason = "observation_exceeds_transport_limit";
    public const string ContentLimitReason = "observation_exceeds_content_limit";
    public const string UnsupportedBinaryReason = "unsupported_binary_content";
    public const string MalformedJsonReason = "json_parse_error";
    public const string UninspectableSourceRecordReason = "source_record_uninspectable";
    public const string InvalidUtf8ContentPolicy = "invalid_utf8";
    public const string BinaryOmissionField = "capture_fidelity_omission";

    private static readonly HashSet<string> UnsupportedBinaryCategories =
        new(StringComparer.Ordinal)
        {
            "attachment",
            "archive",
            "executable",
            "image",
            "audio"
        };

    /// <summary>
    /// Replaces only the byte-bearing field of the explicit adapter-owned
    /// <c>binary_content</c> tagged union. Metadata and model-visible text stay
    /// ordinary source evidence. Strings and untagged arrays are deliberately
    /// not classified.
    /// </summary>
    public static BinaryFidelitySelection<JsonElement>
        OmitUnsupportedBinaryContent(
            JsonElement sourcePayload,
            string harness,
            CaptureSourceIdentity sourceIdentity,
            long sourcePosition,
            string? locatorKind,
            long maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes), maxBytes, "The binary fidelity bound must be positive.");
        }

        long effectiveBound = Math.Min(
            maxBytes,
            SafetyBudgets.Default.MaxObservationBytes);
        var deadline = new GovernedDeadline(SafetyBudgets.Default.MaxScanTime);
        RewrittenJson rewritten = RewriteUnsupportedBinaryContent(
            sourcePayload,
            harness,
            sourceIdentity,
            sourcePosition,
            locatorKind,
            effectiveBound,
            TrustedTraversalRootContext.RawSource,
            deadline);
        if (rewritten.ExceededBound)
        {
            long originalByteCount = CountSerializedBytes(sourcePayload, deadline);
            JsonElement omitted = MaterializeUnsupportedBinaryProvenance(
                originalByteCount,
                sourceIdentity,
                sourcePosition,
                locatorKind,
                effectiveBound,
                deadline);
            return new(omitted, OmissionCount: 1);
        }
        return new(rewritten.Value, rewritten.OmissionCount);
    }

    /// <summary>
    /// Selects one binary-safe adapter request under one absolute deadline.
    /// The source and every derived event are rewritten together; if their safe
    /// field-level representation grows beyond the transport ceiling, only a
    /// compact whole-observation omission may be returned.
    /// </summary>
    public static BinaryFidelitySelection<CaptureObservationRequest>
        OmitUnsupportedBinaryContent(
            CaptureObservationRequest observation,
            int maxTransportBytes = ProductionTransportBytes)
    {
        if (maxTransportBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTransportBytes),
                maxTransportBytes,
                "The binary fidelity bound must be positive.");
        }

        int effectiveBound = Math.Min(
            maxTransportBytes,
            ProductionTransportBytes);
        CaptureObservationCommand validated =
            CaptureObservationCommand.FromRequest(observation);
        var deadline = new GovernedDeadline(SafetyBudgets.Default.MaxScanTime);
        RewrittenJson rewrittenSource = RewriteUnsupportedBinaryContent(
            observation.SourcePayload,
            observation.Source.Harness,
            validated.SourceIdentity,
            observation.SourcePosition,
            observation.Locator.Kind,
            effectiveBound,
            TrustedTraversalRootContext.RawSource,
            deadline);
        if (rewrittenSource.ExceededBound)
        {
            return OmitWholeRequestForUnsupportedBinary(
                observation,
                validated,
                effectiveBound,
                deadline);
        }

        long remaining = effectiveBound - rewrittenSource.SerializedBytes;
        int omissionCount = rewrittenSource.OmissionCount;
        var events = new CaptureEvent[observation.Events.Count];
        for (int index = 0; index < observation.Events.Count; index++)
        {
            CaptureEvent item = observation.Events[index];
            if (remaining <= 0)
            {
                return OmitWholeRequestForUnsupportedBinary(
                    observation,
                    validated,
                    effectiveBound,
                    deadline);
            }
            RewrittenJson rewrittenPayload = RewriteUnsupportedBinaryContent(
                item.Payload,
                observation.Source.Harness,
                validated.SourceIdentity,
                observation.SourcePosition,
                observation.Locator.Kind,
                remaining,
                IsAdapterOwnedCodexReasoningEnvelope(validated, item)
                    ? TrustedTraversalRootContext.AdapterEvent
                    : TrustedTraversalRootContext.None,
                deadline);
            if (rewrittenPayload.ExceededBound)
            {
                return OmitWholeRequestForUnsupportedBinary(
                    observation,
                    validated,
                    effectiveBound,
                    deadline);
            }
            remaining -= rewrittenPayload.SerializedBytes;
            omissionCount += rewrittenPayload.OmissionCount;
            events[index] = item with { Payload = rewrittenPayload.Value };
        }

        if (omissionCount == 0)
        {
            return new(observation, 0);
        }

        CaptureObservationRequest selected = observation with
        {
            SourcePayload = rewrittenSource.Value,
            Events = events
        };
        if (CountSerializedBytes(selected, deadline) > effectiveBound)
        {
            return OmitWholeRequestForUnsupportedBinary(
                observation,
                validated,
                effectiveBound,
                deadline);
        }
        deadline.AssertWithinDeadline();
        return new(selected, omissionCount);
    }

    public static BinaryFidelitySelection<CaptureObservationCommand>
        OmitUnsupportedBinaryContent(
            CaptureObservationCommand observation,
            long maxContentBytes)
    {
        if (maxContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxContentBytes),
                maxContentBytes,
                "The binary fidelity bound must be positive.");
        }

        long effectiveBound = Math.Min(
            maxContentBytes,
            SafetyBudgets.Default.MaxObservationBytes);
        long remaining = effectiveBound;
        var deadline = new GovernedDeadline(SafetyBudgets.Default.MaxScanTime);
        RewrittenJson rewrittenSource = RewriteUnsupportedBinaryContent(
            observation.SourcePayload,
            observation.Source.Harness,
            observation.SourceIdentity,
            observation.SourcePosition,
            observation.Locator.Kind,
            remaining,
            TrustedTraversalRootContext.RawSource,
            deadline);
        if (rewrittenSource.ExceededBound)
        {
            return OmitWholeObservationForUnsupportedBinary(
                observation,
                effectiveBound,
                deadline);
        }
        remaining -= rewrittenSource.SerializedBytes;
        int omissionCount = rewrittenSource.OmissionCount;
        var events = new CaptureEvent[observation.Events.Count];
        for (int index = 0; index < observation.Events.Count; index++)
        {
            CaptureEvent item = observation.Events[index];
            if (remaining <= 0)
            {
                return OmitWholeObservationForUnsupportedBinary(
                    observation,
                    effectiveBound,
                    deadline);
            }
            RewrittenJson rewrittenPayload = RewriteUnsupportedBinaryContent(
                item.Payload,
                observation.Source.Harness,
                observation.SourceIdentity,
                observation.SourcePosition,
                observation.Locator.Kind,
                remaining,
                IsAdapterOwnedCodexReasoningEnvelope(observation, item)
                    ? TrustedTraversalRootContext.AdapterEvent
                    : TrustedTraversalRootContext.None,
                deadline);
            if (rewrittenPayload.ExceededBound)
            {
                return OmitWholeObservationForUnsupportedBinary(
                    observation,
                    effectiveBound,
                    deadline);
            }
            remaining -= rewrittenPayload.SerializedBytes;
            omissionCount += rewrittenPayload.OmissionCount;
            events[index] = item with { Payload = rewrittenPayload.Value };
        }

        if (omissionCount == 0)
        {
            return new(observation, 0);
        }

        CaptureObservationCommand selected = observation with
        {
            SourcePayload = rewrittenSource.Value,
            Events = events
        };
        if (CountSerializedBytes(selected, deadline) > effectiveBound)
        {
            return OmitWholeObservationForUnsupportedBinary(
                observation,
                effectiveBound,
                deadline);
        }
        deadline.AssertWithinDeadline();
        return new(selected, omissionCount);
    }

    /// <summary>
    /// Reports whether a command contains this policy's exact unsupported-binary
    /// omission representation. Callers do not interpret policy-owned JSON.
    /// </summary>
    public static bool ContainsUnsupportedBinaryOmission(
        CaptureObservationCommand observation) =>
        UnsupportedBinaryOmissionByteCounts(observation).Count > 0;

    /// <summary>
    /// Returns the safe original byte count from every exact policy-owned
    /// unsupported-binary omission in a command.
    /// </summary>
    public static IReadOnlyList<long> UnsupportedBinaryOmissionByteCounts(
        CaptureObservationCommand observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var counts = new List<long>();
        CollectUnsupportedBinaryOmissionByteCounts(
            observation.SourcePayload,
            observation.SourceIdentity,
            observation.SourcePosition,
            observation.Locator.Kind,
            counts);
        foreach (CaptureEvent item in observation.Events)
        {
            CollectUnsupportedBinaryOmissionByteCounts(
                item.Payload,
                observation.SourceIdentity,
                observation.SourcePosition,
                observation.Locator.Kind,
                counts);
        }
        return counts;
    }

    /// <summary>
    /// Reconstructs the same exact policy-owned omission counts from canonical
    /// observation and event payloads. Canonical observations do not expose
    /// source position, so the policy marker's non-negative position remains
    /// trusted through immutable scan provenance.
    /// </summary>
    public static IReadOnlyList<long> UnsupportedBinaryOmissionByteCounts(
        CaptureObservationReceipt observation,
        IEnumerable<JsonElement> eventPayloads)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(eventPayloads);
        var counts = new List<long>();
        CollectUnsupportedBinaryOmissionByteCounts(
            observation.SafeSourcePayload,
            observation.SourceIdentity,
            sourcePosition: null,
            observation.Locator.Kind,
            counts);
        foreach (JsonElement payload in eventPayloads)
        {
            CollectUnsupportedBinaryOmissionByteCounts(
                payload,
                observation.SourceIdentity,
                sourcePosition: null,
                observation.Locator.Kind,
                counts);
        }
        return counts;
    }

    /// <summary>
    /// Reports whether a command is exactly one of the adapter-owned v10
    /// terminal-record fidelity representations. The source discriminator
    /// alone is source-owned and therefore insufficient.
    /// </summary>
    public static bool IsAdapterOwnedTerminalMalformedRepresentation(
        CaptureObservationCommand observation)
    {
        if (observation.ContractVersion != 1
            || !string.Equals(observation.Source.Harness, "codex", StringComparison.Ordinal)
            || !string.Equals(
                observation.Adapter.Name,
                "codex-synthetic-jsonl",
                StringComparison.Ordinal)
            || !string.Equals(observation.Adapter.Version, "10", StringComparison.Ordinal)
            || observation.Locator is not CaptureSourceLocator.ByteRange
            || observation.SourceTimestamp is not null
            || observation.RouteEvidence is not null
            || observation.Source.HarnessVersion is not null
            || observation.Source.Model is not null
            || observation.Source.Provider is not null
            || !string.Equals(
                observation.Source.MaterialKind,
                "persisted_record",
                StringComparison.Ordinal)
            || observation.Events is not [CaptureEvent terminalEvent]
            || !string.Equals(terminalEvent.PartKey, "record:opaque", StringComparison.Ordinal)
            || terminalEvent.PartOrder != 0
            || !string.Equals(terminalEvent.Kind, "opaque", StringComparison.Ordinal)
            || !string.Equals(terminalEvent.Actor, "unknown", StringComparison.Ordinal)
            || terminalEvent.OccurredAt is not null
            || terminalEvent.Relationships is not { Count: 0 }
            || !IsExactTerminalEventProjection(
                terminalEvent.Payload,
                observation.Source.RecordType,
                observation.SourcePayload))
        {
            return false;
        }

        return observation.Source.RecordType switch
        {
            "malformed_json" =>
                IsMalformedJsonRepresentation(observation.SourcePayload, observation),
            "source_record_omission" =>
                IsUninspectableRecordRepresentation(observation.SourcePayload, observation),
            _ => false
        };
    }

    /// <summary>
    /// Classifies only exact policy- or adapter-owned deterministic fidelity
    /// representations. Source-owned lookalikes are not outcomes.
    /// </summary>
    public static CaptureDeterministicFidelity? ClassifyDeterministicFidelity(
        CaptureObservationCommand observation)
    {
        if (IsAdapterOwnedTerminalMalformedRepresentation(observation))
        {
            return observation.Source.RecordType switch
            {
                "malformed_json" => new(
                    MalformedJsonReason,
                    ((CaptureSourceLocator.ByteRange)observation.Locator).Length),
                "source_record_omission" => new(
                    InvalidUtf8ContentPolicy,
                    ((CaptureSourceLocator.ByteRange)observation.Locator).Length),
                _ => null
            };
        }
        if (ContainsUnsupportedBinaryOmission(observation))
        {
            return new(
                UnsupportedBinaryReason,
                observation.Locator is CaptureSourceLocator.ByteRange binaryRange
                    ? binaryRange.Length
                    : CountSerializedBytes(observation));
        }
        if (!string.Equals(
                observation.Adapter.Name,
                "capture-fidelity-policy",
                StringComparison.Ordinal)
            || !string.Equals(
                observation.Adapter.Version,
                CurrentVersion,
                StringComparison.Ordinal)
            || observation.SourcePayload.ValueKind != JsonValueKind.Object
            || !observation.SourcePayload.TryGetProperty(
                "omission",
                out JsonElement omission)
            || !HasOnlyProperties(
                omission,
                "reason",
                "originalByteCount",
                "policyVersion",
                "sourceIdentity")
            || omission.GetProperty("reason").GetString() is not { } reason
            || reason is not (
                TransportLimitReason
                or ContentLimitReason
                or UnsupportedBinaryReason)
            || !omission.TryGetProperty(
                "originalByteCount",
                out JsonElement originalByteCount)
            || !originalByteCount.TryGetInt64(out long count)
            || count < 0
            || !HasString(omission, "policyVersion", CurrentVersion)
            || !omission.TryGetProperty(
                "sourceIdentity",
                out JsonElement sourceIdentity)
            || !HasTrustedSourceIdentity(
                sourceIdentity,
                observation.SourceIdentity,
                observation.SourcePosition,
                observation.Locator.Kind))
        {
            return null;
        }
        return new(reason, count);
    }

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
        BoundedCaptureRepresentation<CaptureObservationRequest> bounded =
            SerializeWithinLimit(
                observation,
                effectiveBound,
                (request, originalByteCount) =>
                    validated.Locator is CaptureSourceLocator.NativeId
                        ? throw new InvalidOperationException(
                            "An over-limit native_id observation cannot fit safely and " +
                            "fails closed because transport omission requires " +
                            "binding-stable content identity.")
                        : OmitForTransport(
                            request,
                            originalByteCount,
                            validated.SourceIdentity),
                omittedByteCount => new InvalidOperationException(
                    "The required capture source identity and locator cannot fit " +
                    $"within the {effectiveBound}-byte transport limit " +
                    $"({omittedByteCount} bytes required)."));
        CaptureObservationRequest snapshot =
            JsonSerializer.Deserialize<CaptureObservationRequest>(
                bounded.Serialized,
                CaptureLedger.JsonOptions)
            ?? throw new InvalidOperationException(
                "The bounded transport representation could not be reconstructed.");
        return bounded with { Observation = snapshot };
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

        long effectiveBound = Math.Min(
            maxContentBytes,
            SafetyBudgets.Default.MaxObservationBytes);
        BoundedCaptureRepresentation<CaptureObservationCommand> bounded =
            SerializeWithinLimit(
            observation,
            effectiveBound,
            (command, originalByteCount) =>
                OmitForContentLimit(command, originalByteCount),
            _ => new SafetyScanException(
                $"the observation budget of {effectiveBound} bytes was exceeded"));
        CaptureObservationRequest snapshot =
            JsonSerializer.Deserialize<CaptureObservationRequest>(
                bounded.Serialized,
                CaptureLedger.JsonOptions)
            ?? throw new SafetyScanException(
                "the bounded capture representation could not be reconstructed");
        return bounded with
        {
            Observation = CaptureObservationCommand.FromRequest(snapshot)
        };
    }

    private static CaptureObservationCommand OmitForContentLimit(
        CaptureObservationCommand observation,
        long originalByteCount,
        string reason = ContentLimitReason)
    {
        JsonElement provenance = Provenance(
            reason,
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
        CaptureSourceIdentity canonicalIdentity,
        string reason = TransportLimitReason)
    {
        JsonElement provenance = Provenance(
            reason,
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
            long materializedByteCount = Encoding.UTF8.GetByteCount(originalJson);
            if (materializedByteCount <= byteLimit)
            {
                return new(
                    observation,
                    originalJson,
                    materializedByteCount,
                    WasOmitted: false);
            }

            originalByteCount = materializedByteCount;
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
        var deadline = new GovernedDeadline(SafetyBudgets.Default.MaxScanTime);
        return CountSerializedBytes(observation, deadline);
    }

    private static long CountSerializedBytes<T>(
        T observation,
        GovernedDeadline deadline)
    {
        using var counter = new CountingSerializationStream(deadline);
        JsonSerializer.Serialize(
            counter,
            observation,
            CaptureLedger.JsonOptions);
        counter.AssertWithinDeadline();
        return counter.BytesWritten;
    }

    private static BinaryFidelitySelection<CaptureObservationCommand>
        OmitWholeObservationForUnsupportedBinary(
            CaptureObservationCommand observation,
            long effectiveBound,
            GovernedDeadline deadline)
    {
        long originalByteCount = CountSerializedBytes(observation, deadline);
        CaptureObservationCommand omitted = OmitForContentLimit(
            observation,
            originalByteCount,
            UnsupportedBinaryReason);
        long omittedByteCount = CountSerializedBytes(omitted, deadline);
        if (omittedByteCount > effectiveBound)
        {
            throw new SafetyScanException(
                "the required unsupported-binary omission cannot fit within " +
                $"the observation budget of {effectiveBound} bytes");
        }
        return new(omitted, OmissionCount: 1);
    }

    private static BinaryFidelitySelection<CaptureObservationRequest>
        OmitWholeRequestForUnsupportedBinary(
            CaptureObservationRequest observation,
            CaptureObservationCommand validated,
            int effectiveBound,
            GovernedDeadline deadline)
    {
        if (validated.Locator is CaptureSourceLocator.NativeId)
        {
            throw new InvalidDataException(
                "A native_id Codex record with unsupported binary content fails closed: " +
                UnsupportedBinaryReason + ".");
        }

        long originalByteCount = CountSerializedBytes(observation, deadline);
        CaptureObservationRequest omitted = OmitForTransport(
            observation,
            originalByteCount,
            validated.SourceIdentity,
            UnsupportedBinaryReason);
        long omittedByteCount = CountSerializedBytes(omitted, deadline);
        if (omittedByteCount > effectiveBound)
        {
            throw new InvalidOperationException(
                "The required capture source identity and locator cannot fit " +
                $"within the {effectiveBound}-byte transport limit.");
        }
        deadline.AssertWithinDeadline();
        return new(omitted, OmissionCount: 1);
    }

    private static JsonElement MaterializeUnsupportedBinaryProvenance(
        long originalByteCount,
        CaptureSourceIdentity sourceIdentity,
        long sourcePosition,
        string? locatorKind,
        long effectiveBound,
        GovernedDeadline deadline)
    {
        try
        {
            using var stream = new BoundedBufferSerializationStream(
                effectiveBound,
                deadline);
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("omission");
                writer.WriteStartObject();
                writer.WriteString("reason", UnsupportedBinaryReason);
                writer.WriteNumber("originalByteCount", originalByteCount);
                writer.WriteString("policyVersion", CurrentVersion);
                writer.WritePropertyName("sourceIdentity");
                writer.WriteStartObject();
                writer.WriteString(
                    "externalSessionId",
                    sourceIdentity.ExternalSessionId);
                if (sourceIdentity.ChildId is not null)
                {
                    writer.WriteString("childId", sourceIdentity.ChildId);
                }
                writer.WriteNumber("sourcePosition", sourcePosition);
                if (locatorKind is not null)
                {
                    writer.WriteString("locatorKind", locatorKind);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.Flush();
            }
            using JsonDocument document = JsonDocument.Parse(stream.WrittenMemory);
            JsonElement materialized = document.RootElement.Clone();
            deadline.AssertWithinDeadline();
            return materialized;
        }
        catch (CaptureRepresentationLimitException)
        {
            throw new SafetyScanException(
                "the required unsupported-binary omission cannot fit within " +
                $"the fidelity budget of {effectiveBound} bytes");
        }
    }

    private static bool IsAdapterOwnedCodexReasoningEnvelope(
        CaptureObservationCommand observation,
        CaptureEvent item) =>
        string.Equals(observation.Source.Harness, "codex", StringComparison.Ordinal)
        && string.Equals(
            observation.Source.RecordType,
            "response_item",
            StringComparison.Ordinal)
        && string.Equals(
            observation.Adapter.Name,
            "codex-synthetic-jsonl",
            StringComparison.Ordinal)
        && string.Equals(item.Kind, "opaque", StringComparison.Ordinal)
        && string.Equals(item.PartKey, "reasoning:opaque", StringComparison.Ordinal)
        && observation.SourcePayload.ValueKind == JsonValueKind.Object
        && HasString(observation.SourcePayload, "type", "response_item")
        && observation.SourcePayload.TryGetProperty(
            "payload",
            out JsonElement sourceReasoningPayload)
        && sourceReasoningPayload.ValueKind == JsonValueKind.Object
        && HasString(sourceReasoningPayload, "type", "reasoning")
        && item.Payload.ValueKind == JsonValueKind.Object
        && item.Payload.TryGetProperty("source", out JsonElement eventSource)
        && JsonElement.DeepEquals(sourceReasoningPayload, eventSource);

    private static RewrittenJson RewriteUnsupportedBinaryContent(
        JsonElement source,
        string harness,
        CaptureSourceIdentity sourceIdentity,
        long sourcePosition,
        string? locatorKind,
        long maxBytes,
        TrustedTraversalRootContext rootContext,
        GovernedDeadline deadline)
    {
        using (var candidatePass = new CountingSerializationStream(deadline))
        {
            int candidates = CountUnsupportedBinaryContent(
                source,
                harness,
                candidatePass,
                inKnownCodexReasoningPayload: false,
                rootContext);
            deadline.AssertWithinDeadline();
            if (candidates == 0)
            {
                return new(
                    source,
                    OmissionCount: 0,
                    SerializedBytes: 0,
                    ExceededBound: false);
            }
        }

        try
        {
            using var stream = new BoundedBufferSerializationStream(
                maxBytes,
                deadline);
            using (var writer = new Utf8JsonWriter(stream))
            {
                var state = new BinaryRewriteState(
                    stream,
                    harness,
                    sourceIdentity,
                    sourcePosition,
                    locatorKind,
                    rootContext);
                WriteRewritten(source, writer, state, knownOpaqueMetadata: false);
                writer.Flush();
                deadline.AssertWithinDeadline();
                using JsonDocument document = JsonDocument.Parse(stream.WrittenMemory);
                JsonElement materialized = document.RootElement.Clone();
                deadline.AssertWithinDeadline();
                return new(
                    materialized,
                    state.OmissionCount,
                    stream.BytesWritten,
                    ExceededBound: false);
            }
        }
        catch (CaptureRepresentationLimitException)
        {
            // The caller's ordinary transport/content serializer owns the
            // deterministic whole-observation omission for this case.
            return new(
                source,
                OmissionCount: 0,
                SerializedBytes: maxBytes,
                ExceededBound: true);
        }
    }

    private static int CountUnsupportedBinaryContent(
        JsonElement value,
        string harness,
        GovernedSerializationStream deadline,
        bool inKnownCodexReasoningPayload,
        TrustedTraversalRootContext rootContext)
    {
        deadline.AssertWithinDeadline();
        if (value.ValueKind == JsonValueKind.Array)
        {
            int arrayCount = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                arrayCount += CountUnsupportedBinaryContent(
                    item,
                    harness,
                    deadline,
                    inKnownCodexReasoningPayload: false,
                    TrustedTraversalRootContext.None);
            }
            return arrayCount;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        bool isBinary = TryUnsupportedBinary(
            value,
            deadline,
            out _,
            out _);
        bool isCodexResponseItem = string.Equals(
                harness, "codex", StringComparison.Ordinal)
            && rootContext == TrustedTraversalRootContext.RawSource
            && HasString(value, "type", "response_item");
        bool isCodexReasoningOpaqueEnvelope = string.Equals(
                harness, "codex", StringComparison.Ordinal)
            && rootContext == TrustedTraversalRootContext.AdapterEvent
            && HasString(value, "recordType", "response_item")
            && HasString(value, "payloadType", "reasoning");
        bool isCodexReasoningPayload = HasString(value, "type", "reasoning");
        int count = isBinary ? 1 : 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            deadline.AssertWithinDeadline();
            if (isBinary && property.NameEquals("byte_payload"))
            {
                continue;
            }
            if (isCodexReasoningPayload
                && inKnownCodexReasoningPayload
                && property.Name is "signature" or "encrypted_content")
            {
                continue;
            }
            bool childIsKnownReasoning =
                ((isCodexResponseItem && property.NameEquals("payload"))
                    || (isCodexReasoningOpaqueEnvelope
                        && property.NameEquals("source")))
                && property.Value.ValueKind == JsonValueKind.Object
                && HasString(property.Value, "type", "reasoning");
            count += CountUnsupportedBinaryContent(
                property.Value,
                harness,
                deadline,
                childIsKnownReasoning,
                TrustedTraversalRootContext.None);
        }
        return count;
    }

    private static void WriteRewritten(
        JsonElement value,
        Utf8JsonWriter writer,
        BinaryRewriteState state,
        bool knownOpaqueMetadata)
    {
        state.Stream.AssertWithinDeadline();
        if (knownOpaqueMetadata)
        {
            WriteOpaque(value, writer, state.Stream);
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(value, writer, state);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    state.Depth++;
                    WriteRewritten(item, writer, state, knownOpaqueMetadata: false);
                    state.Depth--;
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(
        JsonElement value,
        Utf8JsonWriter writer,
        BinaryRewriteState state)
    {
        bool isBinary = TryUnsupportedBinary(
            value,
            state.Stream,
            out string? category,
            out long originalByteCount);
        bool isCodexResponseItem = string.Equals(
                state.Harness, "codex", StringComparison.Ordinal)
            && state.Depth == 0
            && state.RootContext == TrustedTraversalRootContext.RawSource
            && HasString(value, "type", "response_item");
        bool isCodexReasoningOpaqueEnvelope = string.Equals(
                state.Harness, "codex", StringComparison.Ordinal)
            && state.Depth == 0
            && state.RootContext == TrustedTraversalRootContext.AdapterEvent
            && HasString(value, "recordType", "response_item")
            && HasString(value, "payloadType", "reasoning");
        bool isCodexReasoningPayload = HasString(value, "type", "reasoning");
        writer.WriteStartObject();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            state.Stream.AssertWithinDeadline();
            if (isBinary && property.NameEquals("byte_payload"))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            bool knownOpaqueMetadata =
                isCodexReasoningPayload
                && state.InCodexResponsePayload
                && property.Name is "signature" or "encrypted_content";
            bool priorContext = state.InCodexResponsePayload;
            state.InCodexResponsePayload =
                ((isCodexResponseItem && property.NameEquals("payload"))
                    || (isCodexReasoningOpaqueEnvelope
                        && property.NameEquals("source")))
                && property.Value.ValueKind == JsonValueKind.Object
                && HasString(property.Value, "type", "reasoning");
            state.Depth++;
            WriteRewritten(
                property.Value,
                writer,
                state,
                knownOpaqueMetadata);
            state.Depth--;
            state.InCodexResponsePayload = priorContext;
        }

        if (isBinary)
        {
            writer.WritePropertyName(NonCollidingOmissionField(value));
            WriteBinaryOmission(
                value,
                writer,
                category!,
                originalByteCount,
                state.SourceIdentity,
                state.SourcePosition,
                state.LocatorKind);
            state.OmissionCount++;
        }
        writer.WriteEndObject();
    }

    private static bool TryUnsupportedBinary(
        JsonElement value,
        GovernedSerializationStream stream,
        out string? category,
        out long byteCount)
    {
        category = null;
        byteCount = 0;
        int typeCount = 0;
        int categoryCount = 0;
        int bytePayloadCount = 0;
        JsonElement typeElement = default;
        JsonElement categoryElement = default;
        JsonElement bytes = default;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            stream.AssertWithinDeadline();
            if (property.NameEquals("type"))
            {
                typeCount++;
                typeElement = property.Value;
            }
            else if (property.NameEquals("category"))
            {
                categoryCount++;
                categoryElement = property.Value;
            }
            else if (property.NameEquals("byte_payload"))
            {
                bytePayloadCount++;
                bytes = property.Value;
            }
        }

        if (typeCount != 1
            || categoryCount != 1
            || bytePayloadCount != 1
            || typeElement.ValueKind != JsonValueKind.String
            || !string.Equals(
                typeElement.GetString(),
                "binary_content",
                StringComparison.Ordinal)
            || categoryElement.ValueKind != JsonValueKind.String
            || (category = categoryElement.GetString()) is null
            || !UnsupportedBinaryCategories.Contains(category)
            || bytes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in bytes.EnumerateArray())
        {
            stream.AssertWithinDeadline();
            if (!item.TryGetInt32(out int octet)
                || octet is < byte.MinValue or > byte.MaxValue)
            {
                return false;
            }
            byteCount++;
        }
        return true;
    }

    private static void WriteBinaryOmission(
        JsonElement source,
        Utf8JsonWriter writer,
        string category,
        long originalByteCount,
        CaptureSourceIdentity sourceIdentity,
        long sourcePosition,
        string? locatorKind)
    {
        writer.WriteStartObject();
        writer.WriteString("reason", UnsupportedBinaryReason);
        writer.WriteString("category", category);
        writer.WriteNumber("originalByteCount", originalByteCount);
        writer.WriteString("policyVersion", CurrentVersion);
        writer.WritePropertyName("sourceIdentity");
        writer.WriteStartObject();
        writer.WriteString(
            "externalSessionId",
            sourceIdentity.ExternalSessionId);
        if (sourceIdentity.ChildId is not null)
        {
            writer.WriteString("childId", sourceIdentity.ChildId);
        }
        writer.WriteNumber("sourcePosition", sourcePosition);
        if (locatorKind is not null)
        {
            writer.WriteString("locatorKind", locatorKind);
        }
        writer.WriteEndObject();
        CopySafeProvenance(source, writer, "media_type", "mediaType");
        CopySafeProvenance(source, writer, "source_path", "sourcePath");
        CopySafeProvenance(source, writer, "source_identity", "localSourceIdentity");
        writer.WriteEndObject();
    }

    private static void CopySafeProvenance(
        JsonElement source,
        Utf8JsonWriter writer,
        string sourceName,
        string omissionName)
    {
        if (source.TryGetProperty(sourceName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            writer.WriteString(omissionName, value.GetString());
        }
    }

    private static string NonCollidingOmissionField(JsonElement source)
    {
        string candidate = BinaryOmissionField;
        int suffix = 0;
        while (source.TryGetProperty(candidate, out _))
        {
            candidate = $"{BinaryOmissionField}_{++suffix}";
        }
        return candidate;
    }

    private static void CollectUnsupportedBinaryOmissionByteCounts(
        JsonElement value,
        CaptureSourceIdentity expectedIdentity,
        long? sourcePosition,
        string locatorKind,
        List<long> counts)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (IsPolicyOwnedBinaryOmissionField(
                            value,
                            property.Name,
                            property.Value,
                            expectedIdentity,
                            sourcePosition,
                            locatorKind))
                    {
                        counts.Add(property.Value.GetProperty(
                            "originalByteCount").GetInt64());
                    }
                    else
                    {
                        CollectUnsupportedBinaryOmissionByteCounts(
                            property.Value,
                            expectedIdentity,
                            sourcePosition,
                            locatorKind,
                            counts);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    CollectUnsupportedBinaryOmissionByteCounts(
                        item,
                        expectedIdentity,
                        sourcePosition,
                        locatorKind,
                        counts);
                }
                break;
        }
    }

    private static bool IsPolicyOwnedBinaryOmissionField(
        JsonElement parent,
        string name,
        JsonElement value,
        CaptureSourceIdentity expectedIdentity,
        long? sourcePosition,
        string locatorKind)
    {
        if (!IsUnsupportedBinaryOmission(
                value,
                expectedIdentity,
                sourcePosition,
                locatorKind))
        {
            return false;
        }
        if (string.Equals(name, BinaryOmissionField, StringComparison.Ordinal))
        {
            return true;
        }

        string prefix = BinaryOmissionField + "_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(
                name.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int suffix)
            || suffix <= 0
            || !string.Equals(
                suffix.ToString(System.Globalization.CultureInfo.InvariantCulture),
                name[prefix.Length..],
                StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = 0; index < suffix; index++)
        {
            string occupied = index == 0
                ? BinaryOmissionField
                : $"{BinaryOmissionField}_{index}";
            if (!parent.TryGetProperty(occupied, out _))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsUnsupportedBinaryOmission(
        JsonElement value,
        CaptureSourceIdentity expectedIdentity,
        long? sourcePosition,
        string locatorKind)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !HasOnlyBinaryOmissionProperties(value)
            || !HasString(value, "reason", UnsupportedBinaryReason)
            || !value.TryGetProperty("category", out JsonElement category)
            || category.ValueKind != JsonValueKind.String
            || category.GetString() is not { } categoryName
            || !UnsupportedBinaryCategories.Contains(categoryName)
            || !value.TryGetProperty("originalByteCount", out JsonElement byteCount)
            || !byteCount.TryGetInt64(out long count)
            || count < 0
            || !HasString(value, "policyVersion", CurrentVersion)
            || !value.TryGetProperty("sourceIdentity", out JsonElement sourceIdentity)
            || sourceIdentity.ValueKind != JsonValueKind.Object
            || !HasTrustedSourceIdentity(
                sourceIdentity,
                expectedIdentity,
                sourcePosition,
                locatorKind))
        {
            return false;
        }

        return HasOptionalNonBlankString(value, "mediaType")
            && HasOptionalNonBlankString(value, "sourcePath")
            && HasOptionalNonBlankString(value, "localSourceIdentity");
    }

    private static bool HasOnlyBinaryOmissionProperties(JsonElement value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || property.Name is not (
                    "reason"
                    or "category"
                    or "originalByteCount"
                    or "policyVersion"
                    or "sourceIdentity"
                    or "mediaType"
                    or "sourcePath"
                    or "localSourceIdentity"))
            {
                return false;
            }
        }
        return seen.Contains("reason")
            && seen.Contains("category")
            && seen.Contains("originalByteCount")
            && seen.Contains("policyVersion")
            && seen.Contains("sourceIdentity");
    }

    private static bool HasTrustedSourceIdentity(
        JsonElement value,
        CaptureSourceIdentity expectedIdentity,
        long? expectedSourcePosition,
        string expectedLocatorKind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || property.Name is not (
                    "externalSessionId"
                    or "childId"
                    or "sourcePosition"
                    or "locatorKind"))
            {
                return false;
            }
        }

        bool hasChildProperty = value.TryGetProperty("childId", out JsonElement child);
        bool childMatches = expectedIdentity.ChildId is null
            ? !hasChildProperty || child.ValueKind == JsonValueKind.Null
            : hasChildProperty
                && child.ValueKind == JsonValueKind.String
                && string.Equals(
                    child.GetString(),
                    expectedIdentity.ChildId,
                    StringComparison.Ordinal);
        return seen.Count == (hasChildProperty ? 4 : 3)
            && HasString(
                value,
                "externalSessionId",
                expectedIdentity.ExternalSessionId)
            && childMatches
            && value.TryGetProperty("sourcePosition", out JsonElement position)
            && position.TryGetInt64(out long sourcePosition)
            && (expectedSourcePosition is null
                ? sourcePosition >= 0
                : sourcePosition == expectedSourcePosition)
            && HasString(value, "locatorKind", expectedLocatorKind);
    }

    private static bool IsMalformedJsonRepresentation(
        JsonElement value,
        CaptureObservationCommand observation) =>
        HasOnlyProperties(value, "opaqueText", "parseError")
        && value.TryGetProperty("opaqueText", out JsonElement opaqueText)
        && opaqueText.ValueKind == JsonValueKind.String
        && !opaqueText.ValueEquals(string.Empty)
        && value.TryGetProperty("parseError", out JsonElement parseError)
        && HasOnlyProperties(parseError, "reason", "policyVersion", "sourceIdentity")
        && HasString(parseError, "reason", MalformedJsonReason)
        && HasString(parseError, "policyVersion", CurrentVersion)
        && parseError.TryGetProperty("sourceIdentity", out JsonElement sourceIdentity)
        && HasTerminalRecordSourceIdentity(sourceIdentity, observation);

    private static bool IsUninspectableRecordRepresentation(
        JsonElement value,
        CaptureObservationCommand observation) =>
        HasOnlyProperties(value, "omission")
        && value.TryGetProperty("omission", out JsonElement omission)
        && HasOnlyProperties(
            omission,
            "reason",
            "originalByteCount",
            "policyVersion",
            "contentPolicy",
            "sourceIdentity")
        && HasString(omission, "reason", UninspectableSourceRecordReason)
        && omission.TryGetProperty("originalByteCount", out JsonElement byteCount)
        && byteCount.TryGetInt64(out long count)
        && count > 0
        && HasTerminalRecordLength(count, observation)
        && HasString(omission, "policyVersion", CurrentVersion)
        && HasString(omission, "contentPolicy", InvalidUtf8ContentPolicy)
        && omission.TryGetProperty("sourceIdentity", out JsonElement sourceIdentity)
        && HasTerminalRecordSourceIdentity(sourceIdentity, observation);

    private static bool HasTerminalRecordSourceIdentity(
        JsonElement value,
        CaptureObservationCommand observation)
    {
        if (!HasOnlyProperties(
                value,
                "externalSessionId",
                "childId",
                "sourcePosition",
                "locatorKind")
            || !HasString(
                value,
                "externalSessionId",
                observation.SourceIdentity.ExternalSessionId)
            || !value.TryGetProperty("childId", out JsonElement child)
            || !value.TryGetProperty("sourcePosition", out JsonElement position)
            || !position.TryGetInt64(out long sourcePosition)
            || sourcePosition != observation.SourcePosition
            || !HasString(value, "locatorKind", "byte_range"))
        {
            return false;
        }

        return observation.SourceIdentity.ChildId is null
            ? child.ValueKind == JsonValueKind.Null
            : child.ValueKind == JsonValueKind.String
                && string.Equals(
                    child.GetString(),
                    observation.SourceIdentity.ChildId,
                    StringComparison.Ordinal);
    }

    private static bool HasTerminalRecordLength(
        long contentByteCount,
        CaptureObservationCommand observation) =>
        observation.Locator is CaptureSourceLocator.ByteRange range
        && range.Length - contentByteCount is >= 0 and <= 2;

    private static bool IsExactTerminalEventProjection(
        JsonElement value,
        string? recordType,
        JsonElement sourcePayload) =>
        recordType is not null
        && HasOnlyProperties(value, "recordType", "payloadType", "source")
        && HasString(value, "recordType", recordType)
        && value.TryGetProperty("payloadType", out JsonElement payloadType)
        && payloadType.ValueKind == JsonValueKind.Null
        && value.TryGetProperty("source", out JsonElement source)
        && JsonElement.DeepEquals(source, sourcePayload);

    private static bool HasOnlyProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                return false;
            }
        }
        return remaining.Count == 0;
    }

    private static bool HasOptionalNonBlankString(
        JsonElement value,
        string propertyName) =>
        !value.TryGetProperty(propertyName, out JsonElement property)
        || (property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()));

    private static bool HasString(
        JsonElement value,
        string propertyName,
        string expected) =>
        value.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
        && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static void WriteOpaque(
        JsonElement value,
        Utf8JsonWriter writer,
        GovernedSerializationStream stream)
    {
        stream.AssertWithinDeadline();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    stream.AssertWithinDeadline();
                    writer.WritePropertyName(property.Name);
                    WriteOpaque(property.Value, writer, stream);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteOpaque(item, writer, stream);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private sealed class BinaryRewriteState(
        GovernedSerializationStream stream,
        string harness,
        CaptureSourceIdentity sourceIdentity,
        long sourcePosition,
        string? locatorKind,
        TrustedTraversalRootContext rootContext)
    {
        public GovernedSerializationStream Stream { get; } = stream;
        public string Harness { get; } = harness;
        public CaptureSourceIdentity SourceIdentity { get; } = sourceIdentity;
        public long SourcePosition { get; } = sourcePosition;
        public string? LocatorKind { get; } = locatorKind;
        public TrustedTraversalRootContext RootContext { get; } = rootContext;
        public int Depth { get; set; }
        public bool InCodexResponsePayload { get; set; }
        public int OmissionCount { get; set; }
    }

    private enum TrustedTraversalRootContext
    {
        None,
        RawSource,
        AdapterEvent
    }

    private sealed record RewrittenJson(
        JsonElement Value,
        int OmissionCount,
        long SerializedBytes,
        bool ExceededBound);

    private static JsonElement Provenance(
        string reason,
        long originalByteCount,
        CaptureSourceIdentity source,
        long sourcePosition,
        string? locatorKind) =>
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

public sealed record BoundedCaptureRepresentation<T>(
    T Observation,
    string Serialized,
    long OriginalByteCount,
    bool WasOmitted);

public sealed record BinaryFidelitySelection<T>(
    T Observation,
    int OmissionCount)
{
    public bool WasOmitted => OmissionCount > 0;
}

public sealed record CaptureDeterministicFidelity(
    string Reason,
    long OriginalByteCount);
