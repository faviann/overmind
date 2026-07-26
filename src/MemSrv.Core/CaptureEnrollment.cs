using Dapper;
using Npgsql;

namespace MemSrv.Core;

/// <summary>
/// Operator enrollment of one capture source binding. Callers need to know
/// nothing about ingestion or canonical reads: enrolling is complete when a
/// binding uuid comes back.
/// </summary>
public sealed class CaptureEnrollment(string connectionString, NeverStoreGate neverStore)
{
    public async Task<Guid> EnrollAsync(
        string stableName,
        string harness,
        string agentId,
        string credential,
        string? routeNamespace,
        CancellationToken cancellationToken = default)
    {
        CaptureLedger.RequireSafetyConfigured(neverStore);
        CaptureLedger.Require(stableName, nameof(stableName));
        CaptureLedger.Require(agentId, nameof(agentId));
        neverStore.AssertAllowed(stableName);
        neverStore.AssertAllowed(agentId);
        CaptureCredential.RequireCaptureForm(credential);
        if (!string.Equals(harness, "codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This disabled slice enrolls only harness 'codex'.");
        }

        string effective = routeNamespace ?? "capture/unscoped";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        bool exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM namespaces WHERE name = @effective)", new { effective });
        if (!exists)
        {
            throw new InvalidOperationException($"Namespace '{effective}' does not exist.");
        }

        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_source_bindings
              (stable_name, harness, agent_id, credential_hash, route_namespace, allowed_namespaces)
            VALUES (@stableName, @harness, @agentId, @credentialHash, @routeNamespace, @allowedNamespaces)
            RETURNING binding_uuid
            """,
            new
            {
                stableName,
                harness,
                agentId,
                credentialHash = CaptureCredential.Hash(credential),
                routeNamespace,
                allowedNamespaces = new[] { effective }
            });
    }
}
