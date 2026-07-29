using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

/// <summary>
/// Harness-neutral execution seam for the explicitly disabled fixture tracer.
/// It matches durable queued responsibility back to the source fixture, owns
/// the local safety gate and HTTP delivery, and persists each successful
/// receipt through its callback before attempting the next queued record.
/// An injected adapter owns only source interpretation.
///
/// The runtime crosses the SAME governed gate the server crosses, before it
/// emits anything. A record absent from the durable queue is never delivered;
/// the server then scans each delivered observation independently before
/// canonical append.
///
/// It scans but does not REWRITE what it transmits. Pre-redacting the wire
/// would hand the server already-sanitized bytes and make it record
/// <c>scan_status = "clean"</c> with no rule ids for content that was in fact
/// redacted, destroying the provenance parent #73 requires. Imported content
/// supplies evidence only; the server stays the sole author of canonical scan
/// provenance. What the runtime owes is a refusal: any scan failure or budget
/// exhaustion means nothing is emitted at all.
/// </summary>
public static class DisabledCaptureRuntime
{
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(5);

    public static async Task<IReadOnlyList<string>> RunClaimedFixtureAsync(
        ICaptureSourceAdapter adapter,
        string fixturePath,
        string sourceSessionId,
        IReadOnlyList<CaptureRuntimeQueueItem> queue,
        Uri captureEndpoint,
        string credential,
        NeverStoreGate safetyGate,
        Func<string, CaptureRuntimeQueueItem, CancellationToken, Task> persistReceiptAsync,
        CancellationToken cancellationToken = default,
        bool terminalAtEndOfFile = false,
        string? transcriptIdentity = null,
        CaptureSourceIdentity? sourceIdentity = null,
        int maxTransportBytes = CaptureFidelityPolicy.ProductionTransportBytes)
    {
        byte[] sourceBytes = await File.ReadAllBytesAsync(fixturePath, cancellationToken);
        var sourceRecords = JsonlSourceReader.Read(
            sourceBytes,
            sourceIdentity ?? new CaptureSourceIdentity(sourceSessionId),
            terminalAtEndOfFile);
        var recordsByPosition = sourceRecords.ToDictionary(record => record.SourcePosition);
        transcriptIdentity ??= Digest(
            Encoding.UTF8.GetBytes(Path.GetFullPath(fixturePath)));
        using var client = new HttpClient
        {
            // The runtime owns timeout classification so scheduler cancellation
            // remains distinguishable from a request that stopped responding.
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        var receipts = new List<string>();

        foreach (CaptureRuntimeQueueItem queued in queue.OrderBy(item => item.SourcePosition))
        {
            CaptureRuntimeLocatorEvidence evidence = queued.DeterministicLocatorEvidence;
            if (!string.Equals(queued.SourceStream, sourceSessionId, StringComparison.Ordinal)
                || !string.Equals(
                    evidence.TranscriptIdentity, transcriptIdentity, StringComparison.Ordinal)
                || !recordsByPosition.TryGetValue(queued.SourcePosition, out var sourceRecord)
                || sourceRecord.Locator is not CaptureSourceLocator.ByteRange locator
                || locator.Offset != evidence.ByteOffset
                || locator.Length != evidence.ByteLength
                || !string.Equals(
                    locator.SourceContentSha256, evidence.RecordSha256, StringComparison.Ordinal)
                || evidence.PrefixEvidence.ByteLength
                    != checked(evidence.ByteOffset + evidence.ByteLength)
                || evidence.PrefixEvidence.ByteLength > sourceBytes.LongLength
                || !string.Equals(
                    evidence.PrefixEvidence.Sha256,
                    Digest(sourceBytes.AsSpan(
                        0, checked((int)evidence.PrefixEvidence.ByteLength))),
                    StringComparison.Ordinal))
            {
                throw new CaptureRuntimeConflictException(
                    CaptureRuntimeConflictClassifier.QueuedSourceEvidenceChanged(
                        queued.SourcePosition));
            }

            var outcome = adapter.Adapt(sourceRecord);
            if (outcome is CaptureSourcePositionOutcome.Incomplete)
            {
                throw new CaptureRuntimeConflictException(
                    CaptureRuntimeConflictClassifier.QueuedSourceEvidenceChanged(
                        queued.SourcePosition));
            }

            var terminal = (CaptureSourcePositionOutcome.Terminal)outcome;
            string originalJson = JsonSerializer.Serialize(
                terminal.Observation, JsonDefaults.Options);
            long originalByteCount = Encoding.UTF8.GetByteCount(originalJson);
            CaptureObservationRequest boundedObservation =
                CaptureFidelityPolicy.ApplyTransportLimit(
                    terminal.Observation,
                    originalByteCount,
                    maxTransportBytes);
            string observationJson = ReferenceEquals(
                    boundedObservation, terminal.Observation)
                ? originalJson
                : JsonSerializer.Serialize(boundedObservation, JsonDefaults.Options);
            // Fail closed before the observation leaves the process: the scan
            // runs here, and a scan FAILURE — an exhausted budget, a matcher
            // timeout, an internal scanner error, or an unusable rule set —
            // throws out of here and nothing is sent. A leaf the scanner
            // cannot map to an exact span is not a failure: it becomes an
            // explicit omission the server persists as one, so this call does
            // not refuse on omissions. The scan result itself is deliberately
            // discarded. In-limit observations retain their original payload
            // on the wire; observations already compacted for the transport
            // limit carry that omission instead. The server remains the sole
            // author of canonical scan provenance in either case.
            safetyGate.AssertObservationWithinBudget(observationJson);
            string candidateJson = safetyGate.ScanJson(observationJson).Redacted;
            if (!string.Equals(
                    candidateJson, queued.RedactedSafeCandidate, StringComparison.Ordinal))
            {
                throw new CaptureRuntimeConflictException(
                    CaptureRuntimeConflictClassifier.QueuedSourceEvidenceChanged(
                        queued.SourcePosition));
            }
            using var content = new StringContent(
                observationJson, Encoding.UTF8, "application/json");
            using var requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(RequestTimeout);
            try
            {
                using var response = await client.PostAsync(
                    new Uri(captureEndpoint, "/capture/v1/observations"),
                    content,
                    requestCancellation.Token);
                string responseText = await response.Content.ReadAsStringAsync(
                    requestCancellation.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (CaptureRuntimeConflictClassifier.FromHttpFailure(
                            terminal.SourcePosition,
                            response.StatusCode,
                            responseText) is { } stop)
                    {
                        throw new CaptureRuntimeConflictException(stop);
                    }
                    throw new CaptureDeliveryException(
                        terminal.SourcePosition,
                        response.StatusCode);
                }
                await persistReceiptAsync(
                    responseText, queued, cancellationToken);
                receipts.Add(responseText);
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested
                && requestCancellation.IsCancellationRequested)
            {
                throw new CaptureDeliveryTimeoutException(
                    terminal.SourcePosition, RequestTimeout, ex);
            }
        }

        return receipts;
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

}

public static class CaptureRuntimeConflictClassifier
{
    public static CaptureRuntimeStopState QueuedSourceEvidenceChanged(
        long sourcePosition) =>
        new(CaptureRuntimeStopCode.QueuedSourceEvidenceChanged, sourcePosition);

    public static CaptureRuntimeStopState? FromHttpFailure(
        long sourcePosition,
        HttpStatusCode statusCode,
        string responseText)
    {
        if (statusCode != HttpStatusCode.Conflict)
        {
            return null;
        }

        try
        {
            using JsonDocument response = JsonDocument.Parse(responseText);
            if (response.RootElement.TryGetProperty("reason", out JsonElement reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is { } reasonCode)
            {
                return reasonCode switch
                {
                    "blocked_by_earlier_gap" => new CaptureRuntimeStopState(
                        CaptureRuntimeStopCode.BlockedByEarlierGap,
                        sourcePosition),
                    "accepted_source_conflict" => new CaptureRuntimeStopState(
                        CaptureRuntimeStopCode.AcceptedSourceConflict,
                        sourcePosition),
                    _ => null
                };
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}

public sealed class CaptureDeliveryTimeoutException(
    long sourcePosition,
    TimeSpan timeout,
    Exception innerException)
    : HttpRequestException(
        $"Capture delivery at source position {sourcePosition} received no response " +
        $"within {timeout.TotalSeconds:0.###} seconds.",
        innerException)
{
    public long SourcePosition { get; } = sourcePosition;
    public TimeSpan Timeout { get; } = timeout;
}

public sealed class CaptureDeliveryException(
    long sourcePosition,
    HttpStatusCode statusCode)
    : Exception(
        $"Capture failed at source position {sourcePosition} " +
        $"with HTTP {(int)statusCode}.")
{
    public long SourcePosition { get; } = sourcePosition;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
