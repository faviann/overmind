using System.ComponentModel;
using System.Text.Json;
using MemSrv.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemSrv.Server;

[McpServerToolType]
public sealed class McpMemoryTools
{
    // Session identity is server-derived (transport session over HTTP,
    // configuration over stdio) — never a tool argument, the same rule as
    // agent_id and namespace. The effective session is echoed in the response.
    [McpServerTool(Name = "log_trace")]
    [Description("Append an immutable trace event for the current agent and namespace.")]
    public static Task<ToolEnvelope<TraceResult>> LogTrace(
        MemoryService memory,
        MemoryContext context,
        [Description("Trace event type from the Phase 1 taxonomy.")] string event_type,
        [Description("JSON content for the event.")] JsonElement content,
        [Description("Optional memory UUIDs consumed or produced by this event.")] Guid[]? refs = null,
        [Description("Optional namespace for this event. Must be within the agent's allowlist; defaults to the agent's default namespace.")] string? @namespace = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.LogTraceAsync(context, event_type, content, refs, @namespace, cancellationToken));

    [McpServerTool(Name = "search_memory")]
    [Description("Search approved shared memories and this agent's private memories. Returns previews only.")]
    public static Task<ToolEnvelope<IReadOnlyList<SearchMemoryResult>>> SearchMemory(
        MemoryService memory,
        MemoryContext context,
        [Description("Search query.")] string query,
        [Description("Optional namespaces to search. Defaults to the configured namespace.")] string[]? namespaces = null,
        [Description("Optional memory types to include.")] string[]? types = null,
        [Description("Optional result limit, capped by retrieval_config.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.SearchMemoryAsync(context, query, namespaces, types, limit, cancellationToken));

    [McpServerTool(Name = "get_by_id")]
    [Description("Fetch full memory content by UUID and log a memory_consumed trace event.")]
    public static Task<ToolEnvelope<MemoryRecord>> GetById(
        MemoryService memory,
        MemoryContext context,
        [Description("Memory UUID returned by search_memory.")] Guid uuid,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.GetByIdAsync(context, uuid, cancellationToken));

    [McpServerTool(Name = "retrieve_trace")]
    [Description("Fetch a full trace record by UUID for provenance context and log a trace_consumed event. Readable iff the trace's namespace is within the agent's allowlist.")]
    public static Task<ToolEnvelope<RetrievedTraceRecord>> RetrieveTrace(
        MemoryService memory,
        MemoryContext context,
        [Description("Trace UUID, e.g. a memory's source_id or an entry in another trace's refs.")] Guid trace_uuid,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.RetrieveTraceAsync(context, trace_uuid, cancellationToken));

    [McpServerTool(Name = "propose_memory")]
    [Description("Create a shared proposed memory that requires operator approval.")]
    public static Task<ToolEnvelope<MemoryWriteResult>> ProposeMemory(
        MemoryService memory,
        MemoryContext context,
        [Description("Namespace that owns the memory.")] string @namespace,
        [Description("Memory type: decision, fact, preference, task, adr, runbook, or note.")] string type,
        [Description("Memory content.")] string content,
        [Description("Source type: trace, document, human, or worker.")] string source_type,
        [Description("Source identifier such as trace_uuid or file path.")] string? source_id = null,
        [Description("UUID superseded by this proposal, if any.")] Guid? supersedes = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.ProposeMemoryAsync(context, @namespace, type, content, source_type, source_id, supersedes, cancellationToken));

    [McpServerTool(Name = "save_note")]
    [Description("Save a private approved note visible only to the current agent.")]
    public static Task<ToolEnvelope<MemoryWriteResult>> SaveNote(
        MemoryService memory,
        MemoryContext context,
        [Description("Namespace that owns the note.")] string @namespace,
        [Description("Memory type: decision, fact, preference, task, adr, runbook, or note.")] string type,
        [Description("Note content.")] string content,
        [Description("Source type. Defaults to human.")] string source_type = "human",
        [Description("Optional source identifier.")] string? source_id = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.SaveNoteAsync(context, @namespace, type, content, source_type, source_id, cancellationToken));

    [McpServerTool(Name = "list_workstreams")]
    [Description("List workstreams with status and owners — check inflight work to avoid conflicts.")]
    public static Task<ToolEnvelope<IReadOnlyList<WorkstreamRecord>>> ListWorkstreams(
        MemoryService memory,
        MemoryContext context,
        [Description("Optional namespace. Must be within the agent's allowlist; defaults to the agent's default namespace.")] string? @namespace = null,
        [Description("Optional inflight status filter: open or checked_out.")] string? status = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.ListWorkstreamsAsync(context, @namespace, status, cancellationToken));

    [McpServerTool(Name = "checkout_workstream")]
    [Description("Check out a workstream by uuid or by title (creates it if no live workstream bears that title). Fails if it is already checked out — no force-steal.")]
    public static Task<ToolEnvelope<WorkstreamCheckoutResult>> CheckoutWorkstream(
        MemoryService memory,
        MemoryContext context,
        [Description("UUID of an existing workstream to check out.")] Guid? uuid = null,
        [Description("Title to check out; creates the workstream in the default namespace if missing.")] string? title = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.CheckoutWorkstreamAsync(context, uuid, title, cancellationToken));

    [McpServerTool(Name = "checkin_workstream")]
    [Description("Check a workstream back in with a status. Owner-only. Status open makes the notes the handoff summary for the next agent; done and abandoned are terminal.")]
    public static Task<ToolEnvelope<WorkstreamRecord>> CheckinWorkstream(
        MemoryService memory,
        MemoryContext context,
        [Description("UUID of the checked-out workstream.")] Guid uuid,
        [Description("Resulting status: open (handoff), done, or abandoned.")] string status,
        [Description("Freeform state notes; on status open they become the handoff summary.")] string notes,
        [Description("Optional related memory/trace UUIDs to attach.")] Guid[]? refs = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.CheckinWorkstreamAsync(context, uuid, status, notes, refs, cancellationToken));

    [McpServerTool(Name = "create_handoff")]
    [Description("Create a handoff: an open workstream carrying a compact summary and reference UUIDs for the receiving agent. Full context stays retrievable by reference, never inlined.")]
    public static Task<ToolEnvelope<WorkstreamRecord>> CreateHandoff(
        MemoryService memory,
        MemoryContext context,
        [Description("Handoff summary the receiving agent starts from.")] string summary,
        [Description("Related memory/trace UUIDs the summary refers to.")] Guid[] refs,
        [Description("Optional namespace. Must be within the agent's allowlist; defaults to the agent's default namespace.")] string? @namespace = null,
        CancellationToken cancellationToken = default) =>
        Relay(() => memory.CreateHandoffAsync(context, @namespace, summary, refs, cancellationToken));

    // Agent-facing failures must reach the calling agent verbatim: workstream
    // coordination failures (conflicts, ownership, terminal states — e.g. WHO
    // owns a checked-out stream) and namespace rejections (WHICH namespace was
    // outside the allowlist). The SDK masks plain exceptions with a generic
    // message, so they are re-thrown as McpException — still a tool execution
    // error (IsError = true), not a JSON-RPC protocol error. Any tool can hit
    // namespace authorization, so every tool goes through this adapter.
    private static async Task<T> Relay<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (WorkstreamException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (NamespaceForbiddenException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
