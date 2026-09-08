using Dapper;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
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
            writeSafety.AssertAllowed(amendedContent);
        }
        catch (WriteSafetyRejectedException ex)
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
                    payload = writeSafety.RedactObject(new { content = amendedContent })
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

    public async Task RetireAsync(
        Guid uuid,
        string retiredBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Retirement reason is required.", nameof(reason));
        }

        var @operator = NormalizeOperatorIdentity(retiredBy);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MemoryRow>(
            """
            SELECT uuid, namespace, status
            FROM memories
            WHERE uuid = @Uuid
            FOR UPDATE
            """,
            new { Uuid = uuid },
            transaction);

        if (row is null)
        {
            throw new InvalidOperationException($"Memory '{uuid}' was not found.");
        }

        if (row.Status != "approved")
        {
            throw new InvalidOperationException(
                $"Memory '{uuid}' has status '{row.Status}' and cannot be retired; only approved memories are eligible.");
        }

        await connection.ExecuteAsync(
            "UPDATE memories SET status = 'retired', retired_at = now() WHERE uuid = @Uuid AND status = 'approved'",
            new { Uuid = uuid },
            transaction);

        await InsertTraceRawAsync(
            @operator,
            row.Namespace,
            $"retirement:{uuid}",
            "retirement",
            new { @operator, reason },
            [uuid],
            cancellationToken,
            connection,
            transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string NormalizeReviewerIdentity(string reviewer) =>
        NormalizeActorIdentity(reviewer, "Reviewer identity is required.", nameof(reviewer));

    private static string NormalizeOperatorIdentity(string @operator) =>
        NormalizeActorIdentity(@operator, "Operator identity is required.", nameof(@operator));

    private static string NormalizeActorIdentity(string actor, string requiredMessage, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException(requiredMessage, parameterName);
        }

        return actor.Contains(':', StringComparison.Ordinal) ? actor : $"human:{actor}";
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
}
