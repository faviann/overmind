using Dapper;
using Npgsql;

namespace MemSrv.Core;

public static class CaptureInstructionOperations
{
    public const string Scan = "scan";
    public const string Retry = "retry";
    public const string Pause = "pause";
    public const string Resume = "resume";

    public static string Require(string operation) => operation switch
    {
        Scan or Retry or Pause or Resume => operation,
        _ => throw new ArgumentException(
            "Capture instruction operation must be scan, retry, pause, or resume.",
            nameof(operation))
    };
}

public sealed record CaptureInstruction(
    Guid InstructionId,
    string Operation,
    DateTimeOffset CreatedAt);

public sealed record CaptureInstructionPoll(
    bool Paused,
    IReadOnlyList<CaptureInstruction> Instructions);

public sealed record CaptureInstructionAcknowledgement(
    Guid InstructionId,
    DateTimeOffset AcknowledgedAt);

public sealed record CaptureInstructionCreateRequest(
    string StableName,
    string Operation);

/// <summary>
/// Owns the complete closed capture-instruction lifecycle. Runtime callers
/// always provide an already-authenticated binding; operator callers name a
/// binding and an audited identity.
/// </summary>
public sealed class CaptureInstructions(string connectionString)
{
    public async Task<Guid> CreateAsync(
        string stableName,
        string operation,
        string operatorIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorIdentity);
        operation = CaptureInstructionOperations.Require(operation);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        Guid? created = await connection.ExecuteScalarAsync<Guid?>(
            """
            INSERT INTO capture_instructions (binding_uuid, operation, created_by)
            SELECT binding_uuid, @operation, @operatorIdentity
            FROM capture_source_bindings
            WHERE stable_name = @stableName AND active
            RETURNING instruction_uuid
            """,
            new { stableName, operation, operatorIdentity });
        return created ?? throw new KeyNotFoundException(
            $"Active capture binding '{stableName}' was not found.");
    }

    public async Task<CaptureInstructionPoll> PollAsync(
        CaptureBindingContext binding,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<InstructionRow>(
            """
            SELECT instruction_uuid AS InstructionId, operation,
                   created_at AS CreatedAt
            FROM capture_instructions
            WHERE binding_uuid = @bindingUuid AND acknowledged_at IS NULL
            ORDER BY created_at, instruction_uuid
            """,
            new { bindingUuid = binding.BindingUuid })).ToArray();
        string? latestPolicy = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT operation
            FROM capture_instructions
            WHERE binding_uuid = @bindingUuid AND operation IN ('pause', 'resume')
            ORDER BY created_at DESC, instruction_uuid DESC
            LIMIT 1
            """,
            new { bindingUuid = binding.BindingUuid });
        return new CaptureInstructionPoll(
            latestPolicy == CaptureInstructionOperations.Pause,
            rows.Select(row => new CaptureInstruction(
                row.InstructionId, row.Operation, row.CreatedAt)).ToArray());
    }

    public async Task<CaptureInstructionAcknowledgement?> AcknowledgeAsync(
        CaptureBindingContext binding,
        Guid instructionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        AcknowledgementRow? row = await connection.QuerySingleOrDefaultAsync<AcknowledgementRow>(
            """
            UPDATE capture_instructions
            SET acknowledged_at = COALESCE(acknowledged_at, now())
            WHERE instruction_uuid = @instructionId AND binding_uuid = @bindingUuid
            RETURNING instruction_uuid AS InstructionId,
                      acknowledged_at AS AcknowledgedAt
            """,
            new { instructionId, bindingUuid = binding.BindingUuid });
        return row is null
            ? null
            : new CaptureInstructionAcknowledgement(row.InstructionId, row.AcknowledgedAt);
    }

    public async Task<bool> IsOwnedByAsync(
        CaptureBindingContext binding,
        Guid instructionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM capture_instructions
                WHERE instruction_uuid = @instructionId
                  AND binding_uuid = @bindingUuid
            )
            """,
            new { instructionId, bindingUuid = binding.BindingUuid });
    }

    private sealed class InstructionRow
    {
        public Guid InstructionId { get; set; }
        public string Operation { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class AcknowledgementRow
    {
        public Guid InstructionId { get; set; }
        public DateTimeOffset AcknowledgedAt { get; set; }
    }
}
