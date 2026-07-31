using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemSrv.Core;

namespace CaptureAdapters;

/// <summary>
/// The deliberate public boundary over the Codex capture runtime's one
/// writable durable-state volume. A claim is one transaction: verified-prefix
/// evidence, queued responsibility, and enqueued-through progress either all
/// become visible or none do.
/// </summary>
public interface ICaptureRuntimeState
{
    Task<CaptureRuntimeSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    Task<bool> ClaimAsync(
        CaptureRuntimeQueueItem claim,
        CapturePrefixEvidence? expectedPrefix,
        Func<CapturePrefixEvidence?, bool> verifiedPrefixMatchesSnapshot,
        CancellationToken cancellationToken = default);

    Task<CaptureRuntimeStreamState?> InspectSourceAsync(
        string sourceStream,
        Func<CaptureRuntimeStreamState, CaptureRuntimeStopState?> detectConflict,
        CancellationToken cancellationToken = default);

    Task RecordServerReceiptAsync(
        string sourceStream,
        CaptureServerReceiptState receipt,
        CancellationToken cancellationToken = default);

    Task<TResult> DeliverAuthorizedAsync<TResult>(
        string sourceStream,
        CaptureRuntimeQueueItem queued,
        Func<CancellationToken, Task<CaptureRuntimeDeliveryResult<TResult>>> deliverAsync,
        CancellationToken cancellationToken = default);

}

public sealed record CapturePrefixEvidence(long ByteLength, string Sha256);

/// <summary>
/// Complete, immutable mechanical evidence for one persisted source record.
/// Its identity binds every field and is independent of delivery requests or
/// batches.
/// </summary>
public sealed record CaptureRuntimeLocatorEvidence
{
    public CaptureRuntimeLocatorEvidence(
        string transcriptIdentity,
        long sourcePosition,
        long byteOffset,
        long byteLength,
        string recordSha256,
        CapturePrefixEvidence prefixEvidence)
    {
        TranscriptIdentity = transcriptIdentity;
        SourcePosition = sourcePosition;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        RecordSha256 = recordSha256;
        PrefixEvidence = prefixEvidence;
        Identity = CalculateIdentity(
            transcriptIdentity, sourcePosition, byteOffset, byteLength,
            recordSha256, prefixEvidence);
    }

    public string TranscriptIdentity { get; }
    public long SourcePosition { get; }
    public long ByteOffset { get; }
    public long ByteLength { get; }
    public string RecordSha256 { get; }
    public CapturePrefixEvidence PrefixEvidence { get; }
    public string Identity { get; }

