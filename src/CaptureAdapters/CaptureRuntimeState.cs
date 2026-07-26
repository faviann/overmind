using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        CaptureRuntimeClaim claim,
        CapturePrefixEvidence? expectedPrefix,
        CancellationToken cancellationToken = default);

    Task RecordServerReceiptAsync(
        string sourceStream,
        CaptureServerReceiptState receipt,
        CancellationToken cancellationToken = default);
}

public sealed record CapturePrefixEvidence(long ByteLength, string Sha256);

public sealed record CaptureRuntimeClaim(
    string SourceStream,
    string TranscriptIdentity,
    long SourcePosition,
    long ByteOffset,
    long ByteLength,
    string RecordSha256,
    CapturePrefixEvidence VerifiedPrefix,
    string LocatorIdentity,
    string CandidateObservationJson);

public sealed record CaptureRuntimeQueueItem(
    string SourceStream,
    string TranscriptIdentity,
    long SourcePosition,
    long ByteOffset,
    long ByteLength,
    string RecordSha256,
    CapturePrefixEvidence PrefixEvidence,
    string LocatorIdentity,
    string CandidateObservationJson);

public sealed record CaptureServerReceiptState(
    long SourcePosition,
    string LocatorIdentity,
    string Status,
    Guid? ObservationUuid);

public sealed record CaptureRuntimeStreamState(
    string SourceStream,
    string TranscriptIdentity,
    CapturePrefixEvidence? VerifiedPrefix,
    long? EnqueuedThrough,
    IReadOnlyList<CaptureRuntimeQueueItem> Queue,
    CaptureServerReceiptState? LastServerReceipt);

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
        CaptureRuntimeClaim claim,
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
                stream.TranscriptIdentity, claim.TranscriptIdentity, StringComparison.Ordinal))
        {
            throw new CapturePrefixChangedException(
                claim.SourceStream, "transcript identity changed");
        }
        if (!Equals(stream?.VerifiedPrefix, expectedPrefix))
        {
            throw new CaptureRuntimeConcurrencyException(claim.SourceStream);
        }
        if (stream?.Queue.Any(item =>
                string.Equals(item.LocatorIdentity, claim.LocatorIdentity, StringComparison.Ordinal))
            == true)
        {
            return false;
        }

        var queue = stream?.Queue.ToList() ?? [];
        queue.Add(new CaptureRuntimeQueueItem(
            claim.SourceStream,
            claim.TranscriptIdentity,
            claim.SourcePosition,
            claim.ByteOffset,
            claim.ByteLength,
            claim.RecordSha256,
            claim.VerifiedPrefix,
            claim.LocatorIdentity,
            claim.CandidateObservationJson));
        var nextStream = new CaptureRuntimeStreamState(
            claim.SourceStream,
            claim.TranscriptIdentity,
            claim.VerifiedPrefix,
            claim.SourcePosition,
            queue,
            stream?.LastServerReceipt);
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
        if (!stream.Queue.Any(item =>
                string.Equals(
                    item.LocatorIdentity, receipt.LocatorIdentity, StringComparison.Ordinal)
                && item.SourcePosition == receipt.SourcePosition))
        {
            throw new InvalidOperationException(
                $"Server receipt does not match a queued claim for '{sourceStream}'.");
        }
        streams[streamIndex] = stream with { LastServerReceipt = receipt };
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
    public static async Task<IReadOnlyList<CaptureRuntimeClaim>> ClaimCompletedAsync(
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
        var claimed = new List<CaptureRuntimeClaim>();
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
            string locatorIdentity = LocatorIdentity(
                transcriptIdentity,
                record.SourcePosition,
                byteRange,
                prefix);
            var claim = new CaptureRuntimeClaim(
                sourceStream,
                transcriptIdentity,
                record.SourcePosition,
                byteRange.Offset,
                byteRange.Length,
                byteRange.SourceContentSha256!,
                prefix,
                locatorIdentity,
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

    private static string LocatorIdentity(
        string transcriptIdentity,
        long sourcePosition,
        CaptureSourceLocator.ByteRange locator,
        CapturePrefixEvidence prefix)
    {
        string canonical = string.Join(
            "\n",
            "capture-locator/v1",
            transcriptIdentity,
            sourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            locator.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            locator.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            locator.SourceContentSha256,
            prefix.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            prefix.Sha256);
        return Digest(Encoding.UTF8.GetBytes(canonical));
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
