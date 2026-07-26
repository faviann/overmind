using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed class CaptureConflictException(string message) : Exception(message);

/// <summary>
/// Authenticated capture ingestion: validate one observation against its
/// binding, apply the safety gate, and append the observation, its canonical
/// events and relationships, and the stream checkpoint in one transaction.
/// The caller supplies an already-resolved <see cref="CaptureBindingContext"/>;
/// this module never sees a raw credential.
/// </summary>
public sealed class CaptureIngestion(string connectionString, NeverStoreGate neverStore)
{
    public async Task<CaptureImportReceipt> ImportAsync(
        CaptureBindingContext binding,
        CaptureObservationCommand command,
        CancellationToken cancellationToken = default)
    {
        CaptureLedger.RequireSafetyConfigured(neverStore);
        Validate(binding, command);
        string inputJson = JsonSerializer.Serialize(command, CaptureLedger.JsonOptions);
        if (Encoding.UTF8.GetByteCount(inputJson) > 1_000_000)
        {
            throw new InvalidOperationException("Capture observation exceeds the 1000000-byte non-production limit.");
        }

        string signatureContent = JsonSerializer.Serialize(
            new CaptureSignatureContent(
                command.ContractVersion,
                command.SourceSessionId,
                command.Locator,
                command.SourceTimestamp,
                command.Source,
                command.Adapter,
                command.SourcePayload,
                command.Events,
                command.RouteEvidence),
            CaptureLedger.JsonOptions);
        string contentSignature = Sign(signatureContent, binding.ContentSignatureKey);
        var scan = new ScanAccumulator(neverStore.RuleSetVersion);
        AssertSafe(command.SourceSessionId, scan);
        AssertSafe(command.Locator.Kind, scan);
        switch (command.Locator)
        {
            case CaptureSourceLocator.NativeId nativeId:
                AssertSafe(nativeId.Value, scan);
                break;
            case CaptureSourceLocator.ByteRange { SourceContentSha256: { } digest }:
                AssertSafe(digest, scan);
                break;
        }
        if (command.SourceTimestamp is not null)
        {
            AssertSafe(command.SourceTimestamp.Raw, scan);
        }
        foreach (var item in command.Events)
        {
            AssertSafe(item.PartKey, scan);
            AssertSafe(item.Kind, scan);
            AssertSafe(item.Actor, scan);
            foreach (var relationship in item.Relationships ?? [])
            {
                AssertSafe(relationship.Type, scan);
            }
        }
        string source = Redact(
            JsonSerializer.Serialize(command.Source, CaptureLedger.JsonOptions), scan);
        string adapter = Redact(
            JsonSerializer.Serialize(command.Adapter, CaptureLedger.JsonOptions), scan);
        var routeEvidenceScan = neverStore.Scan(
            JsonSerializer.Serialize(command.RouteEvidence, CaptureLedger.JsonOptions));
        scan.Add(routeEvidenceScan);
        string routeEvidence = routeEvidenceScan.Redacted;
        CaptureRouteEvidence? safeRouteEvidence =
            routeEvidenceScan.RedactionCount == 0 ? command.RouteEvidence : null;
        string safePayload = Redact(command.SourcePayload.GetRawText(), scan);
        var safeEvents = command.Events.Select(item => new SafeEvent(
            item,
            Redact(item.Payload.GetRawText(), scan),
            (item.Relationships ?? []).Select(relationship => new SafeRelationship(
                relationship,
                Redact(relationship.Target.NativeId, scan),
                relationship.Target.Kind is null
                    ? null
                    : Redact(relationship.Target.Kind, scan))).ToArray()
        )).ToArray();

        var locator = command.Locator.ToColumns();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stream = await connection.QuerySingleOrDefaultAsync<StreamRow>(
            """
            SELECT stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                   route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition
            FROM capture_source_streams
            WHERE binding_uuid = @BindingUuid AND source_session_id = @SourceSessionId
            """,
            new { binding.BindingUuid, command.SourceSessionId }, transaction);
        bool streamWasEstablished = stream is not null;
        if (stream is null)
        {
            var route = await CaptureRouteResolver.ResolveAsync(
                connection, transaction, binding, safeRouteEvidence);
            stream = await connection.QuerySingleOrDefaultAsync<StreamRow>(
                """
                INSERT INTO capture_source_streams
                  (binding_uuid, source_session_id, effective_namespace, route_basis)
                VALUES (@BindingUuid, @SourceSessionId, @Namespace, @Basis)
                ON CONFLICT (binding_uuid, source_session_id) DO NOTHING
                RETURNING stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                          route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition
                """,
                new
                {
                    binding.BindingUuid,
                    command.SourceSessionId,
                    route.Namespace,
                    route.Basis
                }, transaction);
            if (stream is null)
            {
                streamWasEstablished = true;
                stream = await connection.QuerySingleAsync<StreamRow>(
                    """
                    SELECT stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                           route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition
                    FROM capture_source_streams
                    WHERE binding_uuid = @BindingUuid AND source_session_id = @SourceSessionId
                    """,
                    new { binding.BindingUuid, command.SourceSessionId }, transaction);
            }
        }
        string publicRouteBasis = streamWasEstablished ? "established" : stream.RouteBasis;

        var existingMatches = (await connection.QueryAsync<ExistingObservation>(
            """
            SELECT observation_uuid AS ObservationUuid, source_position AS SourcePosition,
                   locator_kind AS LocatorKind, locator_native_id AS LocatorNativeId,
                   locator_byte_offset AS LocatorByteOffset,
                   locator_byte_length AS LocatorByteLength,
                   content_signature AS ContentSignature
            FROM capture_observations
            WHERE stream_uuid = @StreamUuid
              AND (
                source_position = @SourcePosition
                OR (
                  locator_kind = @Kind
                  AND locator_native_id IS NOT DISTINCT FROM @NativeId
                  AND locator_byte_offset IS NOT DISTINCT FROM @ByteOffset
                  AND locator_byte_length IS NOT DISTINCT FROM @ByteLength
                )
              )
            """,
            new
            {
                stream.StreamUuid,
                command.SourcePosition,
                locator.Kind,
                locator.NativeId,
                locator.ByteOffset,
                locator.ByteLength
            }, transaction)).AsList();
        if (existingMatches.Count > 0)
        {
            var locatorMatch = existingMatches.SingleOrDefault(candidate => candidate.Matches(locator));
            if (locatorMatch is not null)
            {
                if (!string.Equals(
                    locatorMatch.ContentSignature, contentSignature, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw ConflictAt(command);
                }

                var oldObservation = await CaptureLedger.LoadObservationAsync(
                    connection, locatorMatch.ObservationUuid, transaction);
                var oldEvents = await CaptureLedger.LoadEventsAsync(
                    connection, locatorMatch.ObservationUuid, transaction);
                await transaction.CommitAsync(cancellationToken);
                return new CaptureImportReceipt(
                    locatorMatch.ObservationUuid, "already_accepted", locatorMatch.SourcePosition,
                    stream.EffectiveNamespace, "established", oldObservation!, oldEvents);
            }

            await transaction.RollbackAsync(cancellationToken);
            throw ConflictAt(command);
        }

        long expectedPosition = (stream.CheckpointPosition ?? -1) + 1;
        if (command.SourcePosition != expectedPosition)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CaptureConflictException(
                $"Capture stream expected sourcePosition {expectedPosition} but received " +
                $"{command.SourcePosition}; gaps and backtracking are not accepted.");
        }

        var observationUuid = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_observations
              (stream_uuid, source_position, locator_kind, locator_native_id,
               locator_byte_offset, locator_byte_length,
               source_timestamp_raw, source_timestamp_parsed, content_signature,
               effective_namespace, route_basis, source, route_evidence, adapter, safe_source_payload,
               scan_status, scan_rule_set_version, scan_rule_ids, scan_categories,
               scan_redaction_count)
            VALUES
              (@StreamUuid, @SourcePosition, @Kind, @NativeId,
               @ByteOffset, @ByteLength, @SourceTimestampRaw, @SourceTimestampParsed,
               @contentSignature,
               @EffectiveNamespace, @RouteBasis, CAST(@source AS jsonb),
               CAST(@routeEvidence AS jsonb), CAST(@adapter AS jsonb),
               CAST(@safePayload AS jsonb), @ScanStatus, @RuleSetVersion, @RuleIds,
               @Categories, @RedactionCount)
            RETURNING observation_uuid
            """,
            new
            {
                stream.StreamUuid,
                command.SourcePosition,
                locator.Kind,
                locator.NativeId,
                locator.ByteOffset,
                locator.ByteLength,
                SourceTimestampRaw = command.SourceTimestamp?.Raw,
                SourceTimestampParsed = command.SourceTimestamp?.Parsed,
                contentSignature,
                stream.EffectiveNamespace,
                stream.RouteBasis,
                source,
                routeEvidence,
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
                    sessionId = $"capture:{binding.BindingUuid}:{command.SourceSessionId}",
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
                      (source_trace_uuid, relationship_type, target_source_stream_uuid,
                       target_native_id, target_kind)
                    VALUES
                      (@traceUuid, @Type, @TargetSourceStreamUuid, @TargetNativeId, @TargetKind)
                    """,
                    new
                    {
                        traceUuid,
                        relationship.Type,
                        TargetSourceStreamUuid = relationship.Target.SourceStreamUuid,
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
            new { command.SourcePosition, stream.StreamUuid }, transaction);
        var observation = await CaptureLedger.LoadObservationAsync(
            connection, observationUuid, transaction);
        var receipts = await CaptureLedger.LoadEventsAsync(
            connection, observationUuid, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new CaptureImportReceipt(
            observationUuid, "new", command.SourcePosition,
            stream.EffectiveNamespace, publicRouteBasis, observation!, receipts);
    }

    private static CaptureConflictException ConflictAt(CaptureObservationCommand command) =>
        new($"Source position {command.SourcePosition} or locator '{command.Locator.Describe()}' " +
            "was already accepted with different identity or content.");

    private static void Validate(CaptureBindingContext binding, CaptureObservationCommand command)
    {
        if (command.ContractVersion != 1)
        {
            throw new InvalidOperationException("Only capture contractVersion 1 is supported.");
        }
        CaptureLedger.Require(command.SourceSessionId, nameof(command.SourceSessionId));
        if (command.SourceTimestamp is not null)
        {
            CaptureLedger.Require(command.SourceTimestamp.Raw, "sourceTimestamp.raw");
        }
        if (!string.Equals(binding.Harness, command.Source.Harness, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Source harness does not match the authenticated binding.");
        }
        if (command.Events.Count == 0)
        {
            throw new InvalidOperationException("An observation must contain at least one event.");
        }
        if (command.Events.Select(item => item.PartKey).Distinct(StringComparer.Ordinal).Count()
            != command.Events.Count)
        {
            throw new InvalidOperationException("Event partKey values must be unique within an observation.");
        }
        foreach (var relationship in command.Events.SelectMany(item => item.Relationships ?? []))
        {
            CaptureLedger.Require(relationship.Type, "relationship.type");
            if (relationship.Target is null)
            {
                throw new ArgumentException("relationship.target is required.");
            }
            CaptureLedger.Require(relationship.Target.NativeId, "relationship.target.nativeId");
        }
        if (command.SourcePosition < 0)
        {
            throw new InvalidOperationException("sourcePosition must be zero or greater.");
        }
    }

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
        public string LocatorKind { get; set; } = "";
        public string? LocatorNativeId { get; set; }
        public long? LocatorByteOffset { get; set; }
        public long? LocatorByteLength { get; set; }
        public string ContentSignature { get; set; } = "";

        public bool Matches(CaptureSourceLocator.Columns locator) =>
            string.Equals(LocatorKind, locator.Kind, StringComparison.Ordinal)
            && string.Equals(LocatorNativeId, locator.NativeId, StringComparison.Ordinal)
            && LocatorByteOffset == locator.ByteOffset
            && LocatorByteLength == locator.ByteLength;
    }

    private sealed record SafeEvent(
        CaptureEvent Event, string Payload, IReadOnlyList<SafeRelationship> Relationships);
    private sealed record SafeRelationship(
        CaptureRelationship Relationship, string TargetNativeId, string? TargetKind);
    // Mirrors CaptureObservationCommand except for SourcePosition, which is
    // deliberately excluded: the retry signature covers source identity and
    // content, not the stream position the record happened to arrive at.
    private sealed record CaptureSignatureContent(
        int ContractVersion,
        string SourceSessionId,
        CaptureSourceLocator Locator,
        CaptureSourceTimestamp? SourceTimestamp,
        CaptureSource Source,
        CaptureAdapter Adapter,
        JsonElement SourcePayload,
        IReadOnlyList<CaptureEvent> Events,
        CaptureRouteEvidence? RouteEvidence);

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
