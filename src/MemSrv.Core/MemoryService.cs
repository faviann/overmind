using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed partial class MemoryService(string connectionString, WriteSafetyGate writeSafety)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // The namespace-authorization seam. Every path that reaches a namespace —
    // qualified writes, unqualified defaults, cross-namespace search, and reads
    // by uuid — is gated by AuthorizeNamespace against the context's allowlist,
    // so request identity meets namespace access in one service-layer function.
    // That single policy point is what keeps the north-star RLS retrofit cheap.
    private static string ResolveNamespace(MemoryContext context, string? requested)
    {
        var @namespace = requested ?? context.DefaultNamespace;
        AuthorizeNamespace(context, @namespace);
        return @namespace;
    }

    private static void AuthorizeNamespace(MemoryContext context, string @namespace)
    {
        if (!context.IsNamespaceAllowed(@namespace))
        {
            throw new NamespaceForbiddenException(@namespace, context.AgentId);
        }
    }

    private async Task ValidateOrLogBlockedAsync(MemoryContext context, string @namespace, string writePath, object payload, CancellationToken cancellationToken)
    {
        try
        {
            writeSafety.AssertAllowedObject(payload);
        }
        catch (WriteSafetyRejectedException ex)
        {
            await InsertTraceRawAsync(context.AgentId, @namespace, context.SessionId, "note", new
            {
                blocked = true,
                rule = ex.RuleName,
                write_path = writePath,
                payload = JsonSerializer.Deserialize<JsonElement>(writeSafety.RedactObject(payload))
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
            await using var connection = await OpenAsync(cancellationToken);
            var answer = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return answer == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Runtime connection string is required.");
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
