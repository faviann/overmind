using System.Text.Json;
using Dapper;

namespace MemSrv.Core;

public sealed partial class MemoryService
{
    private readonly WriteSafetyGate _writeSafety;
    private readonly MemoryDatabase _database;
    private readonly Workstreams _workstreams;

    public MemoryService(string connectionString, WriteSafetyGate writeSafety)
    {
        _writeSafety = writeSafety;
        _database = new MemoryDatabase(connectionString, writeSafety);
        _workstreams = new Workstreams(_database, writeSafety);
    }

    private async Task ValidateOrLogBlockedAsync(MemoryContext context, string @namespace, string writePath, object payload, CancellationToken cancellationToken)
    {
        try
        {
            _writeSafety.AssertAllowedObject(payload);
        }
        catch (WriteSafetyRejectedException ex)
        {
            await _database.InsertTraceRawAsync(context.AgentId, @namespace, context.SessionId, "note", new
            {
                blocked = true,
                rule = ex.RuleName,
                write_path = writePath,
                payload = JsonSerializer.Deserialize<JsonElement>(_writeSafety.RedactObject(payload))
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
            await using var connection = await _database.OpenAsync(cancellationToken);
            var answer = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return answer == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
