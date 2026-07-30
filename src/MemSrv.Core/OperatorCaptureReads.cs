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
    private static readonly CaptureReplayOrderBasis ReplayOrderBasis = new(
        "capture_observations.source_position",
        "captured_events.part_order");

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

    /// <summary>
    /// Replays every canonical event for exactly one accepted source stream.
    /// Presentation order is the observation's verified source position,
    /// followed by the event's source-stated part order.
    /// </summary>
    public async Task<CapturedSourceStreamReplay> ReplaySourceStreamAsync(
        Guid sourceStreamUuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var observations = await CaptureLedger.LoadSourceOrderedObservationsAsync(
            connection, sourceStreamUuid);
        if (observations.Count == 0)
        {
            throw new InvalidOperationException(
                $"Capture source stream '{sourceStreamUuid}' was not found.");
        }

        var replayedEvents = new List<CapturedEventReplay>();
        foreach (var item in observations)
        {
            var events = await CaptureLedger.LoadEventsAsync(
                connection, item.Observation.ObservationUuid);
            replayedEvents.AddRange(events.Select(captured => new CapturedEventReplay(
                item.SourcePosition,
                new CapturedEventEnvelope(
                    ContractVersion,
                    item.Observation,
                    captured.Event,
                    captured.Relationships))));
        }

        return new CapturedSourceStreamReplay(
            ContractVersion,
            sourceStreamUuid,
            ReplayOrderBasis,
            replayedEvents);
    }
}