    private static string CalculateIdentity(
        string transcriptIdentity,
        long sourcePosition,
        long byteOffset,
        long byteLength,
        string recordSha256,
        CapturePrefixEvidence prefix)
    {
        string canonical = string.Join(
            "\n",
            "capture-locator/v1",
            transcriptIdentity,
            sourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byteOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            recordSha256,
            prefix.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            prefix.Sha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

public sealed record CaptureRuntimeQueueItem
{
    [JsonConstructor]
    public CaptureRuntimeQueueItem(
        string sourceStream,
        long sourcePosition,
        CaptureRuntimeLocatorEvidence deterministicLocatorEvidence,
        string redactedSafeCandidate,
        CaptureOutcomeSummary? outcome = null)
    {
        if (sourcePosition != deterministicLocatorEvidence.SourcePosition)
        {
            throw new InvalidDataException(
                "Queued sourcePosition must match deterministic locator evidence.");
        }
        SourceStream = sourceStream;
        SourcePosition = sourcePosition;
        DeterministicLocatorEvidence = deterministicLocatorEvidence;
        RedactedSafeCandidate = redactedSafeCandidate;
        Outcome = outcome ?? CaptureOutcomeAggregation.Empty;
    }

    public CaptureRuntimeQueueItem(
        string sourceStream,
        CaptureRuntimeLocatorEvidence deterministicLocatorEvidence,
        string redactedSafeCandidate,
        CaptureOutcomeSummary? outcome = null)
        : this(
            sourceStream,
            deterministicLocatorEvidence.SourcePosition,
            deterministicLocatorEvidence,
            redactedSafeCandidate,
            outcome)
    {
    }

    public string SourceStream { get; }
    public long SourcePosition { get; }
    public CaptureRuntimeLocatorEvidence DeterministicLocatorEvidence { get; }
    public string RedactedSafeCandidate { get; }
    public CaptureOutcomeSummary Outcome { get; }
}

public sealed record CaptureServerReceiptState(
    long SourcePosition,
    string LocatorIdentity,
    string Status,
    Guid ObservationUuid,
    Guid SourceStreamUuid);

public sealed record CaptureRuntimeDeliveryResult<TResult>(
    CaptureServerReceiptState Receipt,
    TResult Result);

public static class CaptureRuntimeStopCode
{
    public const string VerifiedPrefixChanged = "verified_prefix_changed";
    public const string TranscriptIdentityChanged = "transcript_identity_changed";
    public const string QueuedSourceEvidenceChanged = "queued_source_evidence_changed";
    public const string BlockedByEarlierGap = "blocked_by_earlier_gap";
    public const string AcceptedSourceConflict = "accepted_source_conflict";

    internal static bool IsKnown(string code) =>
        code is VerifiedPrefixChanged
            or TranscriptIdentityChanged
            or QueuedSourceEvidenceChanged
            or BlockedByEarlierGap
            or AcceptedSourceConflict;
}

/// <summary>
/// Content-free durable reason that a capture source stream cannot advance.
/// The finite code set prevents transcript or server-response content from
/// entering operational state or diagnostics.
/// </summary>
public sealed record CaptureRuntimeStopState
{
    [JsonConstructor]
    public CaptureRuntimeStopState(string code, long? sourcePosition)
    {
        if (!CaptureRuntimeStopCode.IsKnown(code))
        {
            throw new InvalidDataException("Capture runtime stop code is not recognized.");
        }
        bool locationIsUnknown =
            code is CaptureRuntimeStopCode.VerifiedPrefixChanged
                or CaptureRuntimeStopCode.TranscriptIdentityChanged;
        if (locationIsUnknown && sourcePosition is not null)
        {
            throw new InvalidDataException(
                "Aggregate capture runtime stops cannot identify a sourcePosition.");
        }
        if (!locationIsUnknown && sourcePosition is null)
        {
            throw new InvalidDataException(
                "Record-specific capture runtime stops require a sourcePosition.");
        }
        if (sourcePosition < 0)
        {
            throw new InvalidDataException(
                "Capture runtime stop sourcePosition cannot be negative.");
        }
        Code = code;
        SourcePosition = sourcePosition;
    }

    public string Code { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SourcePosition { get; }
}

public sealed record CaptureRuntimeStreamState(
    string SourceStream,
    string TranscriptIdentity,
    CapturePrefixEvidence? VerifiedPrefix,
    long? EnqueuedThrough,
    IReadOnlyList<CaptureRuntimeQueueItem> Queue,
    CaptureServerReceiptState? LastServerReceipt,
    Guid? CanonicalSourceStreamUuid,
    CaptureRuntimeStopState? Stop = null);

public sealed record CaptureRuntimeSnapshot(
    int ContractVersion,
    IReadOnlyList<CaptureRuntimeStreamState> Streams)
{
    public static CaptureRuntimeSnapshot Empty { get; } = new(1, []);
}

/// <summary>
/// A single-file durable implementation. It writes a complete next snapshot,
/// flushes it to stable storage, then atomically renames it over the prior
/// snapshot. An interrupted write leaves only an ignored temporary file.
/// </summary>
public sealed class FileCaptureRuntimeState : ICaptureRuntimeState
{
    private const string StateFileName = "capture-state.json";
    private readonly string _directory;
    private readonly string _statePath;
    private readonly string _lockPath;

    public FileCaptureRuntimeState(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _statePath = Path.Combine(_directory, StateFileName);
        _lockPath = Path.Combine(_directory, ".capture-state.lock");
    }

    public async Task<CaptureRuntimeSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath))
        {
            return CaptureRuntimeSnapshot.Empty;
        }

        await using var stream = new FileStream(
            _statePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<CaptureRuntimeSnapshot>(
                stream, RuntimeJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Capture runtime state is empty.");
    }

    public async Task<bool> ClaimAsync(
        CaptureRuntimeQueueItem claim,
        CapturePrefixEvidence? expectedPrefix,
        Func<CapturePrefixEvidence?, bool> verifiedPrefixMatchesSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedPrefixMatchesSnapshot);
        Directory.CreateDirectory(_directory);
        await using FileStream stateLock = await AcquireLockAsync(cancellationToken);
        CaptureRuntimeSnapshot current = await ReadAsync(cancellationToken);
        var streams = current.Streams.ToList();
        int streamIndex = streams.FindIndex(stream =>
            string.Equals(stream.SourceStream, claim.SourceStream, StringComparison.Ordinal));
        CaptureRuntimeStreamState? stream =
            streamIndex >= 0 ? streams[streamIndex] : null;

        if (stream?.Stop is { } stop)
        {
            throw new CaptureStreamStoppedException(claim.SourceStream, stop);
        }
        if (stream is not null
            && !string.Equals(
                stream.TranscriptIdentity,
                claim.DeterministicLocatorEvidence.TranscriptIdentity,
                StringComparison.Ordinal))
        {
            CaptureRuntimeStopState durableStop = await PersistStopAsync(
                current,
                streamIndex,
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.TranscriptIdentityChanged,
                    null));
            throw new CaptureStreamStoppedException(claim.SourceStream, durableStop);
        }
        if (!Equals(stream?.VerifiedPrefix, expectedPrefix))
        {
            if (stream is null)
            {
                throw new CaptureRuntimeConcurrencyException(claim.SourceStream);
            }
            if (!verifiedPrefixMatchesSnapshot(stream.VerifiedPrefix))
            {
                CaptureRuntimeStopState durableStop = await PersistStopAsync(
                    current,
                    streamIndex,
                    new CaptureRuntimeStopState(
                        CaptureRuntimeStopCode.VerifiedPrefixChanged,
                        null));
                throw new CaptureStreamStoppedException(claim.SourceStream, durableStop);
            }
        }
        if (stream?.EnqueuedThrough >= claim.SourcePosition)
        {
            return false;
        }
        if (stream?.Queue.Any(item =>
                string.Equals(
                    item.DeterministicLocatorEvidence.Identity,
                    claim.DeterministicLocatorEvidence.Identity,
                    StringComparison.Ordinal))
            == true)
        {
            return false;
        }

        var queue = stream?.Queue.ToList() ?? [];
        queue.Add(claim);
        var nextStream = new CaptureRuntimeStreamState(
            claim.SourceStream,
            claim.DeterministicLocatorEvidence.TranscriptIdentity,
            claim.DeterministicLocatorEvidence.PrefixEvidence,
            claim.SourcePosition,
            queue,
            stream?.LastServerReceipt,
            stream?.CanonicalSourceStreamUuid);
        if (streamIndex >= 0)
        {
            streams[streamIndex] = nextStream;
        }
        else
        {
            streams.Add(nextStream);
        }

        await WriteAtomicallyAsync(new CaptureRuntimeSnapshot(1, streams), cancellationToken);
        return true;
    }

    public async Task<CaptureRuntimeStreamState?> InspectSourceAsync(
        string sourceStream,
        Func<CaptureRuntimeStreamState, CaptureRuntimeStopState?> detectConflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detectConflict);
        Directory.CreateDirectory(_directory);
        await using FileStream stateLock = await AcquireLockAsync(cancellationToken);
        CaptureRuntimeSnapshot current = await ReadAsync(cancellationToken);
        int streamIndex = current.Streams.ToList().FindIndex(stream =>
            string.Equals(stream.SourceStream, sourceStream, StringComparison.Ordinal));
        if (streamIndex < 0)
        {
            return null;
        }

        CaptureRuntimeStreamState stream = current.Streams[streamIndex];
        if (stream.Stop is { } existingStop)
        {
            throw new CaptureStreamStoppedException(sourceStream, existingStop);
        }
        if (detectConflict(stream) is not { } detectedStop)
        {
            return stream;
        }

        CaptureRuntimeStopState durableStop = await PersistStopAsync(
            current, streamIndex, detectedStop);
        throw new CaptureStreamStoppedException(sourceStream, durableStop);
    }

