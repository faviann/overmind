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
        CancellationToken cancellationToken = default)
    {
        CaptureLedger.RequireSafetyConfigured(neverStore);
        CaptureLedger.Require(stableName, nameof(stableName));
        CaptureLedger.Require(harness, nameof(harness));
        CaptureLedger.Require(agentId, nameof(agentId));
        neverStore.AssertAllowed(stableName);
        neverStore.AssertAllowed(harness);
        neverStore.AssertAllowed(agentId);
        CaptureCredential.RequireCaptureForm(credential);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

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
                routeNamespace = (string?)null,
                allowedNamespaces = new[] { "capture/unscoped" }
            });
    }
}
