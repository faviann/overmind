using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

/// <summary>
/// The single reader over durable capture rows. Ingestion and operator reads
/// both project canonical facts through here, so an observation looks the same
/// whichever module returns it. It is also where the capture modules keep the
/// small guards they share, so a rule is stated once.
/// </summary>
internal static class CaptureLedger
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Capture refuses to run at all without a loaded never-store rule set.
    /// Missing, empty, invalid, duplicated, unsupported, or un-loadable rule
    /// configuration all arrive here, and all fail closed with the safe reason
    /// the gate reported at load.
    /// </summary>
    internal static void RequireSafetyConfigured(NeverStoreGate neverStore)
    {
        if (!neverStore.IsConfigured)
        {
            throw new SafetyConfigurationException(
                neverStore.FailureReason ?? "the rule set could not be loaded");
        }
    }

    /// <summary>Rejects a blank required string argument.</summary>
    internal static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }
    }

    internal static async Task<CaptureObservationReceipt?> LoadObservationAsync(
        NpgsqlConnection connection,
        Guid observationUuid,
        NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ObservationRow>(
            """
            SELECT o.observation_uuid AS ObservationUuid,
                   o.stream_uuid AS SourceStreamUuid,
                   s.external_session_id AS ExternalSessionId,
                   s.child_id AS ChildId,
                   o.locator_kind AS LocatorKind,
                   o.locator_native_id AS LocatorNativeId,
                   o.locator_byte_offset AS LocatorByteOffset,
                   o.locator_byte_length AS LocatorByteLength,
                   o.source_timestamp_raw AS SourceTimestampRaw,
                   o.source_timestamp_parsed AS SourceTimestampParsed,
                   o.source::text AS SourceJson,
                   COALESCE(o.route_evidence, 'null'::jsonb)::text AS RouteEvidenceJson,
                   o.adapter::text AS AdapterJson,
                   o.safe_source_payload::text AS SafeSourcePayloadJson,
                   o.scan_status AS ScanStatus,
                   o.scan_rule_set_version AS ScanRuleSetVersion,
                   o.scan_rule_ids AS ScanRuleIds,
                   o.scan_categories AS ScanCategories,
                   o.scan_redaction_count AS ScanRedactionCount,
                   o.captured_at AS CapturedAt
            FROM capture_observations o
            JOIN capture_source_streams s USING (stream_uuid)
            WHERE o.observation_uuid = @observationUuid
            """,
            new { observationUuid }, transaction);
        if (row is null)
        {
            return null;
        }

        return new CaptureObservationReceipt(
            row.ObservationUuid,
            row.SourceStreamUuid,
            new CaptureSourceIdentity(row.ExternalSessionId, row.ChildId),
            JsonSerializer.Deserialize<CaptureSource>(row.SourceJson, JsonOptions)!,
            CaptureSourceLocator.FromColumns(new CaptureSourceLocator.Columns(
                row.LocatorKind, row.LocatorNativeId, row.LocatorByteOffset, row.LocatorByteLength)),
            row.SourceTimestampRaw is null
                ? null
                : new CaptureSourceTimestamp(row.SourceTimestampRaw, row.SourceTimestampParsed),
            JsonSerializer.Deserialize<CaptureRouteEvidence?>(
                row.RouteEvidenceJson, JsonOptions),
            JsonSerializer.Deserialize<CaptureAdapter>(row.AdapterJson, JsonOptions)!,
            JsonDocument.Parse(row.SafeSourcePayloadJson).RootElement.Clone(),
            new CaptureScanReceipt(
                row.ScanStatus,
                row.ScanRuleSetVersion,
                row.ScanRuleIds,
                row.ScanCategories,
                row.ScanRedactionCount),
            row.CapturedAt);
    }

    internal static async Task<IReadOnlyList<CapturedEventReceipt>> LoadEventsAsync(
        NpgsqlConnection connection,
        Guid observationUuid,
        NpgsqlTransaction? transaction = null)
    {
        var events = (await connection.QueryAsync<EventRow>(
            """
            SELECT trace_uuid AS TraceUuid, session_id AS SessionId,
                   agent_id AS AgentId, namespace,
                   part_key AS PartKey, part_order AS PartOrder,
                   kind, actor, occurred_at AS OccurredAt,
                   payload_version AS PayloadVersion, payload::text AS PayloadJson
            FROM captured_events
            WHERE observation_uuid = @observationUuid
            ORDER BY part_order
            """,
            new { observationUuid }, transaction)).AsList();
        var receipts = new List<CapturedEventReceipt>(events.Count);
        foreach (var item in events)
        {
            var relationships = (await connection.QueryAsync<RelationshipRow>(
                """
                SELECT relationship_type AS Type,
                       target_source_stream_uuid AS TargetSourceStreamUuid,
                       target_native_id AS TargetNativeId,
                       target_kind AS TargetKind
                FROM captured_event_relationships
                WHERE source_trace_uuid = @TraceUuid
                ORDER BY relationship_type, target_native_id
                """,
                new { item.TraceUuid }, transaction)).AsList();
            receipts.Add(new CapturedEventReceipt(
                new CanonicalCapturedEvent(
                    item.TraceUuid,
                    item.SessionId,
                    item.AgentId,
                    item.Namespace,
                    item.PartKey,
                    item.PartOrder,
                    item.Kind,
                    item.Actor,
                    item.OccurredAt,
                    item.PayloadVersion,
                    JsonDocument.Parse(item.PayloadJson).RootElement.Clone()),
                relationships.Select(relationship => new CaptureRelationship(
                    relationship.Type,
                    new CaptureRelationshipTarget(
                        relationship.TargetSourceStreamUuid,
                        relationship.TargetNativeId,
                        relationship.TargetKind))).ToArray()));
        }
        return receipts;
    }

    internal static async Task<IReadOnlyList<SourceOrderedObservation>>
        LoadSourceOrderedObservationsAsync(
            NpgsqlConnection connection,
            Guid sourceStreamUuid)
    {
        var rows = (await connection.QueryAsync<SourceOrderedObservationRow>(
            """
            SELECT observation_uuid AS ObservationUuid,
                   source_position AS SourcePosition
            FROM capture_observations
            WHERE stream_uuid = @sourceStreamUuid
            ORDER BY source_position
            """,
            new { sourceStreamUuid })).AsList();
        var observations = new List<SourceOrderedObservation>(rows.Count);
        foreach (var row in rows)
        {
            var observation = await LoadObservationAsync(connection, row.ObservationUuid)
                ?? throw new InvalidOperationException(
                    $"Capture observation '{row.ObservationUuid}' was not found.");
            observations.Add(new SourceOrderedObservation(row.SourcePosition, observation));
        }

        return observations;
    }

    internal static Task<SessionRow?> LoadAuthorizedSessionAsync(
        NpgsqlConnection connection,
        Guid sourceStreamUuid,
        IReadOnlyCollection<string> allowedNamespaces) =>
        connection.QuerySingleOrDefaultAsync<SessionRow>(
            """
            SELECT stream_uuid AS SourceStreamUuid,
                   binding_uuid AS BindingUuid,
                   trace_session_id AS SessionId,
                   effective_namespace AS Namespace,
                   external_session_id AS ExternalSessionId,
                   child_id AS ChildId
            FROM capture_source_streams
            WHERE stream_uuid = @sourceStreamUuid
              AND effective_namespace = ANY(@allowedNamespaces)
            """,
            new { sourceStreamUuid, allowedNamespaces = allowedNamespaces.ToArray() });

    internal static async Task<IReadOnlyList<SessionRelationshipRow>>
        LoadOutgoingSessionRelationshipsAsync(
            NpgsqlConnection connection,
            Guid sourceStreamUuid)
    {
        return (await connection.QueryAsync<SessionRelationshipRow>(
            """
            SELECT r.relationship_type AS RelationshipType,
                   r.source_trace_uuid AS SourceTraceUuid,
                   source_stream.stream_uuid AS SourceStreamUuid,
                   source_stream.binding_uuid AS SourceBindingUuid,
                   r.target_source_stream_uuid AS TargetSourceStreamUuid,
                   r.target_native_id AS TargetNativeId,
                   r.target_kind AS TargetKind
            FROM captured_event_relationships r
            JOIN captured_events e ON e.trace_uuid = r.source_trace_uuid
            JOIN capture_observations o USING (observation_uuid)
            JOIN capture_source_streams source_stream USING (stream_uuid)
            WHERE source_stream.stream_uuid = @sourceStreamUuid
              AND r.relationship_type = ANY(@relationshipTypes)
              AND r.target_kind = 'session'
            """,
            new
            {
                sourceStreamUuid,
                relationshipTypes = SessionRelationshipTypes
            })).AsList();
    }

    internal static async Task<IReadOnlyList<SessionRelationshipRow>>
        LoadIncomingSessionRelationshipsAsync(
            NpgsqlConnection connection,
            SessionRow target,
            IReadOnlyCollection<string> allowedNamespaces)
    {
        return (await connection.QueryAsync<SessionRelationshipRow>(
            """
            SELECT r.relationship_type AS RelationshipType,
                   r.source_trace_uuid AS SourceTraceUuid,
                   source_stream.stream_uuid AS SourceStreamUuid,
                   source_stream.binding_uuid AS SourceBindingUuid,
                   r.target_source_stream_uuid AS TargetSourceStreamUuid,
                   r.target_native_id AS TargetNativeId,
                   r.target_kind AS TargetKind,
                   source_stream.trace_session_id AS SourceSessionId,
                   source_stream.effective_namespace AS SourceNamespace,
                   source_stream.external_session_id AS SourceExternalSessionId,
                   source_stream.child_id AS SourceChildId
            FROM captured_event_relationships r
            JOIN captured_events e ON e.trace_uuid = r.source_trace_uuid
            JOIN capture_observations o USING (observation_uuid)
            JOIN capture_source_streams source_stream USING (stream_uuid)
            WHERE source_stream.effective_namespace = ANY(@allowedNamespaces)
              AND r.relationship_type = ANY(@relationshipTypes)
              AND r.target_kind = 'session'
              AND (
                r.target_source_stream_uuid = @targetSourceStreamUuid
                OR (
                  r.target_source_stream_uuid IS NULL
                  AND source_stream.binding_uuid = @targetBindingUuid
                  AND r.target_native_id = @targetNativeIdentity
                  AND (
                    SELECT count(*)
                    FROM capture_source_streams candidate
                    WHERE candidate.binding_uuid = source_stream.binding_uuid
                      AND COALESCE(candidate.child_id, candidate.external_session_id)
                          = r.target_native_id
                  ) = 1
                )
              )
            """,
            new
            {
                allowedNamespaces = allowedNamespaces.ToArray(),
                relationshipTypes = SessionRelationshipTypes,
                targetSourceStreamUuid = target.SourceStreamUuid,
                targetBindingUuid = target.BindingUuid,
                targetNativeIdentity = target.NativeIdentity
            })).AsList();
    }

    internal static async Task<SessionRow?> ResolveAuthorizedRelationshipTargetAsync(
        NpgsqlConnection connection,
        SessionRelationshipRow relationship,
        IReadOnlyCollection<string> allowedNamespaces)
    {
        if (relationship.TargetSourceStreamUuid is Guid targetSourceStreamUuid)
        {
            return await LoadAuthorizedSessionAsync(
                connection, targetSourceStreamUuid, allowedNamespaces);
        }

        var rows = (await connection.QueryAsync<SessionCandidateRow>(
            """
            WITH candidates AS (
              SELECT stream_uuid AS SourceStreamUuid,
                     binding_uuid AS BindingUuid,
                     trace_session_id AS SessionId,
                     effective_namespace AS Namespace,
                     external_session_id AS ExternalSessionId,
                     child_id AS ChildId
              FROM capture_source_streams
              WHERE binding_uuid = @sourceBindingUuid
                AND COALESCE(child_id, external_session_id) = @targetNativeId
            )
            SELECT candidate.*,
                   (SELECT count(*) FROM candidates) AS MatchCount
            FROM candidates candidate
            WHERE candidate.Namespace = ANY(@allowedNamespaces)
            """,
            new
            {
                relationship.SourceBindingUuid,
                relationship.TargetNativeId,
                allowedNamespaces = allowedNamespaces.ToArray()
            })).AsList();
        return rows.Count == 1 && rows[0].MatchCount == 1
            ? rows[0].ToSessionRow()
            : null;
    }

    private static readonly string[] SessionRelationshipTypes =
        ["parent_session", "spawned_by", "forked_from"];

    internal sealed record SourceOrderedObservation(
        long SourcePosition,
        CaptureObservationReceipt Observation);

    internal class SessionRow
    {
        public Guid SourceStreamUuid { get; set; }
        public Guid BindingUuid { get; set; }
        public string SessionId { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string ExternalSessionId { get; set; } = "";
        public string? ChildId { get; set; }
        public string NativeIdentity => ChildId ?? ExternalSessionId;

        internal CapturedSessionReference ToReference() => new(
            SourceStreamUuid,
            SessionId,
            Namespace,
            new CaptureSourceIdentity(ExternalSessionId, ChildId));
    }

    internal sealed class SessionRelationshipRow
    {
        public string RelationshipType { get; set; } = "";
        public Guid SourceTraceUuid { get; set; }
        public Guid SourceStreamUuid { get; set; }
        public Guid SourceBindingUuid { get; set; }
        public Guid? TargetSourceStreamUuid { get; set; }
        public string TargetNativeId { get; set; } = "";
        public string? TargetKind { get; set; }
        public string SourceSessionId { get; set; } = "";
        public string SourceNamespace { get; set; } = "";
        public string SourceExternalSessionId { get; set; } = "";
        public string? SourceChildId { get; set; }

        internal CaptureSessionRelationshipEvidence ToEvidence() => new(
            RelationshipType,
            SourceTraceUuid,
            SourceStreamUuid,
            TargetSourceStreamUuid,
            TargetNativeId,
            TargetKind);

        internal CapturedSessionReference ToSourceReference() => new(
            SourceStreamUuid,
            SourceSessionId,
            SourceNamespace,
            new CaptureSourceIdentity(SourceExternalSessionId, SourceChildId));
    }

    private sealed class SessionCandidateRow : SessionRow
    {
        public long MatchCount { get; set; }

        internal SessionRow ToSessionRow() => new()
        {
            SourceStreamUuid = SourceStreamUuid,
            BindingUuid = BindingUuid,
            SessionId = SessionId,
            Namespace = Namespace,
            ExternalSessionId = ExternalSessionId,
            ChildId = ChildId
        };
    }

    private sealed class ObservationRow
    {
        public Guid ObservationUuid { get; set; }
        public Guid SourceStreamUuid { get; set; }
        public string ExternalSessionId { get; set; } = "";
        public string? ChildId { get; set; }
        public string LocatorKind { get; set; } = "";
        public string? LocatorNativeId { get; set; }
        public long? LocatorByteOffset { get; set; }
        public long? LocatorByteLength { get; set; }
        public string? SourceTimestampRaw { get; set; }
        public DateTimeOffset? SourceTimestampParsed { get; set; }
        public string SourceJson { get; set; } = "";
        public string RouteEvidenceJson { get; set; } = "null";
        public string AdapterJson { get; set; } = "";
        public string SafeSourcePayloadJson { get; set; } = "";
        public string ScanStatus { get; set; } = "";
        public string ScanRuleSetVersion { get; set; } = "";
        public string[] ScanRuleIds { get; set; } = [];
        public string[] ScanCategories { get; set; } = [];
        public int ScanRedactionCount { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
    }

    private sealed class EventRow
    {
        public Guid TraceUuid { get; set; }
        public string SessionId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string PartKey { get; set; } = "";
        public int PartOrder { get; set; }
        public string Kind { get; set; } = "";
        public string Actor { get; set; } = "";
        public DateTimeOffset? OccurredAt { get; set; }
        public int PayloadVersion { get; set; }
        public string PayloadJson { get; set; } = "";
    }

    private sealed class RelationshipRow
    {
        public string Type { get; set; } = "";
        public Guid? TargetSourceStreamUuid { get; set; }
        public string TargetNativeId { get; set; } = "";
        public string? TargetKind { get; set; }
    }

    private sealed class SourceOrderedObservationRow
    {
        public Guid ObservationUuid { get; set; }
        public long SourcePosition { get; set; }
    }
}