    public async Task RecordServerReceiptAsync(
        string sourceStream,
        CaptureServerReceiptState receipt,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await using FileStream stateLock = await AcquireLockAsync(cancellationToken);
        CaptureRuntimeSnapshot current = await ReadAsync(cancellationToken);
        var streams = current.Streams.ToList();
        int streamIndex = streams.FindIndex(stream =>
            string.Equals(stream.SourceStream, sourceStream, StringComparison.Ordinal));
        if (streamIndex < 0)
        {
            throw new InvalidOperationException(
                $"Capture source stream '{sourceStream}' has no durable claim.");
        }

        streams[streamIndex] = ApplyReceipt(sourceStream, streams[streamIndex], receipt);
        await WriteAtomicallyAsync(new CaptureRuntimeSnapshot(1, streams), cancellationToken);
    }

    public async Task<TResult> DeliverAuthorizedAsync<TResult>(
        string sourceStream,
        CaptureRuntimeQueueItem queued,
        Func<CancellationToken, Task<CaptureRuntimeDeliveryResult<TResult>>> deliverAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliverAsync);
        Directory.CreateDirectory(_directory);
        await using FileStream stateLock = await AcquireLockAsync(cancellationToken);
        CaptureRuntimeSnapshot current = await ReadAsync(cancellationToken);
        var streams = current.Streams.ToList();
        int streamIndex = streams.FindIndex(stream =>
            string.Equals(stream.SourceStream, sourceStream, StringComparison.Ordinal));
        if (streamIndex < 0)
        {
            throw new InvalidOperationException(
                $"Capture source stream '{sourceStream}' has no durable claim.");
        }

