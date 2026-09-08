using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
    private const string WorkstreamColumns =
        "uuid, namespace, title, status, owner_agent AS OwnerAgent, session_id AS SessionId, notes, refs, updated_at AS UpdatedAt";

    public async Task<ToolEnvelope<IReadOnlyList<WorkstreamRecord>>> ListWorkstreamsAsync(
        MemoryContext context,
        string? @namespace = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var targetNamespace = ResolveNamespace(context, @namespace);
        if (status is not null and not ("open" or "checked_out"))
        {
            throw new WorkstreamException("list_workstreams status must be open or checked_out.");
        }

        await InsertTraceRawAsync(context.AgentId, targetNamespace, context.SessionId, "tool_call", new
        {
            tool = "list_workstreams",
            @params = new { @namespace = targetNamespace, status }
        }, null, cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<WorkstreamRow>(
            $"""
            SELECT {WorkstreamColumns}
            FROM workstreams
            WHERE namespace = @Namespace
              AND status IN ('open', 'checked_out')
              AND (@Status IS NULL OR status = @Status)
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
                    new { Namespace = @namespace, Title = writeSafety.Redact(title!), context.AgentId, context.SessionId },
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
            new { Uuid = uuid, Status = status, Notes = writeSafety.Redact(notes), Refs = refs },
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
        var redactedSummary = writeSafety.Redact(summary);
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
    /// notes. Unlike the agent-facing checkin, it writes no trace event: the
    /// taxonomy's workstream events describe agent coordination, and the
    /// binding spec defines no operator-release event convention.
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
}
