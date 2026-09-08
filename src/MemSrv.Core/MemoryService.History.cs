using Dapper;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
    public async Task<IReadOnlyList<ConsumedEntry>> ConsumedAsync(string sessionId)
    {
        await using var connection = await _database.OpenAsync();
        var rows = await connection.QueryAsync<ConsumedRow>(
            """
            SELECT t.ts AS Ts, 'memory' AS Kind, m.uuid AS Uuid, m.type AS Type,
                   m.source_type AS SourceType, m.source_id AS SourceId
            FROM traces t
            JOIN memories m ON m.uuid = ANY(t.refs)
            WHERE t.session_id = @SessionId AND t.event_type = 'memory_consumed'
            UNION ALL
            SELECT t.ts AS Ts, 'trace' AS Kind, r.trace_uuid AS Uuid, r.event_type AS Type,
                   NULL AS SourceType, NULL AS SourceId
            FROM traces t
            JOIN traces r ON r.trace_uuid = ANY(t.refs)
            WHERE t.session_id = @SessionId AND t.event_type = 'trace_consumed'
            ORDER BY Ts, Uuid
            """,
            new { SessionId = sessionId });
        return rows
            .Select(row => new ConsumedEntry(row.Ts, row.Kind, row.Uuid, row.Type, row.SourceType, row.SourceId))
            .ToArray();
    }

    public async Task<IReadOnlyList<WhyStep>> WhyAsync(Guid uuid)
    {
        await using var connection = await _database.OpenAsync();
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
        await using var connection = await _database.OpenAsync();
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

    private sealed class ConsumedRow
    {
        public DateTimeOffset Ts { get; set; }
        public string Kind { get; set; } = "";
        public Guid Uuid { get; set; }
        public string Type { get; set; } = "";
        public string? SourceType { get; set; }
        public string? SourceId { get; set; }
    }
}
