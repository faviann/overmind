using Dapper;
using Npgsql;

namespace MemSrv.Core;

/// <summary>
/// Resolves a raw capture credential into the authenticated binding context
/// used by ingestion. This is the only place a raw credential is compared, so
/// the HTTP seam authenticates once and hands the result on.
/// </summary>
public sealed class CaptureAuthority(string connectionString)
{
    /// <summary>
    /// Returns the authenticated binding context, or <c>null</c> when the
    /// credential is not a live capture credential. A caller must treat
    /// <c>null</c> as "reject before reading the request body".
    /// </summary>
    public async Task<CaptureBindingContext?> ResolveAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        // Structural pre-check: a non-capture-form credential can never match a
        // binding, so it is rejected without a database round trip.
        if (!CaptureCredential.IsCaptureForm(credential))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<BindingRow>(
            """
            SELECT binding_uuid AS BindingUuid, harness,
                   agent_id AS AgentId, route_namespace AS RouteNamespace,
                   allowed_namespaces AS AllowedNamespaces,
                   content_signature_key AS ContentSignatureKey
            FROM capture_source_bindings
            WHERE credential_hash = @credentialHash AND active
            """,
            new { credentialHash = CaptureCredential.Hash(credential) });
        return row is null
            ? null
            : new CaptureBindingContext(
                row.BindingUuid,
                row.Harness,
                row.AgentId,
                row.RouteNamespace,
                row.AllowedNamespaces,
                row.ContentSignatureKey);
    }

    private sealed class BindingRow
    {
        public Guid BindingUuid { get; set; }
        public string Harness { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string? RouteNamespace { get; set; }
        public string[] AllowedNamespaces { get; set; } = [];
        public byte[] ContentSignatureKey { get; set; } = [];
    }
}
