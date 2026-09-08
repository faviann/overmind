using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

// Shared connection creation and trace persistence. Each operation retains
// ownership of its connections and transactions; this module never commits them.
internal sealed class MemoryDatabase(string connectionString, WriteSafetyGate writeSafety)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Runtime connection string is required.");
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    internal async Task<Guid> InsertTraceRawAsync(
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
        var contentJson = writeSafety.RedactJson(JsonSerializer.Serialize(content, _jsonOptions));
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
}
