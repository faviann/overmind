using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

/// <summary>
/// The trusted local material from which one source observation can be made.
/// A record and a hook fact use the same identity and position contract.
/// </summary>
public sealed record TrustedSourceObservation(
    CaptureSourceIdentity SourceIdentity,
    long SourcePosition,
    CaptureSourceLocator Locator,
    CaptureSourceMaterialKind MaterialKind,
    JsonElement SourcePayload,
    bool IsTerminal);

public enum CaptureSourceMaterialKind
{
    PersistedRecord,
    HookFact
}

/// <summary>
/// One harness-neutral adapter. Implementations interpret source material but
/// do not authenticate, route, scan, persist, or advance a checkpoint.
/// </summary>
public interface ICaptureSourceAdapter
{
    string Harness { get; }
    CaptureAdapter Identity { get; }
    CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source);
}

/// <summary>
/// The adapter's decision for exactly one trusted source position.
/// Incomplete positions produce no observation and must not advance. Terminal
/// positions produce the complete request that the canonical capture API
/// authenticates, scans, and appends atomically.
/// </summary>
public abstract record CaptureSourcePositionOutcome(long SourcePosition)
{
    public sealed record Incomplete(long Position, string Reason)
        : CaptureSourcePositionOutcome(Position);

    public sealed record Terminal(long Position, CaptureObservationRequest Observation)
        : CaptureSourcePositionOutcome(Position);
}
