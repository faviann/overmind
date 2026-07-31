using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed class CaptureConflictException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}

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
        ValidateMandatory(binding, command);
        CaptureObservationCommand originalCommand = command;
        BoundedCaptureRepresentation<CaptureObservationCommand> bounded =
            CaptureFidelityPolicy.SerializeForContent(
                originalCommand,
                neverStore.Budgets.MaxObservationBytes);
        string inputJson = bounded.Serialized;
        long originalByteCount = bounded.OriginalByteCount;
        command = bounded.Observation;
        ValidateSemantic(command);
        CaptureObservationCommand signatureCommand =
            bounded.WasOmitted ? originalCommand : command;

        string contentSignature = Sign(
            new CaptureSignatureContent(
                signatureCommand.ContractVersion,
                signatureCommand.SourceIdentity,
                signatureCommand.Locator,
                signatureCommand.SourceTimestamp,
                signatureCommand.Source,
                signatureCommand.Adapter,
                signatureCommand.SourcePayload,
                signatureCommand.Events,
                signatureCommand.RouteEvidence),
            binding.ContentSignatureKey);
        bool observationWasOmitted = bounded.WasOmitted;
        // The fidelity policy already proves the chosen serialized
        // representation fits. The gate independently enforces its configured
        // observation budget before scanning.
        neverStore.AssertObservationWithinBudget(inputJson);
        var scan = new ScanAccumulator(neverStore.RuleSetVersion);
        if (observationWasOmitted)
        {
            scan.Omit(CaptureFidelityPolicy.ContentLimitReason);
        }
        AssertSafe(command.SourceIdentity.ExternalSessionId, scan);
        if (command.SourceIdentity.ChildId is not null)
        {
            AssertSafe(command.SourceIdentity.ChildId, scan);
        }
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
        string source = RedactJson(
            JsonSerializer.Serialize(command.Source, CaptureLedger.JsonOptions), scan);
        string adapter = RedactJson(
            JsonSerializer.Serialize(command.Adapter, CaptureLedger.JsonOptions), scan);
        var routeEvidenceScan = neverStore.ScanJson(
            JsonSerializer.Serialize(command.RouteEvidence, CaptureLedger.JsonOptions));
        scan.Add(routeEvidenceScan);
        string routeEvidence = routeEvidenceScan.Redacted;
        CaptureRouteEvidence? safeRouteEvidence =
            routeEvidenceScan.RedactionCount == 0
            && routeEvidenceScan.OmissionReasons.Count == 0
                ? command.RouteEvidence
                : null;
        string safePayload = RedactJson(command.SourcePayload.GetRawText(), scan);
        var safeEvents = command.Events.Select(item => new SafeEvent(
            item,
            RedactJson(item.Payload.GetRawText(), scan),
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
        Guid deterministicStreamUuid = DeterministicUuid(
            binding.BindingUuid, command.SourceIdentity, "capture-source-stream/v1");
        var stream = await connection.QuerySingleOrDefaultAsync<StreamRow>(
            """
            SELECT stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                   route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition,
                   source_session_id AS SourceSessionId,
                   trace_session_id AS TraceSessionId
            FROM capture_source_streams
            WHERE binding_uuid = @BindingUuid
              AND external_session_id = @ExternalSessionId
              AND child_id IS NOT DISTINCT FROM @ChildId
            """,
            new
            {
                binding.BindingUuid,
                command.SourceIdentity.ExternalSessionId,
                command.SourceIdentity.ChildId
            }, transaction);
        bool streamWasEstablished = stream is not null;
        if (stream is null)
        {
            var route = await CaptureRouteResolver.ResolveAsync(
                connection, transaction, binding, safeRouteEvidence);
            stream = await connection.QuerySingleOrDefaultAsync<StreamRow>(
                """
                INSERT INTO capture_source_streams
                  (stream_uuid, binding_uuid, source_session_id, external_session_id,
                   child_id, trace_session_id, effective_namespace, route_basis)
                VALUES (@StreamUuid, @BindingUuid, @ExternalSessionId, @ExternalSessionId,
                        @ChildId, @TraceSessionId, @Namespace, @Basis)
                ON CONFLICT ON CONSTRAINT
                  capture_source_streams_binding_external_child_unique DO NOTHING
                RETURNING stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                          route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition,
                          source_session_id AS SourceSessionId,
                          trace_session_id AS TraceSessionId
                """,
                new
                {
                    binding.BindingUuid,
                    StreamUuid = deterministicStreamUuid,
                    command.SourceIdentity.ExternalSessionId,
                    command.SourceIdentity.ChildId,
                    TraceSessionId = CanonicalSessionId(
                        binding.BindingUuid, command.SourceIdentity),
                    route.Namespace,
                    route.Basis
                }, transaction);
            if (stream is null)
            {
                streamWasEstablished = true;
                stream = await connection.QuerySingleAsync<StreamRow>(
                    """
                    SELECT stream_uuid AS StreamUuid, effective_namespace AS EffectiveNamespace,
                           route_basis AS RouteBasis, checkpoint_position AS CheckpointPosition,
                           source_session_id AS SourceSessionId,
                           trace_session_id AS TraceSessionId
                    FROM capture_source_streams
                    WHERE binding_uuid = @BindingUuid
                      AND external_session_id = @ExternalSessionId
                      AND child_id IS NOT DISTINCT FROM @ChildId
                    """,
                    new
                    {
                        binding.BindingUuid,
                        command.SourceIdentity.ExternalSessionId,
                        command.SourceIdentity.ChildId
                    }, transaction);
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
                        locatorMatch.ContentSignature, contentSignature, StringComparison.Ordinal)
                    && !CompatibleContentSignatures(
                            signatureCommand,
                            stream.SourceSessionId,
                            binding.ContentSignatureKey)
                        .Contains(locatorMatch.ContentSignature, StringComparer.Ordinal))
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
                "blocked_by_earlier_gap",
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
                ScanStatus = scan.Status,
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
                    sessionId = stream.TraceSessionId,
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
        new("accepted_source_conflict",
            $"Source position {command.SourcePosition} was already accepted with " +
            "different identity or content.");

    private static void ValidateMandatory(
        CaptureBindingContext binding,
        CaptureObservationCommand command)
    {
        if (command.ContractVersion != 1)
        {
            throw new InvalidOperationException("Only capture contractVersion 1 is supported.");
        }
        CaptureLedger.Require(
            command.SourceIdentity.ExternalSessionId,
            "sourceIdentity.externalSessionId");
        if (command.SourceIdentity.ChildId is not null)
        {
            CaptureLedger.Require(command.SourceIdentity.ChildId, "sourceIdentity.childId");
        }
        if (!string.Equals(binding.Harness, command.Source.Harness, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Source harness does not match the authenticated binding.");
        }
        if (command.SourcePosition < 0)
        {
            throw new InvalidOperationException("sourcePosition must be zero or greater.");
        }
        _ = command.Locator switch
        {
            CaptureSourceLocator.NativeId nativeId =>
                CaptureSourceLocator.Parse(
                    new CaptureLocator("native_id", nativeId.Value, null, null, null)),
            CaptureSourceLocator.ByteRange range =>
                CaptureSourceLocator.Parse(
                    new CaptureLocator(
                        "byte_range",
                        null,
                        range.Offset,
                        range.Length,
                        range.SourceContentSha256)),
            _ => throw new ArgumentException("locator.kind must be native_id or byte_range.")
        };
    }

    private static void ValidateSemantic(CaptureObservationCommand command)
    {
        if (command.SourceTimestamp is not null)
        {
            CaptureLedger.Require(command.SourceTimestamp.Raw, "sourceTimestamp.raw");
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
    }

    private static string Sign<T>(T value, byte[] key)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        using var stream = new HashingSerializationStream(
            hash,
            SafetyBudgets.Default.MaxScanTime);
        JsonSerializer.Serialize(stream, value, CaptureLedger.JsonOptions);
        stream.AssertWithinDeadline();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // For every record kind whose derived events a version bump left alone,
    // substituting the prior adapter identity reconstructs its accepted
    // signature. A record whose derived events changed does not reconstruct
    // and remains a conflict.
    private static readonly string[] PreVersion7CodexAdapterVersions = ["3", "4", "5", "6"];

    private static IReadOnlyList<string> CompatibleContentSignatures(
        CaptureObservationCommand command,
        string legacySourceSessionId,
        byte[] key)
    {
        if (!string.Equals(command.Source.Harness, "codex", StringComparison.Ordinal)
            || !string.Equals(
                command.Adapter.Name, "codex-synthetic-jsonl", StringComparison.Ordinal)
            || command.Adapter.Version is not ("7" or "8"))
        {
            return [];
        }

        string[] priorVersions = string.Equals(
            command.Adapter.Version, "8", StringComparison.Ordinal)
                ? [.. PreVersion7CodexAdapterVersions, "7"]
                : PreVersion7CodexAdapterVersions;
        var signatures = new List<string>(priorVersions.Length * 2);
        foreach (string version in priorVersions)
        {
            var legacyAdapter = command.Adapter with { Version = version };
            signatures.Add(Sign(
                new CaptureSignatureContent(
                    command.ContractVersion,
                    command.SourceIdentity,
                    command.Locator,
                    command.SourceTimestamp,
                    command.Source,
                    legacyAdapter,
                    command.SourcePayload,
                    command.Events,
                    command.RouteEvidence),
                key));
            signatures.Add(Sign(
                new LegacyCaptureSignatureContent(
                    command.ContractVersion,
                    legacySourceSessionId,
                    command.Locator,
                    command.SourceTimestamp,
                    command.Source,
                    legacyAdapter,
                    command.SourcePayload,
                    command.Events,
                    command.RouteEvidence),
                key));
        }

        return signatures;
    }

    private static string CanonicalSessionId(
        Guid bindingUuid, CaptureSourceIdentity sourceIdentity) =>
        $"capture:v1:{IdentityDigest(bindingUuid, sourceIdentity, "capture-trace-session/v1")}";

    private static Guid DeterministicUuid(
        Guid bindingUuid, CaptureSourceIdentity sourceIdentity, string domain)
    {
        byte[] bytes = Convert.FromHexString(IdentityDigest(bindingUuid, sourceIdentity, domain));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static string IdentityDigest(
        Guid bindingUuid, CaptureSourceIdentity sourceIdentity, string domain)
    {
        string material = JsonSerializer.Serialize(new
        {
            domain,
            bindingUuid,
            sourceIdentity.ExternalSessionId,
            sourceIdentity.ChildId
        }, CaptureLedger.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    // A required identity value is not payload: it cannot be redacted or
    // omitted and still mean what it claims, so a match rejects and an
    // un-inspectable value fails the whole import closed.
    private void AssertSafe(string value, ScanAccumulator scan)
    {
        var result = neverStore.Scan(value);
        scan.Add(result);
        if (result.OmissionReasons.Count > 0 || result.RedactionCount > 0)
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

    // Structured payloads are scanned leaf by leaf and rebuilt; the serialized
    // JSON is never regex-rewritten.
    private string RedactJson(string json, ScanAccumulator scan)
    {
        var result = neverStore.ScanJson(json);
        scan.Add(result);
        return result.Redacted;
    }

    private sealed class StreamRow
    {
        public Guid StreamUuid { get; set; }
        public string EffectiveNamespace { get; set; } = "";
        public string RouteBasis { get; set; } = "";
        public long? CheckpointPosition { get; set; }
        public string SourceSessionId { get; set; } = "";
        public string TraceSessionId { get; set; } = "";
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
        CaptureSourceIdentity SourceIdentity,
        CaptureSourceLocator Locator,
        CaptureSourceTimestamp? SourceTimestamp,
        CaptureSource Source,
        CaptureAdapter Adapter,
        JsonElement SourcePayload,
        IReadOnlyList<CaptureEvent> Events,
        CaptureRouteEvidence? RouteEvidence);
    private sealed record LegacyCaptureSignatureContent(
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
        public SortedSet<string> Omissions { get; } = new(StringComparer.Ordinal);

        // Provenance only: rule ids, categories, counts, and omission reasons.
        // Never the matched value, an unsafe excerpt, or a content digest.
        public string Status => Omissions.Count > 0
            ? "omitted"
            : RedactionCount == 0 ? "clean" : "redacted";

        public void Add(NeverStoreScan scan)
        {
            RuleIds.UnionWith(scan.RuleIds);
            Categories.UnionWith(scan.Categories);
            RedactionCount += scan.RedactionCount;
            foreach (string reason in scan.OmissionReasons)
            {
                Omissions.Add(reason);
                RuleIds.Add($"omission:{reason}");
            }
        }

        public void Omit(string reason)
        {
            Omissions.Add(reason);
            RuleIds.Add($"omission:{reason}");
        }
    }
}
