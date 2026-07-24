using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed class CaptureConflictException(string message) : Exception(message);

public sealed class CaptureService(string connectionString, NeverStoreGate neverStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsCredentialKnownAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM capture_source_bindings
              WHERE credential_hash = @credentialHash AND active
            )
            """,
            new { credentialHash = Hash(credential) });
    }

    public async Task<Guid> EnrollAsync(
        string stableName,
        string harness,
        string agentId,
        string credential,
        string? routeNamespace,
        CancellationToken cancellationToken = default)
    {
        EnsureSafetyConfigured();
        Require(stableName, nameof(stableName));
        Require(agentId, nameof(agentId));
        Require(credential, nameof(credential));
        if (!string.Equals(harness, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This disabled slice enrolls only harness 'codex'.");
        }

        string effective = routeNamespace ?? "capture/unscoped";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        bool exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM namespaces WHERE name = @effective)", new { effective });
        if (!exists)
        {
            throw new InvalidOperationException($"Namespace '{effective}' does not exist.");
        }

        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_source_bindings
              (stable_name, harness, agent_id, credential_hash, route_namespace, allowed_namespaces)
            VALUES (@stableName, @harness, @agentId, @credentialHash, @routeNamespace, @allowedNamespaces)
            RETURNING binding_uuid
            """,
            new
            {
                stableName,
                harness,
                agentId,
                credentialHash = Hash(credential),
                routeNamespace,
                allowedNamespaces = new[] { effective }
            });
    }

    public async Task<CaptureReceipt?> ImportAsync(
        string credential,
        CaptureObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var binding = await connection.QuerySingleOrDefaultAsync<BindingRow>(
            """
            SELECT binding_uuid AS BindingUuid, stable_name AS StableName, harness,
                   agent_id AS AgentId, route_namespace AS RouteNamespace,
                   allowed_namespaces AS AllowedNamespaces,
                   content_signature_key AS ContentSignatureKey
            FROM capture_source_bindings
            WHERE credential_hash = @credentialHash AND active
            """,
            new { credentialHash = Hash(credential) });
        if (binding is null)
        {
            return null;
        }

        EnsureSafetyConfigured();
        Validate(binding, request);
        string inputJson = JsonSerializer.Serialize(request, JsonOptions);
        if (Encoding.UTF8.GetByteCount(inputJson) > 1_000_000)
        {
            throw new InvalidOperationException("Capture observation exceeds the 1000000-byte non-production limit.");
        }

        string signatureContent = JsonSerializer.Serialize(
            new CaptureSignatureContent(
                request.ContractVersion,
                request.SourceSessionId,
                request.SourceLocator,
                request.Source,
                request.Adapter,
                request.SourcePayload,
                request.Events),
            JsonOptions);
        string contentSignature = Sign(signatureContent, binding.ContentSignatureKey);
        var scan = new ScanAccumulator(neverStore.RuleSetVersion);
        AssertSafe(request.SourceSessionId, scan);
        AssertSafe(request.SourceLocator, scan);
        foreach (var item in request.Events)
        {
            AssertSafe(item.PartKey, scan);
            AssertSafe(item.Kind, scan);
            AssertSafe(item.Actor, scan);
            foreach (var relationship in item.Relationships ?? [])
            {
                AssertSafe(relationship.Type, scan);
            }
        }
        string source = Redact(JsonSerializer.Serialize(request.Source, JsonOptions), scan);
        string adapter = Redact(JsonSerializer.Serialize(request.Adapter, JsonOptions), scan);
        string safePayload = Redact(request.SourcePayload.GetRawText(), scan);
        var safeEvents = request.Events.Select(item => new SafeEvent(
            item,
            Redact(item.Payload.GetRawText(), scan),
            (item.Relationships ?? []).Select(relationship => new SafeRelationship(
                relationship,
                Redact(relationship.TargetNativeId, scan),
                relationship.TargetKind is null ? null : Redact(relationship.TargetKind, scan))).ToArray()
        )).ToArray();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string proposedNamespace = binding.RouteNamespace ?? "capture/unscoped";
        string proposedRouteBasis = binding.RouteNamespace is null ? "fallback" : "configured_binding";
        bool streamExists = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM capture_source_streams
              WHERE binding_uuid = @BindingUuid AND source_session_id = @SourceSessionId
            )
            """,
            new { binding.BindingUuid, request.SourceSessionId }, transaction);
        if (!streamExists
            && !binding.AllowedNamespaces.Contains(proposedNamespace, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Binding route is outside its allowed namespaces.");
        }
        var stream = await connection.QuerySingleAsync<StreamRow>(
            """
            INSERT INTO capture_source_streams
              (binding_uuid, source_session_id, effective_namespace, route_basis)
            VALUES (@BindingUuid, @SourceSessionId, @proposedNamespace, @proposedRouteBasis)
            ON CONFLICT (binding_uuid, source_session_id)
            DO UPDATE SET updated_at = capture_source_streams.updated_at
            RETURNING stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                      route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition
            """,
            new
            {
                binding.BindingUuid,
                request.SourceSessionId,
                proposedNamespace,
                proposedRouteBasis
            }, transaction);

        var existingMatches = (await connection.QueryAsync<ExistingObservation>(
            """
            SELECT observation_uuid AS ObservationUuid, source_position AS SourcePosition,
                   source_locator AS SourceLocator, content_signature AS ContentSignature
            FROM capture_observations
            WHERE stream_uuid = @StreamUuid
              AND (source_position = @SourcePosition OR source_locator = @SourceLocator)
            """,
            new { stream.StreamUuid, request.SourcePosition, request.SourceLocator }, transaction)).AsList();
        if (existingMatches.Count > 0)
        {
            var locatorMatch = existingMatches.SingleOrDefault(candidate =>
                string.Equals(candidate.SourceLocator, request.SourceLocator, StringComparison.Ordinal));
            if (locatorMatch is not null)
            {
                if (!string.Equals(
                    locatorMatch.ContentSignature, contentSignature, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new CaptureConflictException(
                        $"Source position {request.SourcePosition} or locator '{request.SourceLocator}' " +
                        "was already accepted with different identity or content.");
                }

                var oldObservation = await LoadObservationReceiptAsync(
                    connection, locatorMatch.ObservationUuid, transaction);
                var oldEvents = await LoadEventReceiptsAsync(
                    connection, locatorMatch.ObservationUuid, transaction);
                await transaction.CommitAsync(cancellationToken);
                return new CaptureReceipt(
                    locatorMatch.ObservationUuid, "already_accepted", locatorMatch.SourcePosition,
                    stream.EffectiveNamespace, stream.RouteBasis, oldObservation, oldEvents);
            }

            await transaction.RollbackAsync(cancellationToken);
            throw new CaptureConflictException(
                $"Source position {request.SourcePosition} or locator '{request.SourceLocator}' " +
                "was already accepted with different identity or content.");
        }

        long expectedPosition = (stream.CheckpointPosition ?? -1) + 1;
        if (request.SourcePosition != expectedPosition)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CaptureConflictException(
                $"Capture stream expected sourcePosition {expectedPosition} but received " +
                $"{request.SourcePosition}; gaps and backtracking are not accepted.");
        }

        var observationUuid = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_observations
              (stream_uuid, source_position, source_locator, content_signature,
               effective_namespace, route_basis, source, adapter, safe_source_payload,
               scan_status, scan_rule_set_version, scan_rule_ids, scan_categories,
               scan_redaction_count)
            VALUES
              (@StreamUuid, @SourcePosition, @SourceLocator, @contentSignature,
               @EffectiveNamespace, @RouteBasis, CAST(@source AS jsonb), CAST(@adapter AS jsonb),
               CAST(@safePayload AS jsonb), @ScanStatus, @RuleSetVersion, @RuleIds,
               @Categories, @RedactionCount)
            RETURNING observation_uuid
            """,
            new
            {
                stream.StreamUuid,
                request.SourcePosition,
                request.SourceLocator,
                contentSignature,
                stream.EffectiveNamespace,
                stream.RouteBasis,
                source,
                adapter,
                safePayload,
                ScanStatus = scan.RedactionCount == 0 ? "clean" : "redacted",
                scan.RuleSetVersion,
                RuleIds = scan.RuleIds.ToArray(),
                Categories = scan.Categories.ToArray(),
                scan.RedactionCount
            }, transaction);

        foreach (var safeEvent in safeEvents)
        {
            var item = safeEvent.Event;
            var traceUuid = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO captured_events
                  (observation_uuid, session_id, agent_id, namespace, part_key, part_order,
                   kind, actor, occurred_at, payload, payload_version)
                VALUES
                  (@observationUuid, @sessionId, @agentId, @EffectiveNamespace, @PartKey, @PartOrder,
                   @Kind, @Actor, @OccurredAt, CAST(@payload AS jsonb), 1)
                RETURNING trace_uuid
                """,
                new
                {
                    observationUuid,
                    sessionId = $"capture:{binding.BindingUuid}:{request.SourceSessionId}",
                    agentId = binding.AgentId,
                    stream.EffectiveNamespace,
                    item.PartKey,
                    item.PartOrder,
                    item.Kind,
                    item.Actor,
                    item.OccurredAt,
                    payload = safeEvent.Payload
                }, transaction);
            foreach (var safeRelationship in safeEvent.Relationships)
            {
                var relationship = safeRelationship.Relationship;
                await connection.ExecuteAsync(
                    """
                    INSERT INTO captured_event_relationships
                      (source_trace_uuid, relationship_type, target_native_id, target_kind)
                    VALUES (@traceUuid, @Type, @TargetNativeId, @TargetKind)
                    """,
                    new
                    {
                        traceUuid,
                        relationship.Type,
                        TargetNativeId = safeRelationship.TargetNativeId,
                        TargetKind = safeRelationship.TargetKind
                    }, transaction);
            }
        }

        await connection.ExecuteAsync(
            """
            UPDATE capture_source_streams
            SET checkpoint_position = @SourcePosition, updated_at = now()
            WHERE stream_uuid = @StreamUuid
            """,
            new { request.SourcePosition, stream.StreamUuid }, transaction);
        var observation = await LoadObservationReceiptAsync(
            connection, observationUuid, transaction);
        var receipts = await LoadEventReceiptsAsync(
            connection, observationUuid, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new CaptureReceipt(
            observationUuid, "new", request.SourcePosition,
            stream.EffectiveNamespace, stream.RouteBasis, observation, receipts);
    }

    public async Task<CaptureReceiptRecord> ReadReceiptAsync(
        Guid observationUuid,
        CancellationToken cancellationToken = default)
    {
        EnsureSafetyConfigured();
        await using var connection = new NpgsqlConnection(connectionString);
        var row = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(
            """
            SELECT o.observation_uuid AS ObservationUuid, b.stable_name AS StableName,
                   b.harness, s.source_session_id AS SourceSessionId,
                   o.source_position AS SourcePosition, o.source_locator AS SourceLocator,
                   o.effective_namespace AS EffectiveNamespace,
                   o.route_basis AS RouteBasis, o.safe_source_payload::text AS SafeSourcePayload,
                   o.scan_status AS ScanStatus,
                   o.scan_rule_set_version AS ScanRuleSetVersion,
                   o.scan_rule_ids AS ScanRuleIds, o.scan_categories AS ScanCategories,
                   o.scan_redaction_count AS ScanRedactionCount,
                   o.captured_at AS CapturedAt
            FROM capture_observations o
            JOIN capture_source_streams s USING (stream_uuid)
            JOIN capture_source_bindings b USING (binding_uuid)
            WHERE o.observation_uuid = @observationUuid
            """,
            new { observationUuid });
        if (row is null)
        {
            throw new InvalidOperationException($"Capture observation '{observationUuid}' was not found.");
        }

        var observation = await LoadObservationReceiptAsync(connection, observationUuid);
        var events = await LoadEventReceiptsAsync(connection, observationUuid);
        return new CaptureReceiptRecord(
            row.ObservationUuid, row.StableName, row.Harness, row.SourceSessionId,
            row.SourcePosition, row.SourceLocator, row.EffectiveNamespace, row.RouteBasis, "new",
            row.ScanStatus, row.ScanRuleSetVersion, row.ScanRuleIds, row.ScanCategories,
            row.ScanRedactionCount,
            row.SafeSourcePayload, row.CapturedAt, observation, events);
    }

    private static void Validate(BindingRow binding, CaptureObservationRequest request)
    {
        if (request.ContractVersion != 1)
        {
            throw new InvalidOperationException("Only capture contractVersion 1 is supported.");
        }
        Require(request.SourceSessionId, nameof(request.SourceSessionId));
        Require(request.SourceLocator, nameof(request.SourceLocator));
        if (!string.Equals(binding.Harness, request.Source.Harness, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Source harness does not match the authenticated binding.");
        }
        if (request.Events.Count == 0)
        {
            throw new InvalidOperationException("An observation must contain at least one event.");
        }
        if (request.Events.Select(item => item.PartKey).Distinct(StringComparer.Ordinal).Count() != request.Events.Count)
        {
            throw new InvalidOperationException("Event partKey values must be unique within an observation.");
        }
        if (request.SourcePosition < 0)
        {
            throw new InvalidOperationException("sourcePosition must be zero or greater.");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }
    }

    private void EnsureSafetyConfigured()
    {
        if (!neverStore.IsConfigured)
        {
            throw new InvalidOperationException(
                "Capture safety rules are missing or empty; capture fails closed.");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sign(string value, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private void AssertSafe(string value, ScanAccumulator scan)
    {
        var result = neverStore.Scan(value);
        scan.Add(result);
        if (result.RedactionCount > 0)
        {
            neverStore.AssertAllowed(value);
        }
    }

    private string Redact(string value, ScanAccumulator scan)
    {
        var result = neverStore.Scan(value);
        scan.Add(result);
        return result.Redacted;
    }

    private static async Task<CaptureObservationReceipt> LoadObservationReceiptAsync(
        NpgsqlConnection connection,
        Guid observationUuid,
        NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleAsync<ObservationReceiptRow>(
            """
            SELECT o.observation_uuid AS ObservationUuid,
                   o.stream_uuid AS SourceStreamUuid,
                   o.source_position AS SourcePosition,
                   o.source_locator AS SourceLocator,
                   o.source::text AS SourceJson,
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
        return new CaptureObservationReceipt(
            row.ObservationUuid,
            row.SourceStreamUuid,
            row.SourcePosition,
            row.SourceLocator,
            JsonSerializer.Deserialize<CaptureSource>(row.SourceJson, JsonOptions)!,
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

    private static async Task<IReadOnlyList<CaptureEventReceipt>> LoadEventReceiptsAsync(
        NpgsqlConnection connection,
        Guid observationUuid,
        NpgsqlTransaction? transaction = null)
    {
        var events = (await connection.QueryAsync<EventReceiptRow>(
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
        var receipts = new List<CaptureEventReceipt>(events.Count);
        foreach (var item in events)
        {
            var relationships = (await connection.QueryAsync<CaptureRelationship>(
                """
                SELECT relationship_type AS Type, target_native_id AS TargetNativeId,
                       target_kind AS TargetKind
                FROM captured_event_relationships
                WHERE source_trace_uuid = @TraceUuid
                ORDER BY relationship_type, target_native_id
                """,
                new { item.TraceUuid }, transaction)).AsList();
            receipts.Add(new CaptureEventReceipt(
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
                JsonDocument.Parse(item.PayloadJson).RootElement.Clone(),
                relationships));
        }
        return receipts;
    }

    private sealed class BindingRow
    {
        public Guid BindingUuid { get; set; }
        public string StableName { get; set; } = "";
        public string Harness { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string? RouteNamespace { get; set; }
        public string[] AllowedNamespaces { get; set; } = [];
        public byte[] ContentSignatureKey { get; set; } = [];
    }
    private sealed class StreamRow
    {
        public Guid StreamUuid { get; set; }
        public string EffectiveNamespace { get; set; } = "";
        public string RouteBasis { get; set; } = "";
        public long? CheckpointPosition { get; set; }
    }
    private sealed class ExistingObservation
    {
        public Guid ObservationUuid { get; set; }
        public long SourcePosition { get; set; }
        public string SourceLocator { get; set; } = "";
        public string ContentSignature { get; set; } = "";
    }
    private sealed class ReceiptRow
    {
        public Guid ObservationUuid { get; set; }
        public string StableName { get; set; } = "";
        public string Harness { get; set; } = "";
        public string SourceSessionId { get; set; } = "";
        public long SourcePosition { get; set; }
        public string SourceLocator { get; set; } = "";
        public string EffectiveNamespace { get; set; } = "";
        public string RouteBasis { get; set; } = "";
        public string SafeSourcePayload { get; set; } = "";
        public string ScanStatus { get; set; } = "";
        public string ScanRuleSetVersion { get; set; } = "";
        public string[] ScanRuleIds { get; set; } = [];
        public string[] ScanCategories { get; set; } = [];
        public int ScanRedactionCount { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
    }
    private sealed class EventReceiptRow
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
    private sealed class ObservationReceiptRow
    {
        public Guid ObservationUuid { get; set; }
        public Guid SourceStreamUuid { get; set; }
        public long SourcePosition { get; set; }
        public string SourceLocator { get; set; } = "";
        public string SourceJson { get; set; } = "";
        public string AdapterJson { get; set; } = "";
        public string SafeSourcePayloadJson { get; set; } = "";
        public string ScanStatus { get; set; } = "";
        public string ScanRuleSetVersion { get; set; } = "";
        public string[] ScanRuleIds { get; set; } = [];
        public string[] ScanCategories { get; set; } = [];
        public int ScanRedactionCount { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
    }
    private sealed record SafeEvent(
        CaptureEvent Event, string Payload, IReadOnlyList<SafeRelationship> Relationships);
    private sealed record SafeRelationship(
        CaptureRelationship Relationship, string TargetNativeId, string? TargetKind);
    private sealed record CaptureSignatureContent(
        int ContractVersion,
        string SourceSessionId,
        string SourceLocator,
        CaptureSource Source,
        CaptureAdapter Adapter,
        JsonElement SourcePayload,
        IReadOnlyList<CaptureEvent> Events);

    private sealed class ScanAccumulator(string ruleSetVersion)
    {
        public string RuleSetVersion { get; } = ruleSetVersion;
        public SortedSet<string> RuleIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Categories { get; } = new(StringComparer.Ordinal);
        public int RedactionCount { get; private set; }

        public void Add(NeverStoreScan scan)
        {
            RuleIds.UnionWith(scan.RuleIds);
            Categories.UnionWith(scan.Categories);
            RedactionCount += scan.RedactionCount;
        }
    }
}
