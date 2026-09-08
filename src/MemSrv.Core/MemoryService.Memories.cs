using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
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

        var next = record.SourceId is not null
            ? $"This memory derives from source_id={record.SourceId}; retrieve_trace on it for full context."
            : "This memory has no recorded source; call search_memory for related context, or log_trace to capture supporting evidence.";
        return new ToolEnvelope<MemoryRecord>(record, next);
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

    private static JsonElement ParseMetadata(string? metadataJson) =>
        JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);

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
