using Npgsql;

namespace MemSrv.Core;

/// <summary>
/// Operator reads over canonical capture state. It returns complete versioned
/// captured-event envelopes, so a caller serializes what it is given and never
/// assembles an envelope from lower-level parts.
/// </summary>
public sealed class OperatorCaptureReads(string connectionString)
{
    private const int ContractVersion = 1;

    /// <summary>
    /// One envelope per canonical event of the observation, in part order. Each
    /// repeats the immutable observation and carries that event's source
    /// relationships.
    /// </summary>
    public async Task<IReadOnlyList<CapturedEventEnvelope>> ReadCapturedEventEnvelopesAsync(
        Guid observationUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var observation = await CaptureLedger.LoadObservationAsync(connection, observationUuid)
            ?? throw new InvalidOperationException(
                $"Capture observation '{observationUuid}' was not found.");
        var events = await CaptureLedger.LoadEventsAsync(connection, observationUuid);
        return events
            .Select(item => new CapturedEventEnvelope(
                ContractVersion, observation, item.Event, item.Relationships))
            .ToArray();
    }
}