        CaptureRuntimeStreamState stream = streams[streamIndex];
        if (stream.Stop is { } stop)
        {
            throw new CaptureStreamStoppedException(sourceStream, stop);
        }
        CaptureRuntimeQueueItem? earliest = stream.Queue
            .OrderBy(item => item.SourcePosition)
            .FirstOrDefault();
        if (earliest is null
            || earliest.SourcePosition != queued.SourcePosition
            || !string.Equals(
                earliest.DeterministicLocatorEvidence.Identity,
                queued.DeterministicLocatorEvidence.Identity,
                StringComparison.Ordinal))
        {
            throw new CaptureRuntimeConcurrencyException(sourceStream);
        }

        CaptureRuntimeDeliveryResult<TResult> delivery;
        try
        {
            delivery = await deliverAsync(cancellationToken);
        }
        catch (CaptureRuntimeConflictException conflict)
        {
            CaptureRuntimeStopState durableStop = await PersistStopAsync(
                current, streamIndex, conflict.Stop);
            throw new CaptureStreamStoppedException(sourceStream, durableStop);
        }
        streams[streamIndex] = ApplyReceipt(sourceStream, stream, delivery.Receipt);
        await WriteAtomicallyAsync(new CaptureRuntimeSnapshot(1, streams), cancellationToken);
        return delivery.Result;
    }

    private async Task<CaptureRuntimeStopState> PersistStopAsync(
        CaptureRuntimeSnapshot current,
        int streamIndex,
        CaptureRuntimeStopState stop)
    {
        var streams = current.Streams.ToList();
        if (streams[streamIndex].Stop is { } existingStop)
        {
            return existingStop;
        }
        streams[streamIndex] = streams[streamIndex] with { Stop = stop };
        await WriteAtomicallyAsync(
            new CaptureRuntimeSnapshot(1, streams), CancellationToken.None);
        return stop;
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static CaptureRuntimeStreamState ApplyReceipt(
        string sourceStream,
        CaptureRuntimeStreamState stream,
        CaptureServerReceiptState receipt)
    {
        if (stream.Stop is { } stop)
        {
            throw new CaptureStreamStoppedException(sourceStream, stop);
        }
        CaptureRuntimeQueueItem? earliest = stream.Queue
            .OrderBy(item => item.SourcePosition)
            .FirstOrDefault();
        if (earliest is null
            || earliest.SourcePosition != receipt.SourcePosition
            || !string.Equals(
                earliest.DeterministicLocatorEvidence.Identity,
                receipt.LocatorIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Server receipt does not match the earliest queued claim for '{sourceStream}'.");
        }
        if (!string.Equals(receipt.Status, "new", StringComparison.Ordinal)
            && !string.Equals(receipt.Status, "already_accepted", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Capture server receipt status '{receipt.Status}' is not conclusive.");
        }
        if (stream.CanonicalSourceStreamUuid is Guid canonicalSourceStreamUuid
            && canonicalSourceStreamUuid != receipt.SourceStreamUuid)
        {
            throw new InvalidDataException(
                $"Capture server receipt sourceStreamUuid does not match the canonical " +
                $"stream UUID for '{sourceStream}'.");
        }

        var remainingQueue = stream.Queue.ToList();
        remainingQueue.Remove(earliest);
        return stream with
        {
            Queue = remainingQueue,
            LastServerReceipt = receipt,
            CanonicalSourceStreamUuid =
                stream.CanonicalSourceStreamUuid ?? receipt.SourceStreamUuid
        };
    }

    private async Task WriteAtomicallyAsync(
        CaptureRuntimeSnapshot state,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            _directory, $".{StateFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, state, RuntimeJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>
/// Claims completed persisted Codex records. Source bytes are used only to
/// verify the append-only prefix and build the adapter candidate; only the
/// locally sanitized candidate crosses the durable-state boundary.
/// </summary>
public static class CodexCaptureClaimer
{
    public static async Task<IReadOnlyList<CaptureRuntimeQueueItem>> ClaimCompletedAsync(
        ICaptureSourceAdapter adapter,
        string transcriptPath,
        string sourceStream,
        ICaptureRuntimeState state,
        NeverStoreGate safetyGate,
        CancellationToken cancellationToken = default,
        bool terminalAtEndOfFile = false,
        string? transcriptIdentity = null,
        CaptureSourceIdentity? sourceIdentity = null,
        int maxTransportBytes = CaptureFidelityPolicy.ProductionTransportBytes)
    {
        byte[] sourceBytes = await File.ReadAllBytesAsync(transcriptPath, cancellationToken);
        transcriptIdentity ??= Digest(
            Encoding.UTF8.GetBytes(Path.GetFullPath(transcriptPath)));
        string immutableTranscriptIdentity = transcriptIdentity;
        CaptureRuntimeStreamState? stream = await state.InspectSourceAsync(
            sourceStream,
            current =>
            {
                if (!KnownPrefixMatches(sourceBytes, current.VerifiedPrefix))
                {
                    return new CaptureRuntimeStopState(
                        CaptureRuntimeStopCode.VerifiedPrefixChanged,
                        null);
                }
                if (!string.Equals(
                        current.TranscriptIdentity,
                        immutableTranscriptIdentity,
                        StringComparison.Ordinal))
                {
                    return new CaptureRuntimeStopState(
                        CaptureRuntimeStopCode.TranscriptIdentityChanged,
                        null);
                }
                return null;
            },
            cancellationToken);
        var records = JsonlSourceReader.Read(
            sourceBytes,
            sourceIdentity ?? new CaptureSourceIdentity(sourceStream),
            terminalAtEndOfFile);
        var claimed = new List<CaptureRuntimeQueueItem>();
        CapturePrefixEvidence? expectedPrefix = stream?.VerifiedPrefix;

        foreach (TrustedSourceObservation record in records)
        {
            if (record.SourcePosition <= (stream?.EnqueuedThrough ?? -1))
            {
                continue;
            }
            var terminal = adapter.Adapt(record) as CaptureSourcePositionOutcome.Terminal;
            if (terminal is null)
            {
                break;
            }
            if (record.Locator is not CaptureSourceLocator.ByteRange byteRange)
            {
                throw new InvalidDataException(
                    "Persisted Codex records require a verified byte-range locator.");
            }
            VerifyRecord(sourceBytes, byteRange, sourceStream, record.SourcePosition);

            long prefixLength = checked(byteRange.Offset + byteRange.Length);
            var prefix = new CapturePrefixEvidence(
                prefixLength,
                Digest(sourceBytes.AsSpan(0, checked((int)prefixLength))));
            BoundedCaptureRepresentation<CaptureObservationRequest> bounded =
                CaptureFidelityPolicy.SerializeForTransport(
                    terminal.Observation,
                    maxTransportBytes);
            string boundedJson = bounded.Serialized;
            string candidateJson;
            try
            {
                safetyGate.AssertObservationWithinBudget(boundedJson);
                candidateJson = safetyGate.ScanJson(boundedJson).Redacted;
            }
            catch (SafetyConfigurationException failure)
            {
                failure.ReportCaptureOutcome(
                    terminal.Observation.Source.Harness,
                    byteRange.Length);
                throw;
            }
            catch (SafetyScanException failure)
            {
                failure.ReportCaptureOutcome(
                    terminal.Observation.Source.Harness,
                    byteRange.Length);
                throw;
            }
            var locatorEvidence = new CaptureRuntimeLocatorEvidence(
                transcriptIdentity,
                record.SourcePosition,
                byteRange.Offset,
                byteRange.Length,
                byteRange.SourceContentSha256!,
                prefix);
            var claim = new CaptureRuntimeQueueItem(
                sourceStream,
                locatorEvidence,
                candidateJson,
                RuntimeOutcome(
                    terminal.Observation.Source.Harness,
                    bounded,
                    byteRange.Length));
            if (await state.ClaimAsync(
                    claim,
                    expectedPrefix,
                    evidence => KnownPrefixMatches(sourceBytes, evidence),
                    cancellationToken))
            {
                claimed.Add(claim);
            }
            expectedPrefix = prefix;
        }

        return claimed;
    }

    private static bool KnownPrefixMatches(
        byte[] bytes, CapturePrefixEvidence? evidence)
    {
        if (evidence is null)
        {
            return true;
        }
        return evidence.ByteLength >= 0
            && evidence.ByteLength <= bytes.LongLength
            && string.Equals(
                evidence.Sha256,
                Digest(bytes.AsSpan(0, checked((int)evidence.ByteLength))),
                StringComparison.Ordinal);
    }

    private static void VerifyRecord(
        byte[] bytes,
        CaptureSourceLocator.ByteRange locator,
        string sourceStream,
        long sourcePosition)
    {
        long end = checked(locator.Offset + locator.Length);
        if (locator.Offset < 0 || locator.Length <= 0 || end > bytes.LongLength
            || locator.SourceContentSha256 is null
            || !string.Equals(
                locator.SourceContentSha256,
                Digest(bytes.AsSpan(
                    checked((int)locator.Offset), checked((int)locator.Length))),
                StringComparison.Ordinal))
        {
            throw new CapturePrefixChangedException(
                sourceStream, $"source record {sourcePosition} changed while being claimed");
        }
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static CaptureOutcomeSummary RuntimeOutcome(
        string harness,
        BoundedCaptureRepresentation<CaptureObservationRequest> bounded,
        long sourceByteCount)
    {
        if (bounded.WasOmitted)
        {
            return CaptureOutcomeAggregation.Summarize(
            [
                CaptureOutcomeAggregation.FidelityOmission(
                    harness,
                    CaptureFidelityPolicy.TransportLimitReason,
                    bounded.OriginalByteCount)
            ]);
        }

        CaptureObservationCommand command =
            CaptureObservationCommand.FromRequest(bounded.Observation);
        return CaptureFidelityPolicy.ClassifyDeterministicFidelity(command)
            is { } fidelity
            ? CaptureOutcomeAggregation.Summarize(
            [
                CaptureOutcomeAggregation.FidelityOmission(
                    harness,
                    fidelity.Reason,
                    sourceByteCount)
            ])
            : CaptureOutcomeAggregation.Empty;
    }
}

public sealed class CapturePrefixChangedException(string sourceStream, string reason)
    : Exception($"Capture source stream '{sourceStream}' stopped: {reason}.");

public sealed class CaptureRuntimeConflictException(CaptureRuntimeStopState stop)
    : Exception($"Capture runtime detected {stop.Code}.")
{
    public CaptureRuntimeStopState Stop { get; } = stop;
}

public sealed class CaptureStreamStoppedException(
    string sourceStream,
    CaptureRuntimeStopState stop)
    : Exception(
        stop.SourcePosition is long sourcePosition
            ? $"Capture source stream '{sourceStream}' is stopped at source position " +
                $"{sourcePosition}: {stop.Code}."
            : $"Capture source stream '{sourceStream}' is stopped at an unknown source " +
                $"position: {stop.Code}.")
{
    public CaptureRuntimeStopState Stop { get; } = stop;
}

public sealed class CaptureRuntimeConcurrencyException(string sourceStream)
    : Exception($"Capture source stream '{sourceStream}' changed during its claim transaction.");

internal static class RuntimeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
