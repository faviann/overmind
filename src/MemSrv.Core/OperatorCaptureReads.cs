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
        CaptureOutcomeSummary outcome = CaptureOutcomeAggregation.FromCanonical(
            observation,
            events.Select(item => item.Event.Payload));
        return events
            .Select(item => new CapturedEventEnvelope(
                ContractVersion, observation, item.Event, item.Relationships, outcome))
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
                    captured.Relationships,
                    CaptureOutcomeAggregation.FromCanonical(
                        item.Observation,
                        events.Select(value => value.Event.Payload))))));
        }

        return new CapturedSourceStreamReplay(
            ContractVersion,
            sourceStreamUuid,
            ReplayOrderBasis,
            replayedEvents);
    }

    /// <summary>
    /// Navigates only source-stated captured-session relationships visible
    /// through the caller's explicit namespace authority. Missing, ambiguous,
    /// and unauthorized outgoing targets share one unavailable representation.
    /// </summary>
    public async Task<CapturedSessionNavigation> NavigateCapturedSessionAsync(
        Guid sourceStreamUuid,
        IReadOnlyCollection<string> allowedNamespaces,
        CancellationToken cancellationToken = default)
    {
        if (allowedNamespaces.Count == 0
            || allowedNamespaces.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "At least one non-blank namespace is required.",
                nameof(allowedNamespaces));
        }

        string[] authority = allowedNamespaces
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var session = await CaptureLedger.LoadAuthorizedSessionAsync(
            connection, sourceStreamUuid, authority)
            ?? throw new InvalidOperationException(
                "Captured source stream is unavailable for the supplied namespace authority.");

        var relationships = new List<CapturedSessionRelationship>();
        foreach (var outgoing in await CaptureLedger.LoadOutgoingSessionRelationshipsAsync(
            connection, sourceStreamUuid))
        {
            CapturedSessionReference? target =
                (await CaptureLedger.ResolveAuthorizedRelationshipTargetAsync(
                    connection, outgoing, authority))?.ToReference();
            CaptureSessionRelationshipEvidence evidence = outgoing.ToEvidence();
            if (target is null)
            {
                evidence = evidence with { TargetSourceStreamUuid = null };
            }
            relationships.Add(new CapturedSessionRelationship(
                "outgoing",
                target is null ? "unavailable" : "available",
                evidence,
                target));
        }

        foreach (var incoming in await CaptureLedger.LoadIncomingSessionRelationshipsAsync(
            connection, session, authority))
        {
            relationships.Add(new CapturedSessionRelationship(
                "incoming",
                "available",
                incoming.ToEvidence(),
                incoming.ToSourceReference()));
        }

        return new CapturedSessionNavigation(
            ContractVersion,
            session.ToReference(),
            relationships);
    }
}
