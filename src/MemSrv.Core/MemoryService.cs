using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed class MemoryService(string connectionString, NeverStoreGate neverStore)
{
    private const double RrfK = 60;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ToolEnvelope<TraceResult>> LogTraceAsync(
        MemoryContext context,
        string sessionId,
        string eventType,
        object content,
        Guid[]? refs = null,
        CancellationToken cancellationToken = default)
    {
        var traceUuid = await InsertTraceRawAsync(context.AgentId, context.Namespace, sessionId, eventType, content, refs, cancellationToken);
        return new ToolEnvelope<TraceResult>(
            new TraceResult(traceUuid),
            "If this event contains a durable decision or fact, call propose_memory referencing this trace_uuid as source_id.");
    }

    public async Task<ToolEnvelope<IReadOnlyList<SearchMemoryResult>>> SearchMemoryAsync(
        MemoryContext context,
        string query,
        string[]? namespaces = null,
        string[]? types = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await ValidateOrLogBlockedAsync(context, context.Namespace, "search_memory", new { query, namespaces, types, limit }, cancellationToken);
        await InsertTraceRawAsync(context.AgentId, context.Namespace, context.SessionId, "tool_call", new
        {
            tool = "search_memory",
            @params = new { query, namespaces, types, limit }
        }, null, cancellationToken);

        var searchedNamespaces = namespaces is { Length: > 0 } ? namespaces : [context.Namespace];
        var config = await GetRetrievalConfigAsync(context.AgentId, searchedNamespaces[0], cancellationToken);
        var take = Math.Min(limit ?? config.MaxResults, config.MaxResults);
        var laneNames = config.Lanes.Count == 0 ? ["fts", "recency"] : config.Lanes;

        var laneRows = new Dictionary<string, IReadOnlyList<LaneRow>>();
        if (laneNames.Contains("fts", StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(query))
        {
            laneRows["fts"] = await QueryFtsLaneAsync(context, query, searchedNamespaces, types, config, take * 5, cancellationToken);
        }

        if (laneNames.Contains("recency", StringComparer.OrdinalIgnoreCase))
        {
            laneRows["recency"] = await QueryRecencyLaneAsync(context, searchedNamespaces, types, config, take * 5, cancellationToken);
        }

        var byUuid = new Dictionary<Guid, FusedRow>();
        foreach (var (lane, rows) in laneRows)
        {
            foreach (var row in rows)
            {
                if (!byUuid.TryGetValue(row.Uuid, out var fused))
                {
                    fused = new FusedRow(row);
                    byUuid[row.Uuid] = fused;
                }

                fused.FusedScore += 1.0 / (RrfK + row.Rank);
                fused.LaneScores[lane] = new LaneScore(row.Rank, row.Score);
            }
        }

        var results = byUuid.Values
            .OrderByDescending(row => row.FusedScore)
            .ThenByDescending(row => row.Row.CreatedAt)
            .Take(take)
            .Select(row => new SearchMemoryResult(
                row.Row.Uuid,
                row.Row.Type,
                row.Row.Tier,
                row.Row.Status,
                Preview(row.Row.Content, config.PreviewChars),
                row.Row.SourceType,
                row.Row.SourceId,
                row.Row.Version,
                row.LaneScores,
                row.FusedScore))
            .ToArray();

        return new ToolEnvelope<IReadOnlyList<SearchMemoryResult>>(
            results,
            "Call get_by_id with a uuid to read full content. Nothing relevant? Consider propose_memory to fill the gap.");
    }

    public async Task<ToolEnvelope<MemoryRecord>> GetByIdAsync(
        MemoryContext context,
        Guid uuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MemoryRow>(
            """
            SELECT uuid, namespace, type, visibility, status, tier, content, source_type AS SourceType,
                   source_id AS SourceId, agent_id AS AgentId, session_id AS SessionId, version,
                   supersedes, created_at AS CreatedAt, approved_by AS ApprovedBy,
                   approved_at AS ApprovedAt, retired_at AS RetiredAt,
                   content_hash AS ContentHash, metadata::text AS MetadataJson
            FROM memories
            WHERE uuid = @Uuid
              AND (visibility = 'shared' OR agent_id = @AgentId)
            """,
            new { Uuid = uuid, context.AgentId });

        if (row is null)
        {
            throw new InvalidOperationException($"Memory '{uuid}' was not found.");
        }

        var supersededBy = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT uuid FROM memories WHERE supersedes = @Uuid ORDER BY created_at DESC LIMIT 1",
            new { Uuid = uuid });

        await InsertTraceRawAsync(context.AgentId, row.Namespace, context.SessionId, "memory_consumed", new
        {
            uuid
        }, [uuid], cancellationToken);

        var record = new MemoryRecord(
            row.Uuid,
            row.Namespace,
            row.Type,
            row.Visibility,
            row.Status,
            row.Tier,
            row.Content,
            row.SourceType,
            row.SourceId,
            row.AgentId,
            row.SessionId,
            row.Version,
            row.Supersedes,
            supersededBy,
            row.CreatedAt,
            row.ApprovedBy,
            row.ApprovedAt,
            row.RetiredAt,
            row.ContentHash,
            ParseMetadata(row.MetadataJson));

        return new ToolEnvelope<MemoryRecord>(
            record,
            $"This memory derives from source_id={record.SourceId ?? "<none>"}; retrieve_trace on it for full context.");
    }

    public async Task<ToolEnvelope<MemoryWriteResult>> ProposeMemoryAsync(
        MemoryContext context,
        string @namespace,
        string type,
        string content,
        string sourceType,
        string? sourceId,
        Guid? supersedes = null,
        CancellationToken cancellationToken = default)
    {
        await ValidateOrLogBlockedAsync(context, @namespace, "propose_memory", new { type, content, sourceType, sourceId, supersedes }, cancellationToken);
        var uuid = await InsertMemoryAsync(context, @namespace, type, "shared", "proposed", content, sourceType, sourceId, supersedes, cancellationToken);
        await InsertTraceRawAsync(context.AgentId, @namespace, context.SessionId, "memory_proposed", new { uuid, type, sourceType, sourceId }, [uuid], cancellationToken);
        return new ToolEnvelope<MemoryWriteResult>(
            new MemoryWriteResult(uuid, "proposed"),
            "Proposal recorded; an operator must approve before it becomes shared knowledge. Continue your task.");
    }

    public async Task<ToolEnvelope<MemoryWriteResult>> SaveNoteAsync(
        MemoryContext context,
        string @namespace,
        string type,
        string content,
        string sourceType = "human",
        string? sourceId = null,
        CancellationToken cancellationToken = default)
    {
        await ValidateOrLogBlockedAsync(context, @namespace, "save_note", new { type, content, sourceType, sourceId }, cancellationToken);
        var uuid = await InsertMemoryAsync(context, @namespace, type, "private", "approved", content, sourceType, sourceId, null, cancellationToken);
        await InsertTraceRawAsync(context.AgentId, @namespace, context.SessionId, "memory_written", new { uuid, type, sourceType, sourceId }, [uuid], cancellationToken);
        return new ToolEnvelope<MemoryWriteResult>(
            new MemoryWriteResult(uuid, "approved"),
            "Private note saved. If other agents need this, propose_memory instead.");
    }

    public async Task<IReadOnlyList<MemoryRecord>> PendingAsync(string? @namespace = null)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<MemoryRow>(
            """
            SELECT uuid, namespace, type, visibility, status, tier, content, source_type AS SourceType,
                   source_id AS SourceId, agent_id AS AgentId, session_id AS SessionId, version,
                   supersedes, created_at AS CreatedAt, approved_by AS ApprovedBy,
                   approved_at AS ApprovedAt, retired_at AS RetiredAt,
                   content_hash AS ContentHash, metadata::text AS MetadataJson
            FROM memories
            WHERE status = 'proposed' AND (@Namespace IS NULL OR namespace = @Namespace)
            ORDER BY created_at
            """,
            new { Namespace = @namespace });
        return rows.Select(ToRecord).ToArray();
    }

    public async Task ApproveAsync(Guid uuid, string approvedBy, CancellationToken cancellationToken = default)
    {
        var reviewer = NormalizeReviewerIdentity(approvedBy);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<MemoryRow>(
            """
            UPDATE memories
            SET status = 'approved', approved_by = @ApprovedBy, approved_at = now()
            WHERE uuid = @Uuid AND visibility = 'shared' AND status = 'proposed'
            RETURNING uuid, namespace, type, visibility, status, tier, content, source_type AS SourceType,
                   source_id AS SourceId, agent_id AS AgentId, session_id AS SessionId, version,
                   supersedes, created_at AS CreatedAt, approved_by AS ApprovedBy,
                   approved_at AS ApprovedAt, retired_at AS RetiredAt,
                   content_hash AS ContentHash, metadata::text AS MetadataJson
            """,
            new { Uuid = uuid, ApprovedBy = reviewer },
            transaction);

        if (row.Supersedes.HasValue)
        {
            await connection.ExecuteAsync(
                "UPDATE memories SET status = 'superseded' WHERE uuid = @Uuid",
                new { Uuid = row.Supersedes.Value },
                transaction);
        }

        await InsertTraceRawAsync(
            reviewer,
            row.Namespace,
            ReviewSessionId(uuid),
            "approval",
            new { reviewer, amended = false },
            ReviewRefs(row, includeApprovedMemory: true),
            cancellationToken,
            connection,
            transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RejectAsync(Guid uuid, string rejectedBy, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Rejection requires --reason.", nameof(reason));
        }

        var reviewer = NormalizeReviewerIdentity(rejectedBy);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<MemoryRow>(
            """
            UPDATE memories
            SET status = 'rejected'
            WHERE uuid = @Uuid AND visibility = 'shared' AND status = 'proposed'
            RETURNING uuid, namespace, type, visibility, status, tier, content, source_type AS SourceType,
                      source_id AS SourceId, agent_id AS AgentId, session_id AS SessionId, version,
                      supersedes, created_at AS CreatedAt, approved_by AS ApprovedBy,
                      approved_at AS ApprovedAt, retired_at AS RetiredAt,
                      content_hash AS ContentHash, metadata::text AS MetadataJson
            """,
            new { Uuid = uuid },
            transaction);

        await InsertTraceRawAsync(
            reviewer,
            row.Namespace,
            ReviewSessionId(uuid),
            "rejection",
            new { reviewer, amended = false, reason },
            ReviewRefs(row, includeApprovedMemory: false),
            cancellationToken,
            connection,
            transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RetireAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            "UPDATE memories SET status = 'retired', retired_at = now() WHERE uuid = @Uuid",
            new { Uuid = uuid });

        if (affected == 0)
        {
            throw new InvalidOperationException($"Memory '{uuid}' was not found.");
        }
    }

    public async Task<MemoryRecord> ShowAsync(Guid uuid)
    {
        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<MemoryRow>(
            """
            SELECT uuid, namespace, type, visibility, status, tier, content, source_type AS SourceType,
                   source_id AS SourceId, agent_id AS AgentId, session_id AS SessionId, version,
                   supersedes, created_at AS CreatedAt, approved_by AS ApprovedBy,
                   approved_at AS ApprovedAt, retired_at AS RetiredAt,
                   content_hash AS ContentHash, metadata::text AS MetadataJson
            FROM memories
            WHERE uuid = @Uuid
            """,
            new { Uuid = uuid });

        if (row is null)
        {
            throw new InvalidOperationException($"Memory '{uuid}' was not found.");
        }

        var supersededBy = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT uuid FROM memories WHERE supersedes = @Uuid ORDER BY created_at DESC LIMIT 1",
            new { Uuid = uuid });

        return ToRecord(row) with { SupersededBy = supersededBy };
    }

    public async Task<IReadOnlyList<ConsumedEntry>> ConsumedAsync(string sessionId)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<ConsumedRow>(
            """
            SELECT t.ts AS Ts, m.uuid AS MemoryUuid, m.type AS Type,
                   m.source_type AS SourceType, m.source_id AS SourceId
            FROM traces t
            JOIN memories m ON m.uuid = ANY(t.refs)
            WHERE t.session_id = @SessionId AND t.event_type = 'memory_consumed'
            ORDER BY t.ts, m.uuid
            """,
            new { SessionId = sessionId });
        return rows
            .Select(row => new ConsumedEntry(row.Ts, row.MemoryUuid, row.Type, row.SourceType, row.SourceId))
            .ToArray();
    }

    public async Task<IReadOnlyList<WhyStep>> WhyAsync(Guid uuid)
    {
        await using var connection = await OpenAsync();
        var steps = new List<WhyStep>();
        var seen = new HashSet<Guid>();
        Guid? current = uuid;

        while (current is Guid target && seen.Add(target))
        {
            var row = await connection.QuerySingleOrDefaultAsync<MemoryRow>(
                """
                SELECT uuid, version, status, source_type AS SourceType, source_id AS SourceId, supersedes
                FROM memories
                WHERE uuid = @Uuid
                """,
                new { Uuid = target });

            if (row is null)
            {
                if (steps.Count == 0)
                {
                    throw new InvalidOperationException($"Memory '{uuid}' was not found.");
                }

                break;
            }

            TraceRecord? sourceTrace = null;
            if (string.Equals(row.SourceType, "trace", StringComparison.Ordinal) && Guid.TryParse(row.SourceId, out var traceUuid))
            {
                sourceTrace = await connection.QuerySingleOrDefaultAsync<TraceRecord>(
                    """
                    SELECT trace_uuid AS TraceUuid, session_id AS SessionId, agent_id AS AgentId, namespace,
                           event_type AS EventType, content::text AS Content, refs, ts
                    FROM traces
                    WHERE trace_uuid = @TraceUuid
                    """,
                    new { TraceUuid = traceUuid });
            }

            steps.Add(new WhyStep(row.Uuid, row.Version, row.Status, row.SourceType, row.SourceId, row.Supersedes, sourceTrace));
            current = row.Supersedes;
        }

        return steps;
    }

    public async Task<IReadOnlyList<TraceRecord>> TraceAsync(string sessionId)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<TraceRecord>(
            """
            SELECT trace_uuid AS TraceUuid, session_id AS SessionId, agent_id AS AgentId, namespace,
                   event_type AS EventType, content::text AS Content, refs, ts
            FROM traces
            WHERE session_id = @SessionId
            ORDER BY ts, id
            """,
            new { SessionId = sessionId });
        return rows.ToArray();
    }

    private async Task<Guid> InsertMemoryAsync(
        MemoryContext context,
        string @namespace,
        string type,
        string visibility,
        string status,
        string content,
        string sourceType,
        string? sourceId,
        Guid? supersedes,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO memories (namespace, type, visibility, status, content, content_hash, source_type, source_id, agent_id, session_id, supersedes)
            VALUES (@Namespace, @Type, @Visibility, @Status, @Content, @ContentHash, @SourceType, @SourceId, @AgentId, @SessionId, @Supersedes)
            RETURNING uuid
            """,
            new
            {
                Namespace = @namespace,
                Type = type,
                Visibility = visibility,
                Status = status,
                Content = content,
                ContentHash = ComputeContentHash(content),
                SourceType = sourceType,
                SourceId = sourceId,
                context.AgentId,
                context.SessionId,
                Supersedes = supersedes
            });
    }

    private async Task<Guid> InsertTraceRawAsync(
        string agentId,
        string @namespace,
        string sessionId,
        string eventType,
        object content,
        Guid[]? refs,
        CancellationToken cancellationToken,
        NpgsqlConnection? existingConnection = null,
        NpgsqlTransaction? transaction = null)
    {
        var contentJson = neverStore.Redact(JsonSerializer.Serialize(content, _jsonOptions));
        if (existingConnection is not null)
        {
            return await existingConnection.QuerySingleAsync<Guid>(
                """
                INSERT INTO traces (session_id, agent_id, namespace, event_type, content, refs)
                VALUES (@SessionId, @AgentId, @Namespace, @EventType, CAST(@ContentJson AS jsonb), @Refs)
                RETURNING trace_uuid
                """,
                new { SessionId = sessionId, AgentId = agentId, Namespace = @namespace, EventType = eventType, ContentJson = contentJson, Refs = refs },
                transaction);
        }

        await using var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO traces (session_id, agent_id, namespace, event_type, content, refs)
            VALUES (@SessionId, @AgentId, @Namespace, @EventType, CAST(@ContentJson AS jsonb), @Refs)
            RETURNING trace_uuid
            """,
            new { SessionId = sessionId, AgentId = agentId, Namespace = @namespace, EventType = eventType, ContentJson = contentJson, Refs = refs });
    }

    private async Task ValidateOrLogBlockedAsync(MemoryContext context, string @namespace, string writePath, object payload, CancellationToken cancellationToken)
    {
        try
        {
            neverStore.AssertAllowedObject(payload);
        }
        catch (NeverStoreException ex)
        {
            await InsertTraceRawAsync(context.AgentId, @namespace, context.SessionId, "note", new
            {
                blocked = true,
                rule = ex.RuleName,
                write_path = writePath,
                payload = JsonSerializer.Deserialize<JsonElement>(neverStore.RedactObject(payload))
            }, null, cancellationToken);
            throw;
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Runtime connection string is required.");
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<RetrievalConfig> GetRetrievalConfigAsync(string agentId, string @namespace, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RetrievalConfigRow>(
            """
            SELECT lanes::text AS LanesJson, recency_half_life_h AS RecencyHalfLifeH,
                   max_results AS MaxResults, preview_chars AS PreviewChars,
                   include_proposed AS IncludeProposed
            FROM retrieval_config
            WHERE (agent_id = @AgentId AND namespace = @Namespace)
               OR (agent_id = @AgentId AND namespace = '*')
               OR (agent_id = '*' AND namespace = @Namespace)
               OR (agent_id = '*' AND namespace = '*')
            ORDER BY CASE
                WHEN agent_id = @AgentId AND namespace = @Namespace THEN 1
                WHEN agent_id = @AgentId AND namespace = '*' THEN 2
                WHEN agent_id = '*' AND namespace = @Namespace THEN 3
                ELSE 4
            END
            LIMIT 1
            """,
            new { AgentId = agentId, Namespace = @namespace });

        if (row is null)
        {
            return new RetrievalConfig(["fts", "recency"], 720, 10, 200, false);
        }

        var lanes = JsonSerializer.Deserialize<string[]>(row.LanesJson) ?? ["fts", "recency"];
        return new RetrievalConfig(lanes, row.RecencyHalfLifeH, row.MaxResults, row.PreviewChars, row.IncludeProposed);
    }

    private async Task<IReadOnlyList<LaneRow>> QueryFtsLaneAsync(
        MemoryContext context,
        string query,
        string[] namespaces,
        string[]? types,
        RetrievalConfig config,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<LaneRow>(
            """
            WITH scored AS (
              SELECT uuid, namespace, type, tier, status, content, source_type AS SourceType,
                     source_id AS SourceId, version, created_at AS CreatedAt,
                     ts_rank_cd(search_tsv, websearch_to_tsquery('english', @Query))::float8 AS Score
              FROM memories
              WHERE namespace = ANY(@Namespaces)
                AND (@HasTypes = false OR type = ANY(@Types))
                AND (
                  (visibility = 'shared' AND (status = 'approved' OR (@IncludeProposed AND status = 'proposed')))
                  OR (visibility = 'private' AND status = 'approved' AND agent_id = @AgentId)
                )
                AND search_tsv @@ websearch_to_tsquery('english', @Query)
            )
            SELECT *, row_number() OVER (ORDER BY Score DESC, CreatedAt DESC)::int AS Rank
            FROM scored
            ORDER BY Rank
            LIMIT @Limit
            """,
            new
            {
                Query = query,
                Namespaces = namespaces,
                Types = types ?? [],
                HasTypes = types is { Length: > 0 },
                IncludeProposed = config.IncludeProposed,
                context.AgentId,
                Limit = limit
            });
        return rows.ToArray();
    }

    private async Task<IReadOnlyList<LaneRow>> QueryRecencyLaneAsync(
        MemoryContext context,
        string[] namespaces,
        string[]? types,
        RetrievalConfig config,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<LaneRow>(
            """
            WITH scored AS (
              SELECT uuid, namespace, type, tier, status, content, source_type AS SourceType,
                     source_id AS SourceId, version, created_at AS CreatedAt,
                     exp((-ln(2) * extract(epoch from (now() - created_at)) / 3600.0) / @HalfLife)::float8 AS Score
              FROM memories
              WHERE namespace = ANY(@Namespaces)
                AND (@HasTypes = false OR type = ANY(@Types))
                AND (
                  (visibility = 'shared' AND (status = 'approved' OR (@IncludeProposed AND status = 'proposed')))
                  OR (visibility = 'private' AND status = 'approved' AND agent_id = @AgentId)
                )
            )
            SELECT *, row_number() OVER (ORDER BY Score DESC, CreatedAt DESC)::int AS Rank
            FROM scored
            ORDER BY Rank
            LIMIT @Limit
            """,
            new
            {
                Namespaces = namespaces,
                Types = types ?? [],
                HasTypes = types is { Length: > 0 },
                IncludeProposed = config.IncludeProposed,
                context.AgentId,
                HalfLife = config.RecencyHalfLifeH,
                Limit = limit
            });
        return rows.ToArray();
    }

    private static string Preview(string content, int chars) =>
        content.Length <= chars ? content : content[..chars];

    private static MemoryRecord ToRecord(MemoryRow row) =>
        new(
            row.Uuid,
            row.Namespace,
            row.Type,
            row.Visibility,
            row.Status,
            row.Tier,
            row.Content,
            row.SourceType,
            row.SourceId,
            row.AgentId,
            row.SessionId,
            row.Version,
            row.Supersedes,
            null,
            row.CreatedAt,
            row.ApprovedBy,
            row.ApprovedAt,
            row.RetiredAt,
            row.ContentHash,
            ParseMetadata(row.MetadataJson));

    private static string ComputeContentHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeReviewerIdentity(string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new ArgumentException("Reviewer identity is required.", nameof(reviewer));
        }

        return reviewer.Contains(':', StringComparison.Ordinal) ? reviewer : $"human:{reviewer}";
    }

    private static string ReviewSessionId(Guid proposalUuid) => $"review:{proposalUuid}";

    private static Guid[] ReviewRefs(MemoryRow row, bool includeApprovedMemory)
    {
        var refs = new List<Guid> { row.Uuid };
        if (Guid.TryParse(row.SourceId, out var sourceTraceUuid))
        {
            refs.Add(sourceTraceUuid);
        }

        if (includeApprovedMemory)
        {
            refs.Add(row.Uuid);
        }

        return refs.ToArray();
    }

    private static JsonElement ParseMetadata(string? metadataJson) =>
        JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);

    private sealed record RetrievalConfig(IReadOnlyList<string> Lanes, double RecencyHalfLifeH, int MaxResults, int PreviewChars, bool IncludeProposed);
    private sealed class RetrievalConfigRow
    {
        public string LanesJson { get; set; } = "";
        public double RecencyHalfLifeH { get; set; }
        public int MaxResults { get; set; }
        public int PreviewChars { get; set; }
        public bool IncludeProposed { get; set; }
    }

    private sealed class FusedRow(LaneRow row)
    {
        public LaneRow Row { get; } = row;
        public Dictionary<string, LaneScore> LaneScores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double FusedScore { get; set; }
    }

    private sealed class LaneRow
    {
        public Guid Uuid { get; set; }
        public string Namespace { get; set; } = "";
        public string Type { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Status { get; set; } = "";
        public string Content { get; set; } = "";
        public string SourceType { get; set; } = "";
        public string? SourceId { get; set; }
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int Rank { get; set; }
        public double Score { get; set; }
    }

    private sealed class ConsumedRow
    {
        public DateTimeOffset Ts { get; set; }
        public Guid MemoryUuid { get; set; }
        public string Type { get; set; } = "";
        public string SourceType { get; set; } = "";
        public string? SourceId { get; set; }
    }

    private sealed class MemoryRow
    {
        public Guid Uuid { get; set; }
        public string Namespace { get; set; } = "";
        public string Type { get; set; } = "";
        public string Visibility { get; set; } = "";
        public string Status { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Content { get; set; } = "";
        public string SourceType { get; set; } = "";
        public string? SourceId { get; set; }
        public string AgentId { get; set; } = "";
        public string? SessionId { get; set; }
        public int Version { get; set; }
        public Guid? Supersedes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? ApprovedBy { get; set; }
        public DateTimeOffset? ApprovedAt { get; set; }
        public DateTimeOffset? RetiredAt { get; set; }
        public string ContentHash { get; set; } = "";
        public string MetadataJson { get; set; } = "{}";
    }
}
