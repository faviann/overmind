using System.ComponentModel;
using System.Text.Json;
using MemSrv.Core;
using ModelContextProtocol.Server;

namespace MemSrv.Server;

[McpServerToolType]
public sealed class McpMemoryTools
{
    [McpServerTool(Name = "log_trace")]
    [Description("Append an immutable trace event for the current agent and namespace.")]
    public static Task<ToolEnvelope<TraceResult>> LogTrace(
        MemoryService memory,
        MemoryContext context,
        [Description("Session identifier for this agent run.")] string session_id,
        [Description("Trace event type from the Phase 1 taxonomy.")] string event_type,
        [Description("JSON content for the event.")] JsonElement content,
        [Description("Optional memory UUIDs consumed or produced by this event.")] Guid[]? refs = null,
        CancellationToken cancellationToken = default) =>
        memory.LogTraceAsync(context, session_id, event_type, content, refs, cancellationToken);

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
        memory.SearchMemoryAsync(context, query, namespaces, types, limit, cancellationToken);

    [McpServerTool(Name = "get_by_id")]
    [Description("Fetch full memory content by UUID and log a memory_consumed trace event.")]
    public static Task<ToolEnvelope<MemoryRecord>> GetById(
        MemoryService memory,
        MemoryContext context,
        [Description("Memory UUID returned by search_memory.")] Guid uuid,
        CancellationToken cancellationToken = default) =>
        memory.GetByIdAsync(context, uuid, cancellationToken);

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
        memory.ProposeMemoryAsync(context, @namespace, type, content, source_type, source_id, supersedes, cancellationToken);

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
        memory.SaveNoteAsync(context, @namespace, type, content, source_type, source_id, cancellationToken);
}
