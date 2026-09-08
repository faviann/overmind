using System.Text.Json;
using Dapper;
using static MemSrv.Core.NamespaceAuthorization;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
    public async Task<ToolEnvelope<TraceResult>> LogTraceAsync(
        MemoryContext context,
        string eventType,
        object content,
        Guid[]? refs = null,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var targetNamespace = ResolveNamespace(context, @namespace);
        var traceUuid = await _database.InsertTraceRawAsync(context.AgentId, targetNamespace, context.SessionId, eventType, content, refs, cancellationToken);
        return new ToolEnvelope<TraceResult>(
            new TraceResult(traceUuid, context.SessionId),
            "If this event contains a durable decision or fact, call propose_memory referencing this trace_uuid as source_id.");
    }

    public async Task<ToolEnvelope<RetrievedTraceRecord>> RetrieveTraceAsync(
        MemoryContext context,
        Guid traceUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TraceRecord>(
            """
            SELECT trace_uuid AS TraceUuid, session_id AS SessionId, agent_id AS AgentId, namespace,
                   event_type AS EventType, content::text AS Content, refs, ts
            FROM traces
            WHERE trace_uuid = @TraceUuid
            """,
            new { TraceUuid = traceUuid });

        if (row is null)
        {
            throw new InvalidOperationException($"Trace '{traceUuid}' was not found.");
        }

        // Open-door reads (decision 2026-07-13): a trace is readable iff the
        // caller's key allows its namespace — the same single trust boundary
        // as get_by_id, checked before the read is consumed.
        AuthorizeNamespace(context, row.Namespace);

        // Mirror of memory_consumed: the grounding read is provenance too, in
        // the trace's own namespace, with the read uuid in refs.
        await _database.InsertTraceRawAsync(context.AgentId, row.Namespace, context.SessionId, "trace_consumed", new
        {
            uuid = traceUuid
        }, [traceUuid], cancellationToken);

        var record = new RetrievedTraceRecord(
            row.TraceUuid,
            row.SessionId,
            row.AgentId,
            row.Namespace,
            row.EventType,
            JsonSerializer.Deserialize<JsonElement>(row.Content),
            row.Refs,
            row.Ts);

        // A refs-less trace must not instruct the caller to fetch nothing —
        // that is the dead-end hint this tool exists to remove.
        var next = record.Refs is { Length: > 0 }
            ? $"This trace references refs=[{string.Join(", ", record.Refs)}]; call get_by_id (memories) or retrieve_trace (traces) on them for surrounding context."
            : "This trace carries no refs; the provenance walk ends here. Call search_memory for related context, or propose_memory if it holds a durable fact worth keeping.";
        return new ToolEnvelope<RetrievedTraceRecord>(record, next);
    }
}
