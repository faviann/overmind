using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemSrv.Core;

// ---------------------------------------------------------------------------
// Wire shapes. These exist only so the HTTP seam can deserialize a capture
// request. They are deliberately permissive: JSON can express a locator that
// mixes native-id and byte-range fields, and the seam is responsible for
// rejecting that before anything else sees it.
// ---------------------------------------------------------------------------

public sealed record CaptureLocator(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NativeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ByteOffset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ByteLength,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceContentSha256);

public sealed record CaptureObservationRequest(
    int ContractVersion,
    string? SourceSessionId,
    long SourcePosition,
    CaptureLocator Locator,
    CaptureSourceTimestamp? SourceTimestamp,
    CaptureSource Source,
    CaptureAdapter Adapter,
    JsonElement SourcePayload,
    IReadOnlyList<CaptureEvent> Events,
    CaptureRouteEvidence? RouteEvidence = null,
    CaptureSourceIdentity? SourceIdentity = null);

/// <summary>
/// Harness-native identity facts used with the authenticated capture binding to
/// establish one capture stream. Relationship facts are deliberately excluded.
/// </summary>
public sealed record CaptureSourceIdentity(
    string ExternalSessionId,
    string? ChildId = null);

// ---------------------------------------------------------------------------
// Shared source facts.
// ---------------------------------------------------------------------------

public sealed record CaptureSource(
    string Harness,
    string? HarnessVersion,
    string? RecordType,
    string? Model = null,
    string? Provider = null,
    string? MaterialKind = null);
public sealed record CaptureAdapter(string Name, string Version);
public sealed record CaptureSourceTimestamp(string Raw, DateTimeOffset? Parsed);
public sealed record CaptureRemote(string Name, string Url);
public sealed record CaptureRouteEvidence(
    string? WorkingDirectory,
    IReadOnlyList<CaptureRemote>? Remotes);
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

// ---------------------------------------------------------------------------
// The internal source-locator representation. The private parameterless
// constructor rules out accidental or positional derivation, so "native id plus
// byte range" is unrepresentable through the parse path rather than merely
// rejected. The protected copy constructor every record synthesizes stays a
// deliberate-abuse escape hatch, undefended by design. Everything past the HTTP
// seam speaks this type.
// ---------------------------------------------------------------------------

[JsonConverter(typeof(CaptureSourceLocatorConverter))]
public abstract record CaptureSourceLocator
{
    private CaptureSourceLocator()
    {
    }

    public abstract string Kind { get; }

    /// <summary>Short human-readable identity used in conflict messages.</summary>
    public abstract string Describe();

    /// <summary>
    /// Parses a wire locator into its variant, or throws <see cref="ArgumentException"/>.
    /// This is the only construction path from untrusted input.
    /// </summary>
    public static CaptureSourceLocator Parse(CaptureLocator? locator)
    {
        if (locator is null)
        {
            throw new ArgumentException("locator is required.");
        }
        if (string.Equals(locator.Kind, "native_id", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(locator.NativeId))
            {
                throw new ArgumentException("locator.nativeId is required.");
            }
            if (locator.ByteOffset is not null
                || locator.ByteLength is not null
                || locator.SourceContentSha256 is not null)
            {
                throw new ArgumentException("native_id locator accepts nativeId only.");
            }
            return new NativeId(locator.NativeId);
        }
        if (string.Equals(locator.Kind, "byte_range", StringComparison.Ordinal))
        {
            if (locator.NativeId is not null
                || locator.ByteOffset is null
                || locator.ByteOffset < 0
                || locator.ByteLength is null
                || locator.ByteLength <= 0
                || !IsLowerHexSha256(locator.SourceContentSha256))
            {
                throw new ArgumentException(
                    "byte_range locator requires byteOffset >= 0, byteLength > 0, " +
                    "and a 64-character lowercase sourceContentSha256.");
            }
            return new ByteRange(
                locator.ByteOffset.Value, locator.ByteLength.Value, locator.SourceContentSha256);
        }
        throw new ArgumentException("locator.kind must be native_id or byte_range.");
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// The four columns a locator occupies in <c>capture_observations</c>. The
    /// projection and its inverse live together on this type so a new variant
    /// cannot be persisted one way and rebuilt another.
    /// </summary>
    internal sealed record Columns(
        string Kind, string? NativeId, long? ByteOffset, long? ByteLength);

    /// <summary>Projects this locator onto its persisted columns.</summary>
    internal Columns ToColumns() =>
        this switch
        {
            NativeId nativeId => new Columns(nativeId.Kind, nativeId.Value, null, null),
            ByteRange range => new Columns(range.Kind, null, range.Offset, range.Length),
            _ => throw new InvalidOperationException("Unknown source locator variant.")
        };

    /// <summary>
    /// Rebuilds a locator from its persisted columns. The inverse of
    /// <see cref="ToColumns"/>, except that a rebuilt <see cref="ByteRange"/>
    /// carries a null digest because the digest is signed but never stored.
    /// </summary>
    internal static CaptureSourceLocator FromColumns(Columns columns) =>
        string.Equals(columns.Kind, "native_id", StringComparison.Ordinal)
            ? new NativeId(columns.NativeId!)
            : new ByteRange(columns.ByteOffset!.Value, columns.ByteLength!.Value, null);

    public sealed record NativeId(string Value) : CaptureSourceLocator
    {
        public override string Kind => "native_id";
        public override string Describe() => Value;
    }

    /// <remarks>
    /// <paramref name="SourceContentSha256"/> is required on import and covered by
    /// the retry signature, but it is never persisted. A locator rebuilt from the
    /// ledger therefore carries null there.
    /// </remarks>
    public sealed record ByteRange(long Offset, long Length, string? SourceContentSha256)
        : CaptureSourceLocator
    {
        public override string Kind => "byte_range";
        public override string Describe() => $"{Offset}+{Length}";
    }
}

internal sealed class CaptureSourceLocatorConverter : JsonConverter<CaptureSourceLocator>
{
    public override CaptureSourceLocator Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Source locators are parsed from CaptureLocator at the HTTP seam, never deserialized directly.");

