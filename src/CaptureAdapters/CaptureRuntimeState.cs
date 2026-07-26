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
        CancellationToken cancellationToken = default);

    Task RecordServerReceiptAsync(
        string sourceStream,
        CaptureServerReceiptState receipt,
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
        string redactedSafeCandidate)
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
    }

    public CaptureRuntimeQueueItem(
        string sourceStream,
        CaptureRuntimeLocatorEvidence deterministicLocatorEvidence,
        string redactedSafeCandidate)
        : this(
            sourceStream,
            deterministicLocatorEvidence.SourcePosition,
            deterministicLocatorEvidence,
            redactedSafeCandidate)
    {
    }

    public string SourceStream { get; }
    public long SourcePosition { get; }
    public CaptureRuntimeLocatorEvidence DeterministicLocatorEvidence { get; }
    public string RedactedSafeCandidate { get; }
}

public sealed record CaptureServerReceiptState(
    long SourcePosition,
    string LocatorIdentity,
    string Status,
    Guid ObservationUuid,
    Guid SourceStreamUuid);

public sealed record CaptureRuntimeStreamState(
    string SourceStream,
    string TranscriptIdentity,
    CapturePrefixEvidence? VerifiedPrefix,
    long? EnqueuedThrough,
    IReadOnlyList<CaptureRuntimeQueueItem> Queue,
    CaptureServerReceiptState? LastServerReceipt,
    Guid? CanonicalSourceStreamUuid);

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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await using FileStream stateLock = await AcquireLockAsync(cancellationToken);
        CaptureRuntimeSnapshot current = await ReadAsync(cancellationToken);
        var streams = current.Streams.ToList();
        int streamIndex = streams.FindIndex(stream =>
            string.Equals(stream.SourceStream, claim.SourceStream, StringComparison.Ordinal));
        CaptureRuntimeStreamState? stream =
            streamIndex >= 0 ? streams[streamIndex] : null;

        if (stream is not null
            && !string.Equals(
                stream.TranscriptIdentity,
                claim.DeterministicLocatorEvidence.TranscriptIdentity,
                StringComparison.Ordinal))
        {
            throw new CapturePrefixChangedException(
                claim.SourceStream, "transcript identity changed");
        }
        if (!Equals(stream?.VerifiedPrefix, expectedPrefix))
        {
            throw new CaptureRuntimeConcurrencyException(claim.SourceStream);
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

        CaptureRuntimeStreamState stream = streams[streamIndex];
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
        streams[streamIndex] = stream with
        {
            Queue = remainingQueue,
            LastServerReceipt = receipt,
            CanonicalSourceStreamUuid =
                stream.CanonicalSourceStreamUuid ?? receipt.SourceStreamUuid
        };
        await WriteAtomicallyAsync(new CaptureRuntimeSnapshot(1, streams), cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        CaptureRuntimeSnapshot snapshot = await state.ReadAsync(cancellationToken);
        CaptureRuntimeStreamState? stream = snapshot.Streams.SingleOrDefault(value =>
            string.Equals(value.SourceStream, sourceStream, StringComparison.Ordinal));
        byte[] sourceBytes = await File.ReadAllBytesAsync(transcriptPath, cancellationToken);
        VerifyKnownPrefix(sourceBytes, stream?.VerifiedPrefix, sourceStream);

        var records = await JsonlSourceReader.ReadAsync(
            transcriptPath, sourceStream, terminalAtEndOfFile: false, cancellationToken);
        string transcriptIdentity = Digest(
            Encoding.UTF8.GetBytes(Path.GetFullPath(transcriptPath)));
        if (stream is not null
            && !string.Equals(
                stream.TranscriptIdentity, transcriptIdentity, StringComparison.Ordinal))
        {
            throw new CapturePrefixChangedException(
                sourceStream, "transcript identity changed");
        }
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
            string originalJson = JsonSerializer.Serialize(
                terminal.Observation, JsonDefaults.Options);
            safetyGate.AssertObservationWithinBudget(originalJson);
            string candidateJson = safetyGate.ScanJson(originalJson).Redacted;
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
                candidateJson);
            if (await state.ClaimAsync(claim, expectedPrefix, cancellationToken))
            {
                claimed.Add(claim);
            }
            expectedPrefix = prefix;
        }

        return claimed;
    }

    private static void VerifyKnownPrefix(
        byte[] bytes, CapturePrefixEvidence? evidence, string sourceStream)
    {
        if (evidence is null)
        {
            return;
        }
        if (evidence.ByteLength < 0 || evidence.ByteLength > bytes.LongLength
            || !string.Equals(
                evidence.Sha256,
                Digest(bytes.AsSpan(0, checked((int)evidence.ByteLength))),
                StringComparison.Ordinal))
        {
            throw new CapturePrefixChangedException(
                sourceStream, "the previously verified prefix changed");
        }
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
}

public sealed class CapturePrefixChangedException(string sourceStream, string reason)
    : Exception($"Capture source stream '{sourceStream}' stopped: {reason}.");

public sealed class CaptureRuntimeConcurrencyException(string sourceStream)
    : Exception($"Capture source stream '{sourceStream}' changed during its claim transaction.");

internal static class RuntimeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
