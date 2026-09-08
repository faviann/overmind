using System.Text.Json;
using Dapper;
using static MemSrv.Core.NamespaceAuthorization;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
    private const double RrfK = 60;

    // Both ranking lanes use the same eligibility rules. Namespace access is
    // still authorized before either lane runs, through AuthorizeNamespace.
    private const string SearchEligibilitySql =
        """
        WHERE namespace = ANY(@Namespaces)
            AND (@HasTypes = false OR type = ANY(@Types))
            AND (
              (visibility = 'shared' AND (status = 'approved' OR (@IncludeProposed AND status = 'proposed')))
              OR (visibility = 'private' AND status = 'approved' AND agent_id = @AgentId)
            )
        """;

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
        await _database.InsertTraceRawAsync(context.AgentId, context.DefaultNamespace, context.SessionId, "tool_call", new
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

    private async Task<RetrievalConfig> GetRetrievalConfigAsync(string agentId, string @namespace, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken);
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
        await using var connection = await _database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<LaneRow>(
            $"""
            WITH scored AS (
              SELECT uuid, namespace, type, tier, status, content, source_type AS SourceType,
                     source_id AS SourceId, version, created_at AS CreatedAt,
                     ts_rank_cd(search_tsv, websearch_to_tsquery('english', @Query))::float8 AS Score
              FROM memories
              {SearchEligibilitySql}
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
        await using var connection = await _database.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<LaneRow>(
            $"""
            WITH scored AS (
              SELECT uuid, namespace, type, tier, status, content, source_type AS SourceType,
                     source_id AS SourceId, version, created_at AS CreatedAt,
                     exp((-ln(2) * extract(epoch from (now() - created_at)) / 3600.0) / @HalfLife)::float8 AS Score
              FROM memories
              {SearchEligibilitySql}
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
}
