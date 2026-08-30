using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Core;

public sealed record CapturePairingRequestCreated(
    Guid RequestId, string VerificationUri, string UserCode,
    string PollingToken, DateTimeOffset ExpiresAt);
public sealed class CapturePairingRequestView
{
    public Guid RequestId { get; set; }
    public string UserCode { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string CodexInstallationId { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed record CapturePairingApproval(
    string Label,
    IReadOnlyList<string> AllowedRepositoryPatterns,
    IReadOnlyList<CaptureSpecialNamespace> SpecialNamespaces,
    IReadOnlyList<CaptureDirectoryRoute>? DirectoryRoutes = null);
public sealed record CapturePairingApproved(Guid BindingId, string Label);
public sealed record CapturePairingPoll(string Status, string? Credential);

/// <summary>
/// Server-owned browser pairing. The polling capability is distinct from the
/// displayed code; only its hash is durable after credential delivery.
/// </summary>
public sealed class CapturePairing(
    string connectionString,
    WriteSafetyGate neverStore,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<CapturePairingRequestCreated> CreateAsync(
        string machineName, string codexInstallationId, string verificationBaseUri,
        CancellationToken cancellationToken = default)
    {
        CaptureLedger.RequireSafetyConfigured(neverStore);
        CaptureLedger.Require(machineName, nameof(machineName));
        CaptureLedger.Require(codexInstallationId, nameof(codexInstallationId));
        neverStore.AssertAllowed(machineName);
        neverStore.AssertAllowed(codexInstallationId);
        string token = RandomSecret(32);
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        DateTimeOffset expiresAt = _time.GetUtcNow().Add(Lifetime);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid id = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO capture_pairing_requests
              (user_code, polling_token_hash, machine_name, codex_installation_id, expires_at)
            VALUES (@code, @tokenHash, @machineName, @codexInstallationId, @expiresAt)
            RETURNING request_uuid
            """,
            new { code, tokenHash = Hash(token), machineName, codexInstallationId, expiresAt },
            transaction);
        await connection.ExecuteAsync(
            "INSERT INTO capture_pairing_audit (request_uuid, action) VALUES (@id, 'created')",
            new { id }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new(id, $"{verificationBaseUri.TrimEnd('/')}/{code}", code, token, expiresAt);
    }

    public async Task<CapturePairingRequestView?> InspectAsync(
        Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QuerySingleOrDefaultAsync<CapturePairingRequestView>(
            """
            SELECT request_uuid AS RequestId, user_code AS UserCode,
                   machine_name AS MachineName, codex_installation_id AS CodexInstallationId,
                   status, expires_at AS ExpiresAt
            FROM capture_pairing_requests WHERE request_uuid = @requestId
            """, new { requestId });
    }

    public async Task<CapturePairingRequestView?> InspectByUserCodeAsync(
        string userCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userCode)) return null;
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QuerySingleOrDefaultAsync<CapturePairingRequestView>(
            """
            SELECT request_uuid AS RequestId, user_code AS UserCode,
                   machine_name AS MachineName, codex_installation_id AS CodexInstallationId,
                   status, expires_at AS ExpiresAt
            FROM capture_pairing_requests WHERE user_code = upper(@userCode)
            """, new { userCode = userCode.Trim() });
    }

    public async Task<CapturePairingApproved> ApproveAsync(
        Guid requestId, string operatorSubject, CapturePairingApproval approval,
        CancellationToken cancellationToken = default)
    {
        CaptureLedger.RequireSafetyConfigured(neverStore);
        CaptureLedger.Require(operatorSubject, nameof(operatorSubject));
        CaptureLedger.Require(approval.Label, nameof(approval.Label));
        neverStore.AssertAllowed(approval.Label);
        string[] patterns = approval.AllowedRepositoryPatterns
            .Select(value => value.Trim().ToLowerInvariant()).ToArray();
        if (patterns.Any(value => string.IsNullOrWhiteSpace(value) || !value.Contains('/')))
            throw new ArgumentException("Allowed repository patterns must be nonblank owner/name patterns.");
        if (approval.SpecialNamespaces.Select(value => value.Alias)
            .Distinct(StringComparer.Ordinal).Count() != approval.SpecialNamespaces.Count)
            throw new ArgumentException("Special namespace aliases must be unique.");
        CaptureDirectoryRoute[] directoryRoutes = (approval.DirectoryRoutes ?? [])
            .Select(item => new CaptureDirectoryRoute(
                CaptureRouteResolver.NormalizeDirectoryForPolicy(item.Directory),
                item.Target))
            .ToArray();
        if (directoryRoutes.Select(value => value.Directory)
            .Distinct(StringComparer.Ordinal).Count() != directoryRoutes.Length)
            throw new ArgumentException("Directory route paths must be unique.");
        var aliases = approval.SpecialNamespaces.Select(value => value.Alias)
            .ToHashSet(StringComparer.Ordinal);
        foreach (CaptureDirectoryRoute route in directoryRoutes)
        {
            if (!route.Target.StartsWith("special:", StringComparison.Ordinal)
                || !aliases.Contains(route.Target["special:".Length..]))
                throw new ArgumentException(
                    $"Directory route target '{route.Target}' must name a configured special alias.");
        }
        foreach (string value in patterns.Concat(approval.SpecialNamespaces
                     .SelectMany(item => new[] { item.Alias, item.Namespace })))
            neverStore.AssertAllowed(value);
        foreach (string value in directoryRoutes.SelectMany(item => new[] { item.Directory, item.Target }))
            neverStore.AssertAllowed(value);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset approvalTime = _time.GetUtcNow();
        PairingRow? request = await connection.QuerySingleOrDefaultAsync<PairingRow>(
            """
            SELECT status, expires_at AS ExpiresAt,
                   codex_installation_id AS CodexInstallationId
            FROM capture_pairing_requests WHERE request_uuid = @requestId FOR UPDATE
            """, new { requestId }, transaction);
        if (request is null) throw new KeyNotFoundException("Pairing request was not found.");
        if (request.Status != "pending" || request.ExpiresAt <= approvalTime)
            throw new CapturePairingConflictException("Pairing request is no longer approvable.");
        foreach (CaptureSpecialNamespace mapping in approval.SpecialNamespaces)
        {
            if (CaptureRoutePolicyStore.IsReservedNamespace(mapping.Namespace)
                || mapping.Namespace.StartsWith("repo/", StringComparison.Ordinal)
                || !await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM namespaces WHERE name=@Namespace)",
                    new { mapping.Namespace }, transaction))
                throw new ArgumentException($"Special namespace '{mapping.Namespace}' is not an allowed existing target.");
        }

        Guid bindingId = Guid.NewGuid();
        string agentId = $"capture:codex:{bindingId:N}";
        string credential = $"mcap_{RandomSecret(32)}";
        byte[] signatureKey = RandomNumberGenerator.GetBytes(32);
        await connection.ExecuteAsync(
            """
            INSERT INTO capture_source_bindings
              (binding_uuid, stable_name, harness, agent_id, credential_hash,
               route_namespace, allowed_namespaces, content_signature_key,
               codex_installation_id)
            VALUES (@bindingId, @label, 'codex', @agentId, @credentialHash,
                    NULL, ARRAY['capture/unscoped']::text[], @signatureKey,
                    @codexInstallationId)
            """, new
            {
                bindingId, label = approval.Label, agentId,
                credentialHash = CaptureCredential.Hash(credential), signatureKey,
                codexInstallationId = request.CodexInstallationId
            }, transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO capture_route_policies
              (binding_uuid, allowed_repository_patterns, remote_overrides,
               directory_routes, special_namespaces)
            VALUES (@bindingId, @patterns, '[]'::jsonb, CAST(@directoryRoutes AS jsonb),
                    CAST(@specialNamespaces AS jsonb))
            """, new
            {
                bindingId, patterns,
                directoryRoutes = JsonSerializer.Serialize(
                    directoryRoutes, CaptureLedger.JsonOptions),
                specialNamespaces = JsonSerializer.Serialize(
                    approval.SpecialNamespaces, CaptureLedger.JsonOptions)
            }, transaction);
        int changed = await connection.ExecuteAsync(
            """
            UPDATE capture_pairing_requests
            SET status='approved', binding_uuid=@bindingId,
                delivery_credential=@credential, approved_by=@operatorSubject,
                approved_at=@approvedAt
            WHERE request_uuid=@requestId AND status='pending'
              AND expires_at>@approvalTime
            """, new
            {
                requestId, bindingId, credential, operatorSubject,
                approvedAt = approvalTime,
                approvalTime
            }, transaction);
        if (changed != 1) throw new CapturePairingConflictException("Pairing approval conflicted.");
        await connection.ExecuteAsync(
            """
            INSERT INTO capture_pairing_audit (request_uuid, action, operator_subject)
            VALUES (@requestId, 'approved', @operatorSubject)
            """, new { requestId, operatorSubject }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new(bindingId, approval.Label);
    }

    public async Task<CapturePairingPoll?> PollAsync(
        Guid requestId, string pollingToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pollingToken)) return null;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        PollRow? request = await connection.QuerySingleOrDefaultAsync<PollRow>(
            """
            SELECT status, expires_at AS ExpiresAt, delivery_credential AS Credential
            FROM capture_pairing_requests
            WHERE request_uuid=@requestId AND polling_token_hash=@tokenHash FOR UPDATE
            """, new { requestId, tokenHash = Hash(pollingToken) }, transaction);
        if (request is null) return null;
        if (request.Status is "cancelled" or "delivered"
            || request.Status == "pending" && request.ExpiresAt <= _time.GetUtcNow())
            return new("gone", null);
        if (request.Status == "pending") return new("pending", null);
        if (request.Status != "approved" || request.Credential is null)
            throw new InvalidOperationException(
                "Approved pairing request has no deliverable credential.");
        await connection.ExecuteAsync(
            """
            UPDATE capture_pairing_requests SET status='delivered', delivery_credential=NULL
            WHERE request_uuid=@requestId AND status='approved'
            """, new { requestId }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO capture_pairing_audit (request_uuid, action) VALUES (@requestId, 'delivered')",
            new { requestId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return new("approved", request.Credential);
    }

    public async Task<bool> CancelAsync(
        Guid requestId, string? pollingToken, string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string tokenClause = pollingToken is null ? "" : " AND polling_token_hash=@tokenHash";
        int changed = await connection.ExecuteAsync(
            $"UPDATE capture_pairing_requests SET status='cancelled' WHERE request_uuid=@requestId AND status='pending' AND expires_at>@now{tokenClause}",
            new
            {
                requestId,
                now = _time.GetUtcNow(),
                tokenHash = pollingToken is null ? null : Hash(pollingToken)
            }, transaction);
        if (changed == 1)
            await connection.ExecuteAsync(
                "INSERT INTO capture_pairing_audit (request_uuid, action, operator_subject) VALUES (@requestId, 'cancelled', @operatorSubject)",
                new { requestId, operatorSubject }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    private static string RandomSecret(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private class PairingRow
    {
        public string Status { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public string CodexInstallationId { get; set; } = "";
    }
    private sealed class PollRow : PairingRow { public string? Credential { get; set; } }
}

public sealed class CapturePairingConflictException(string message) : Exception(message);