    public override void Write(
        Utf8JsonWriter writer, CaptureSourceLocator value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case CaptureSourceLocator.NativeId nativeId:
                writer.WriteString("nativeId", nativeId.Value);
                break;
            case CaptureSourceLocator.ByteRange range:
                writer.WriteNumber("byteOffset", range.Offset);
                writer.WriteNumber("byteLength", range.Length);
                if (range.SourceContentSha256 is not null)
                {
                    writer.WriteString("sourceContentSha256", range.SourceContentSha256);
                }
                break;
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// One authenticated import request. Produced at the HTTP seam from
/// <see cref="CaptureObservationRequest"/>; consumed by capture ingestion.
/// </summary>
public sealed record CaptureObservationCommand(
    int ContractVersion,
    CaptureSourceIdentity SourceIdentity,
    long SourcePosition,
    CaptureSourceLocator Locator,
    CaptureSourceTimestamp? SourceTimestamp,
    CaptureSource Source,
    CaptureAdapter Adapter,
    JsonElement SourcePayload,
    IReadOnlyList<CaptureEvent> Events,
    CaptureRouteEvidence? RouteEvidence)
{
    public static CaptureObservationCommand FromRequest(CaptureObservationRequest request)
    {
        if (request.SourceIdentity is not null
            && !string.IsNullOrWhiteSpace(request.SourceSessionId)
            && !string.Equals(
                request.SourceSessionId,
                request.SourceIdentity.ExternalSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "sourceSessionId must match sourceIdentity.externalSessionId when both are supplied.");
        }

        CaptureSourceIdentity sourceIdentity = request.SourceIdentity
            ?? new CaptureSourceIdentity(request.SourceSessionId ?? "");
        CaptureLedger.Require(
            sourceIdentity.ExternalSessionId,
            "sourceIdentity.externalSessionId");
        if (sourceIdentity.ChildId is not null)
        {
            CaptureLedger.Require(sourceIdentity.ChildId, "sourceIdentity.childId");
        }
        CaptureSourceLocator locator = CaptureSourceLocator.Parse(request.Locator);

        return new(
            request.ContractVersion,
            sourceIdentity,
            request.SourcePosition,
            locator,
            request.SourceTimestamp,
            request.Source,
            request.Adapter,
            request.SourcePayload,
            request.Events,
            request.RouteEvidence);
    }
}

/// <summary>
/// The authorization facts a resolved capture credential carries. Ingestion
/// never re-resolves a raw credential.
/// </summary>
public sealed record CaptureBindingContext(
    Guid BindingUuid,
    string Harness,
    string AgentId,
    byte[] ContentSignatureKey,
    CaptureRoutingPolicy RoutingPolicy);

public sealed record CaptureRouteOverride(string Remote, string Target);
public sealed record CaptureDirectoryRoute(string Directory, string Target);
public sealed record CaptureSpecialNamespace(string Alias, string Namespace);
public sealed record CaptureRoutingPolicy(
    IReadOnlyList<string> AllowedRepositoryPatterns,
    IReadOnlyList<CaptureRouteOverride> RemoteOverrides,
    IReadOnlyList<CaptureDirectoryRoute> DirectoryRoutes,
    IReadOnlyList<CaptureSpecialNamespace> SpecialNamespaces)
{
    public static CaptureRoutingPolicy Empty { get; } = new([], [], [], []);
}

// ---------------------------------------------------------------------------
// Canonical capture facts. One set, shared by import responses and operator
// reads. No caller translates between two shapes of the same fact.
// ---------------------------------------------------------------------------

public sealed record CaptureScanReceipt(
    string Status,
    string RuleSetVersion,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> Categories,
    int RedactionCount);

public sealed record CaptureObservationReceipt(
    Guid ObservationUuid,
    Guid SourceStreamUuid,
    CaptureSourceIdentity SourceIdentity,
    CaptureSource Source,
    CaptureSourceLocator Locator,
    CaptureSourceTimestamp? SourceTimestamp,
    CaptureRouteEvidence? RouteEvidence,
    CaptureAdapter Adapter,
    JsonElement SafeSourcePayload,
    CaptureScanReceipt Scan,
    DateTimeOffset CapturedAt);

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

/// <summary>
/// A canonical event plus its source relationships. Serialized flat (the event
/// fields followed by <c>relationships</c>) so the import response keeps one
/// object per event.
/// </summary>
[JsonConverter(typeof(CapturedEventReceiptConverter))]
public sealed record CapturedEventReceipt(
    CanonicalCapturedEvent Event,
    IReadOnlyList<CaptureRelationship> Relationships);

internal sealed class CapturedEventReceiptConverter : JsonConverter<CapturedEventReceipt>
{
    public override CapturedEventReceipt Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Capture receipts are produced by the server, never read back.");

    public override void Write(
        Utf8JsonWriter writer, CapturedEventReceipt value, JsonSerializerOptions options)
    {
        using var canonical = JsonSerializer.SerializeToDocument(value.Event, options);
        writer.WriteStartObject();
        foreach (var property in canonical.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }
        writer.WritePropertyName("relationships");
        JsonSerializer.Serialize(writer, value.Relationships, options);
        writer.WriteEndObject();
    }
}

/// <summary>The complete versioned unit an operator reads back.</summary>
public sealed record CapturedEventEnvelope(
    int ContractVersion,
    CaptureObservationReceipt Observation,
    CanonicalCapturedEvent Event,
    IReadOnlyList<CaptureRelationship> Relationships);

/// <summary>
/// Durable evidence used to derive presentation order for one source-stream
/// replay. These values name ledger facts rather than inventing an ordinal.
/// </summary>
public sealed record CaptureReplayOrderBasis(
    string Observation,
    string Event);

/// <summary>
/// One canonical envelope in a source-stream replay, annotated only with its
/// primary observation's durable position.
/// </summary>
public sealed record CapturedEventReplay(
    long SourcePosition,
    CapturedEventEnvelope Envelope);

/// <summary>
/// The operator read model for exactly one accepted capture source stream.
/// Canonical envelopes are wrapped unchanged.
/// </summary>
public sealed record CapturedSourceStreamReplay(
    int ContractVersion,
    Guid SourceStreamUuid,
    CaptureReplayOrderBasis OrderBasis,
    IReadOnlyList<CapturedEventReplay> Events);

/// <summary>
/// The authorized, captured session identity exposed by relationship
/// navigation. It contains no event content and is never synthesized from a
/// relationship target.
/// </summary>
public sealed record CapturedSessionReference(
    Guid SourceStreamUuid,
    string SessionId,
    string Namespace,
    CaptureSourceIdentity SourceIdentity);

/// <summary>
/// Safe source-stated relationship evidence used by session navigation.
/// Unavailable targets omit their explicit source-stream UUID.
/// </summary>
public sealed record CaptureSessionRelationshipEvidence(
    string RelationshipType,
    Guid SourceTraceUuid,
    Guid SourceStreamUuid,
    Guid? TargetSourceStreamUuid,
    string TargetNativeId,
    string? TargetKind);

/// <summary>
/// One source-stated session relationship as seen from the requested session.
/// An unavailable related session is always null, whether it is absent,
/// ambiguous, or outside the caller's namespace authority.
/// </summary>
public sealed record CapturedSessionRelationship(
    string Direction,
    string Availability,
    CaptureSessionRelationshipEvidence Evidence,
    CapturedSessionReference? Session);

/// <summary>
/// Authorization-aware read-time navigation over immutable captured session
/// and source-relationship facts.
/// </summary>
public sealed record CapturedSessionNavigation(
    int ContractVersion,
    CapturedSessionReference Session,
    IReadOnlyList<CapturedSessionRelationship> Relationships);

/// <summary>The per-record import outcome returned by the capture endpoint.</summary>
public sealed record CaptureImportReceipt(
    Guid ObservationUuid,
    string Status,
    long SourcePosition,
    string EffectiveNamespace,
    string RouteBasis,
    CaptureObservationReceipt Observation,
    IReadOnlyList<CapturedEventReceipt> Events);
