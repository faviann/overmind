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
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var targetNamespace = ResolveNamespace(context, @namespace);
        var traceUuid = await InsertTraceRawAsync(context.AgentId, targetNamespace, sessionId, eventType, content, refs, cancellationToken);
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
        var searchedNamespaces = namespaces is { Length: > 0 } ? namespaces : [context.DefaultNamespace];
        foreach (var searched in searchedNamespaces)
        {
            AuthorizeNamespace(context, searched);
        }

        await ValidateOrLogBlockedAsync(context, context.DefaultNamespace, "search_memory", new { query, namespaces, types, limit }, cancellationToken);
        await InsertTraceRawAsync(context.AgentId, context.DefaultNamespace, context.SessionId, "tool_call", new
        {
            tool = "search_memory",
            @params = new { query, namespaces, types, limit }
        }, null, cancellationToken);

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

        // Reads flow through the same namespace seam as writes: a memory in a
        // namespace outside the allowlist is rejected before it is consumed.
        AuthorizeNamespace(context, row.Namespace);

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
        AuthorizeNamespace(context, @namespace);
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
        AuthorizeNamespace(context, @namespace);
        await ValidateOrLogBlockedAsync(context, @namespace, "save_note", new { type, content, sourceType, sourceId }, cancellationToken);
        var uuid = await InsertMemoryAsync(context, @namespace, type, "private", "approved", content, sourceType, sourceId, null, cancellationToken);
        await InsertTraceRawAsync(context.AgentId, @namespace, context.SessionId, "memory_written", new { uuid, type, sourceType, sourceId }, [uuid], cancellationToken);
        return new ToolEnvelope<MemoryWriteResult>(
            new MemoryWriteResult(uuid, "approved"),
            "Private note saved. If other agents need this, propose_memory instead.");
    }

    private const string WorkstreamColumns =
        "uuid, namespace, title, status, owner_agent AS OwnerAgent, session_id AS SessionId, notes, refs, updated_at AS UpdatedAt";

    // A read: mutates no coordination state and logs NO trace event — the
    // taxonomy has no event type for listing.
    public async Task<ToolEnvelope<IReadOnlyList<WorkstreamRecord>>> ListWorkstreamsAsync(
        MemoryContext context,
        string? @namespace = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var targetNamespace = ResolveNamespace(context, @namespace);
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<WorkstreamRow>(
            $"""
            SELECT {WorkstreamColumns}
            FROM workstreams
            WHERE namespace = @Namespace AND (@Status IS NULL OR status = @Status)
            ORDER BY updated_at DESC
            """,
            new { Namespace = targetNamespace, Status = status });

        return new ToolEnvelope<IReadOnlyList<WorkstreamRecord>>(
            rows.Select(ToWorkstream).ToArray(),
            "Call checkout_workstream with a uuid (or a new title) to claim work before starting; open entries carry handoff notes to start from.");
    }

    public async Task<ToolEnvelope<WorkstreamCheckoutResult>> CheckoutWorkstreamAsync(
        MemoryContext context,
        Guid? uuid,
        string? title,
        CancellationToken cancellationToken = default)
    {
        if (uuid.HasValue == !string.IsNullOrWhiteSpace(title))
        {
            throw new WorkstreamException("checkout_workstream requires exactly one of uuid or title.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        WorkstreamRow row;
        var created = false;
        if (uuid is Guid target)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<WorkstreamRow>(
                $"SELECT {WorkstreamColumns} FROM workstreams WHERE uuid = @Uuid FOR UPDATE",
                new { Uuid = target },
                transaction)
                ?? throw new WorkstreamException($"Workstream '{target}' was not found.");
            AuthorizeNamespace(context, existing.Namespace);
            row = await ClaimAsync(connection, transaction, context, existing);
        }
        else
        {
            // Title checkout is scoped to the key's default namespace, like every
            // other unqualified call.
            var @namespace = context.DefaultNamespace;
            AuthorizeNamespace(context, @namespace);
            var candidates = (await connection.QueryAsync<WorkstreamRow>(
                $"""
                SELECT {WorkstreamColumns}
                FROM workstreams
                WHERE namespace = @Namespace AND title = @Title AND status IN ('open','checked_out')
                ORDER BY updated_at DESC
                FOR UPDATE
                """,
                new { Namespace = @namespace, Title = title },
                transaction)).ToArray();

            var open = candidates.FirstOrDefault(candidate => candidate.Status == "open");
            if (open is not null)
            {
                row = await ClaimAsync(connection, transaction, context, open);
            }
            else if (candidates.FirstOrDefault(candidate => candidate.Status == "checked_out") is { } conflict)
            {
                throw CheckedOutConflict(conflict);
            }
            else
            {
                // No live workstream bears this title (terminal-status rows do not
                // count — titles are not unique): create and check out atomically.
                row = await connection.QuerySingleAsync<WorkstreamRow>(
                    $"""
                    INSERT INTO workstreams (namespace, title, status, owner_agent, session_id)
                    VALUES (@Namespace, @Title, 'checked_out', @AgentId, @SessionId)
                    RETURNING {WorkstreamColumns}
                    """,
                    new { Namespace = @namespace, Title = neverStore.Redact(title!), context.AgentId, context.SessionId },
                    transaction);
                created = true;
            }
        }

        await InsertTraceRawAsync(context.AgentId, row.Namespace, context.SessionId, "workstream_checkout", new
        {
            uuid = row.Uuid,
            title = row.Title,
            created
        }, [row.Uuid], cancellationToken, connection, transaction);
        await transaction.CommitAsync(cancellationToken);

        return new ToolEnvelope<WorkstreamCheckoutResult>(
            new WorkstreamCheckoutResult(ToWorkstream(row), created),
            "Workstream is checked out to you. When you stop, call checkin_workstream with this uuid and a status: open to hand off (notes become the handoff summary), done, or abandoned.");
    }

    public async Task<ToolEnvelope<WorkstreamRecord>> CheckinWorkstreamAsync(
        MemoryContext context,
        Guid uuid,
        string status,
        string notes,
        Guid[]? refs = null,
        CancellationToken cancellationToken = default)
    {
        if (status is not ("open" or "done" or "abandoned"))
        {
            throw new WorkstreamException("checkin_workstream status must be open, done, or abandoned.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<WorkstreamRow>(
            $"SELECT {WorkstreamColumns} FROM workstreams WHERE uuid = @Uuid FOR UPDATE",
            new { Uuid = uuid },
            transaction)
            ?? throw new WorkstreamException($"Workstream '{uuid}' was not found.");
        AuthorizeNamespace(context, existing.Namespace);
        if (existing.Status != "checked_out")
        {
            throw new WorkstreamException($"Workstream '{uuid}' is not checked out (status '{existing.Status}'); nothing to check in.");
        }

        // Owner means the same agent identity, not the same transport session: a
        // restarted session of the owning agent can still check in its work.
        if (!string.Equals(existing.OwnerAgent, context.AgentId, StringComparison.Ordinal))
        {
            throw new WorkstreamException(
                $"Workstream '{uuid}' is checked out by agent '{existing.OwnerAgent}', not '{context.AgentId}'; only the owner checks in.");
        }

        // Notes follow the TRACE never-store rule: redact-in-place before insert,
        // the checkin itself still succeeds.
        var row = await connection.QuerySingleAsync<WorkstreamRow>(
            $"""
            UPDATE workstreams
            SET status = @Status,
                notes = @Notes,
                refs = COALESCE(@Refs, refs),
                owner_agent = CASE WHEN @Status = 'open' THEN NULL ELSE owner_agent END,
                session_id = CASE WHEN @Status = 'open' THEN NULL ELSE session_id END,
                updated_at = now()
            WHERE uuid = @Uuid
            RETURNING {WorkstreamColumns}
            """,
            new { Uuid = uuid, Status = status, Notes = neverStore.Redact(notes), Refs = refs },
            transaction);

        await InsertTraceRawAsync(context.AgentId, row.Namespace, context.SessionId, "workstream_checkin", new
        {
            uuid = row.Uuid,
            title = row.Title,
            status
        }, [row.Uuid], cancellationToken, connection, transaction);
        await transaction.CommitAsync(cancellationToken);

        return new ToolEnvelope<WorkstreamRecord>(
            ToWorkstream(row),
            status == "open"
                ? "Handoff recorded: your notes are the summary the next agent starts from. Continue with list_workstreams or checkout_workstream for your next unit of work."
                : "Checkin recorded. Call list_workstreams to pick up other inflight work, or checkout_workstream to start something new.");
    }

    public async Task<ToolEnvelope<WorkstreamRecord>> CreateHandoffAsync(
        MemoryContext context,
        string? @namespace,
        string summary,
        Guid[] refs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new WorkstreamException("create_handoff requires a non-empty summary.");
        }

        var targetNamespace = ResolveNamespace(context, @namespace);
        // The summary follows the TRACE never-store rule: redact-in-place before
        // insert (row and trace event alike); the handoff itself still succeeds.
        var redactedSummary = neverStore.Redact(summary);
        var title = HandoffTitle(redactedSummary);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<WorkstreamRow>(
            $"""
            INSERT INTO workstreams (namespace, title, status, notes, refs)
            VALUES (@Namespace, @Title, 'open', @Notes, @Refs)
            RETURNING {WorkstreamColumns}
            """,
            new { Namespace = targetNamespace, Title = title, Notes = redactedSummary, Refs = refs },
            transaction);

        await InsertTraceRawAsync(context.AgentId, targetNamespace, context.SessionId, "handoff", new
        {
            uuid = row.Uuid,
            title = row.Title
        }, [row.Uuid, .. refs], cancellationToken, connection, transaction);
        await transaction.CommitAsync(cancellationToken);

        return new ToolEnvelope<WorkstreamRecord>(
            ToWorkstream(row),
            "Handoff created as an open workstream: the receiving agent should checkout_workstream this uuid and start from the summary, retrieving full context via the refs.");
    }

    /// <summary>
    /// Operator escape hatch (memctl release): flips a stale checked-out
    /// workstream back to open, clearing owner and session but keeping the
    /// notes. Like memctl retire — and unlike the agent-facing checkin — it
    /// writes no trace event: the taxonomy's workstream events describe agent
    /// coordination, and only approve/reject carry the review-event convention.
    /// </summary>
    public async Task<WorkstreamRecord> ReleaseWorkstreamAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<WorkstreamRow>(
            $"""
            UPDATE workstreams
            SET status = 'open', owner_agent = NULL, session_id = NULL, updated_at = now()
            WHERE uuid = @Uuid AND status = 'checked_out'
            RETURNING {WorkstreamColumns}
            """,
            new { Uuid = uuid });

        if (row is null)
        {
            var current = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT status FROM workstreams WHERE uuid = @Uuid",
                new { Uuid = uuid });
            throw new WorkstreamException(current is null
                ? $"Workstream '{uuid}' was not found."
                : $"Workstream '{uuid}' is not checked out (status '{current}'); nothing to release.");
        }

        return ToWorkstream(row);
    }

    // Handoff workstreams have no caller-supplied title; derive a compact one
    // from the (already redacted) summary's first line.
    private static string HandoffTitle(string summary)
    {
        var firstLine = summary.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            firstLine = firstLine[..newline];
        }

        var title = firstLine.Trim().ToString();
        return title.Length <= 80 ? title : title[..80];
    }

    private async Task<WorkstreamRow> ClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemoryContext context,
        WorkstreamRow existing)
    {
        if (existing.Status == "checked_out")
        {
            throw CheckedOutConflict(existing);
        }

        if (existing.Status is "done" or "abandoned")
        {
            throw new WorkstreamException(
                $"Workstream '{existing.Uuid}' has terminal status '{existing.Status}' and cannot be checked out.");
        }

        return await connection.QuerySingleAsync<WorkstreamRow>(
            $"""
            UPDATE workstreams
            SET status = 'checked_out', owner_agent = @AgentId, session_id = @SessionId, updated_at = now()
            WHERE uuid = @Uuid
            RETURNING {WorkstreamColumns}
            """,
            new { existing.Uuid, context.AgentId, context.SessionId },
            transaction);
    }

    private static WorkstreamException CheckedOutConflict(WorkstreamRow row) =>
        new($"Workstream '{row.Uuid}' ('{row.Title}') is already checked out by agent '{row.OwnerAgent}' in session '{row.SessionId}'. There is no force-steal; wait for checkin or ask an operator to run memctl release.");

    private static WorkstreamRecord ToWorkstream(WorkstreamRow row) =>
        new(row.Uuid, row.Namespace, row.Title, row.Status, row.OwnerAgent, row.SessionId, row.Notes, row.Refs, row.UpdatedAt);

    private sealed class WorkstreamRow
    {
        public Guid Uuid { get; set; }
        public string Namespace { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string? OwnerAgent { get; set; }
        public string? SessionId { get; set; }
        public string? Notes { get; set; }
        public Guid[]? Refs { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
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

    public async Task<Guid> ApproveAmendmentAsync(
        Guid proposalUuid,
        string approvedBy,
        string amendedContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(amendedContent))
        {
            throw new ArgumentException("Amended content must not be empty.", nameof(amendedContent));
        }

        var reviewer = NormalizeReviewerIdentity(approvedBy);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var proposal = await connection.QuerySingleAsync<MemoryRow>(
            """
            SELECT uuid, namespace, type, visibility, status, tier, content, source_type AS SourceType,
                   source_id AS SourceId, agent_id AS AgentId, session_id AS SessionId, version,
                   supersedes, created_at AS CreatedAt, approved_by AS ApprovedBy,
                   approved_at AS ApprovedAt, retired_at AS RetiredAt,
                   content_hash AS ContentHash, metadata::text AS MetadataJson
            FROM memories
            WHERE uuid = @Uuid AND visibility = 'shared' AND status = 'proposed'
            FOR UPDATE
            """,
            new { Uuid = proposalUuid },
            transaction);

        try
        {
            neverStore.AssertAllowed(amendedContent);
        }
        catch (NeverStoreException ex)
        {
            await InsertTraceRawAsync(
                reviewer,
                proposal.Namespace,
                ReviewSessionId(proposalUuid),
                "note",
                new
                {
                    blockedWrite = "approve_amendment",
                    rule = ex.RuleName,
                    payload = neverStore.RedactObject(new { content = amendedContent })
                },
                [proposalUuid],
                cancellationToken,
                connection,
                transaction);
            await transaction.CommitAsync(cancellationToken);
            throw;
        }

        await connection.ExecuteAsync(
            "UPDATE memories SET status = 'superseded' WHERE uuid = @Uuid OR uuid = @PriorUuid",
            new { Uuid = proposalUuid, PriorUuid = proposal.Supersedes },
            transaction);

        var approvedUuid = await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO memories (
                namespace, type, visibility, status, tier, content, content_hash, metadata,
                source_type, source_id, agent_id, session_id, version, supersedes, approved_by, approved_at)
            VALUES (
                @Namespace, @Type, 'shared', 'approved', @Tier, @Content, @ContentHash, CAST(@MetadataJson AS jsonb),
                @SourceType, @SourceId, @AgentId, @SessionId, @Version, @Supersedes, @ApprovedBy, now())
            RETURNING uuid
            """,
            new
            {
                proposal.Namespace,
                proposal.Type,
                proposal.Tier,
                Content = amendedContent,
                ContentHash = ComputeContentHash(amendedContent),
                proposal.MetadataJson,
                proposal.SourceType,
                proposal.SourceId,
                proposal.AgentId,
                proposal.SessionId,
                Version = proposal.Version + 1,
                Supersedes = proposalUuid,
                ApprovedBy = reviewer
            },
            transaction);

        await InsertTraceRawAsync(
            reviewer,
            proposal.Namespace,
            ReviewSessionId(proposalUuid),
            "approval",
            new { reviewer, amended = true },
            ReviewRefs(proposal, approvedUuid),
            cancellationToken,
            connection,
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return approvedUuid;
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
            INSERT INTO memories (namespace, type, visibility, status, content, content_hash, source_type, source_id, agent_id, session_id, version, supersedes)
            VALUES (
                @Namespace, @Type, @Visibility, @Status, @Content, @ContentHash, @SourceType, @SourceId, @AgentId, @SessionId,
                COALESCE((SELECT version + 1 FROM memories WHERE uuid = @Supersedes), 1),
                @Supersedes)
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

    // The namespace-authorization seam. Every path that reaches a namespace —
    // qualified writes, unqualified defaults, cross-namespace search, and reads
    // by uuid — is gated by AuthorizeNamespace against the context's allowlist,
    // so request identity meets namespace access in one service-layer function.
    // That single policy point is what keeps the north-star RLS retrofit cheap.
    private static string ResolveNamespace(MemoryContext context, string? requested)
    {
        var @namespace = requested ?? context.DefaultNamespace;
        AuthorizeNamespace(context, @namespace);
        return @namespace;
    }

    private static void AuthorizeNamespace(MemoryContext context, string @namespace)
    {
        if (!context.IsNamespaceAllowed(@namespace))
        {
            throw new NamespaceForbiddenException(@namespace, context.AgentId);
        }
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

    /// <summary>
    /// Cheap liveness probe for <c>/healthz</c>: opens a connection and runs
    /// <c>SELECT 1</c>. Returns false (never throws) if the database is
    /// unreachable or does not answer before the caller's token trips.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var answer = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return answer == 1;
        }
        catch (Exception)
        {
            return false;
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

    private static Guid[] ReviewRefs(MemoryRow proposal, Guid approvedUuid)
    {
        var refs = ReviewRefs(proposal, includeApprovedMemory: false).ToList();
        refs.Add(approvedUuid);
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
