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
        EnsureSafetyConfigured();
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
        EnsureSafetyConfigured();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var binding = await connection.QuerySingleOrDefaultAsync<BindingRow>(
            """
            SELECT binding_uuid AS BindingUuid, stable_name AS StableName, harness,
                   agent_id AS AgentId, route_namespace AS RouteNamespace,
                   allowed_namespaces AS AllowedNamespaces
            FROM capture_source_bindings
            WHERE credential_hash = @credentialHash AND active
            """,
            new { credentialHash = Hash(credential) });
        if (binding is null)
        {
            return null;
        }

        Validate(binding, request);
        string inputJson = JsonSerializer.Serialize(request, JsonOptions);
        if (Encoding.UTF8.GetByteCount(inputJson) > 1_000_000)
        {
            throw new InvalidOperationException("Capture observation exceeds the 1000000-byte non-production limit.");
        }

        string contentHash = Hash(inputJson);
        neverStore.AssertAllowed(request.SourceSessionId);
        neverStore.AssertAllowed(request.SourceLocator);
        foreach (var item in request.Events)
        {
            neverStore.AssertAllowed(item.PartKey);
            neverStore.AssertAllowed(item.Kind);
            neverStore.AssertAllowed(item.Actor);
            foreach (var relationship in item.Relationships ?? [])
            {
                neverStore.AssertAllowed(relationship.Type);
            }
        }
        string safePayload = neverStore.Redact(request.SourcePayload.GetRawText());
        string scanStatus = safePayload == request.SourcePayload.GetRawText() ? "clean" : "redacted";
        string effectiveNamespace = binding.RouteNamespace ?? "capture/unscoped";
        string routeBasis = binding.RouteNamespace is null ? "fallback" : "configured_binding";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var streamUuid = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_source_streams (binding_uuid, source_session_id)
            VALUES (@BindingUuid, @SourceSessionId)
            ON CONFLICT (binding_uuid, source_session_id)
            DO UPDATE SET updated_at = capture_source_streams.updated_at
            RETURNING stream_uuid
            """,
            new { binding.BindingUuid, request.SourceSessionId }, transaction);

        var existing = await connection.QuerySingleOrDefaultAsync<ExistingObservation>(
            """
            SELECT observation_uuid AS ObservationUuid, content_hash AS ContentHash
            FROM capture_observations
            WHERE stream_uuid = @streamUuid AND source_locator = @SourceLocator
            """,
            new { streamUuid, request.SourceLocator }, transaction);
        if (existing is not null)
        {
            if (!string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new CaptureConflictException(
                    $"Source locator '{request.SourceLocator}' was already accepted with different content.");
            }

            var oldEvents = (await connection.QueryAsync<CaptureEventReceipt>(
                """
                SELECT trace_uuid AS TraceUuid, part_key AS PartKey
                FROM captured_events WHERE observation_uuid = @ObservationUuid ORDER BY part_order
                """,
                new { existing.ObservationUuid }, transaction)).AsList();
            await transaction.CommitAsync(cancellationToken);
            return new CaptureReceipt(
                existing.ObservationUuid, "already_accepted", effectiveNamespace, routeBasis, oldEvents);
        }

        var observationUuid = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_observations
              (stream_uuid, source_locator, content_hash, effective_namespace, route_basis,
               source, adapter, safe_source_payload, scan_status)
            VALUES
              (@streamUuid, @SourceLocator, @contentHash, @effectiveNamespace, @routeBasis,
               CAST(@source AS jsonb), CAST(@adapter AS jsonb), CAST(@safePayload AS jsonb), @scanStatus)
            RETURNING observation_uuid
            """,
            new
            {
                streamUuid,
                request.SourceLocator,
                contentHash,
                effectiveNamespace,
                routeBasis,
                source = neverStore.Redact(JsonSerializer.Serialize(request.Source, JsonOptions)),
                adapter = neverStore.Redact(JsonSerializer.Serialize(request.Adapter, JsonOptions)),
                safePayload,
                scanStatus
            }, transaction);

        var receipts = new List<CaptureEventReceipt>(request.Events.Count);
        foreach (var item in request.Events)
        {
            string safeEventPayload = neverStore.Redact(item.Payload.GetRawText());
            var traceUuid = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO captured_events
                  (observation_uuid, session_id, agent_id, namespace, part_key, part_order,
                   kind, actor, occurred_at, payload, payload_version)
                VALUES
                  (@observationUuid, @sessionId, @agentId, @effectiveNamespace, @PartKey, @PartOrder,
                   @Kind, @Actor, @OccurredAt, CAST(@payload AS jsonb), 1)
                RETURNING trace_uuid
                """,
                new
                {
                    observationUuid,
                    sessionId = $"capture:{binding.BindingUuid}:{request.SourceSessionId}",
                    agentId = binding.AgentId,
                    effectiveNamespace,
                    item.PartKey,
                    item.PartOrder,
                    item.Kind,
                    item.Actor,
                    item.OccurredAt,
                    payload = safeEventPayload
                }, transaction);
            receipts.Add(new CaptureEventReceipt(traceUuid, item.PartKey));

            foreach (var relationship in item.Relationships ?? [])
            {
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
                        TargetNativeId = neverStore.Redact(relationship.TargetNativeId),
                        TargetKind = relationship.TargetKind is null
                            ? null
                            : neverStore.Redact(relationship.TargetKind)
                    }, transaction);
            }
        }

        await connection.ExecuteAsync(
            """
            UPDATE capture_source_streams
            SET checkpoint_locator = @SourceLocator, updated_at = now()
            WHERE stream_uuid = @streamUuid
            """,
            new { request.SourceLocator, streamUuid }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new CaptureReceipt(observationUuid, "new", effectiveNamespace, routeBasis, receipts);
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
                   o.source_locator AS SourceLocator, o.effective_namespace AS EffectiveNamespace,
                   o.route_basis AS RouteBasis, o.safe_source_payload::text AS SafeSourcePayload,
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

        var events = (await connection.QueryAsync<CaptureEventReceipt>(
            """
            SELECT trace_uuid AS TraceUuid, part_key AS PartKey
            FROM captured_events WHERE observation_uuid = @observationUuid ORDER BY part_order
            """,
            new { observationUuid })).AsList();
        return new CaptureReceiptRecord(
            row.ObservationUuid, row.StableName, row.Harness, row.SourceSessionId,
            row.SourceLocator, row.EffectiveNamespace, row.RouteBasis, "new",
            row.SafeSourcePayload, row.CapturedAt, events);
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
        string effective = binding.RouteNamespace ?? "capture/unscoped";
        if (!binding.AllowedNamespaces.Contains(effective, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Binding route is outside its allowed namespaces.");
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

    private sealed class BindingRow
    {
        public Guid BindingUuid { get; set; }
        public string StableName { get; set; } = "";
        public string Harness { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string? RouteNamespace { get; set; }
        public string[] AllowedNamespaces { get; set; } = [];
    }
    private sealed class ExistingObservation
    {
        public Guid ObservationUuid { get; set; }
        public string ContentHash { get; set; } = "";
    }
    private sealed class ReceiptRow
    {
        public Guid ObservationUuid { get; set; }
        public string StableName { get; set; } = "";
        public string Harness { get; set; } = "";
        public string SourceSessionId { get; set; } = "";
        public string SourceLocator { get; set; } = "";
        public string EffectiveNamespace { get; set; } = "";
        public string RouteBasis { get; set; } = "";
        public string SafeSourcePayload { get; set; } = "";
        public DateTimeOffset CapturedAt { get; set; }
    }
}
