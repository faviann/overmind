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

    private sealed class ObservationRow
    {
        public Guid ObservationUuid { get; set; }
        public Guid SourceStreamUuid { get; set; }
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
}
