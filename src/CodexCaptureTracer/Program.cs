using CaptureAdapters;
using MemSrv.Core;
using System.Text.Json;

const string EnableValue = "synthetic-non-production";
if (!string.Equals(
        Environment.GetEnvironmentVariable("OVERMIND_CODEX_CAPTURE_ENABLE"),
        EnableValue,
        StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        $"Codex capture tracer is disabled. Set OVERMIND_CODEX_CAPTURE_ENABLE={EnableValue} " +
        "only for synthetic non-production transcripts.");
    return 2;
}

string endpoint = Required("OVERMIND_CAPTURE_URL").TrimEnd('/');
string credential = Required("OVERMIND_CAPTURE_CREDENTIAL");
string transcriptRoot = Path.GetFullPath(Required("OVERMIND_CODEX_TRANSCRIPT_ROOT"));
string stateDirectory =
    Environment.GetEnvironmentVariable("OVERMIND_CAPTURE_STATE_DIR")
    ?? transcriptRoot + ".overmind-state";

// Fail closed before any source material is read: a tracer whose rule set is
// missing, empty, invalid, duplicated, unsupported, or un-loadable refuses to
// run and says why on stderr. Diagnostics never reach stdout.
var captureOptions = Configuration.Load(Directory.GetCurrentDirectory());
var safetyGate = new NeverStoreGate(
    captureOptions.NeverStorePath, captureOptions.NeverStoreLiteralsPath);
if (!safetyGate.IsConfigured)
{
    CaptureOutcomeSummary outcome = CaptureOutcomeAggregation.Summarize(
    [
        CaptureOutcomeAggregation.SafetyFailure(
            "codex",
            CaptureOutcomeReason.ScannerPolicyUnavailable)
    ]);
    Console.Error.WriteLine(
        $"Codex capture tracer refuses to run: {safetyGate.FailureReason}. " +
        "Capture is unhealthy until the never-store rule set loads.");
    WriteOutcome(outcome);
    return 3;
}

var runtimeState = new FileCaptureRuntimeState(stateDirectory);
var adapter = new CodexJsonlAdapter();

async Task ScanAndDeliverAsync(
    CodexTranscriptStream transcript,
    CancellationToken cancellationToken)
{
    await CodexCaptureClaimer.ClaimCompletedAsync(
        adapter,
        transcript.Path,
        transcript.SourceStream,
        runtimeState,
        safetyGate,
        cancellationToken,
        transcript.TerminalAtEndOfFile,
        transcript.TranscriptIdentity,
        transcript.SourceIdentity);

    CaptureRuntimeStreamState? stream = (await runtimeState.ReadAsync(cancellationToken))
        .Streams.SingleOrDefault(value =>
            string.Equals(
                value.SourceStream, transcript.SourceStream, StringComparison.Ordinal));
    if (stream is null || stream.Queue.Count == 0)
    {
        return;
    }

    foreach (CaptureRuntimeQueueItem queued in
        stream.Queue.OrderBy(item => item.SourcePosition))
    {
        string response = await runtimeState.DeliverAuthorizedAsync(
            transcript.SourceStream,
            queued,
            async token =>
            {
                CaptureServerReceiptState? receiptState = null;
                IReadOnlyList<string> responses =
                    await DisabledCaptureRuntime.RunClaimedFixtureAsync(
                        adapter,
                        transcript.Path,
                        transcript.SourceStream,
                        [queued],
                        new Uri(endpoint, UriKind.Absolute),
                        credential,
                        safetyGate,
                        (receipt, delivered, _) =>
                        {
                            receiptState = ValidateReceipt(receipt, delivered);
                            return Task.CompletedTask;
                        },
                        token,
                        transcript.TerminalAtEndOfFile,
                        transcript.TranscriptIdentity,
                        transcript.SourceIdentity);
                if (receiptState is null || responses.Count != 1)
                {
                    throw new InvalidDataException(
                        "Capture delivery did not return one conclusive receipt.");
                }
                return new CaptureRuntimeDeliveryResult<string>(
                    receiptState, responses[0]);
            },
            cancellationToken);
        Console.WriteLine(response);
    }
}

CaptureRescanSchedule schedule = CaptureRescanConfiguration.Load();
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

try
{
    await CaptureRescanScheduler.RunAsync(
        async cancellationToken =>
        {
            await CodexTranscriptScanCycle.RunAsync(
                CodexTranscriptDiscovery.Enumerate(transcriptRoot),
                async (transcript, token) =>
                {
                    try
                    {
                        await ScanAndDeliverAsync(transcript, token);
                    }
                    catch (Exception ex) when (
                        ex is CaptureDeliveryException
                        or HttpRequestException
                        or CapturePrefixChangedException
                        or CaptureStreamStoppedException
                        or CaptureRuntimeConcurrencyException
                        or InvalidDataException
                        or JsonException
                        or SafetyScanException
                        or SafetyConfigurationException)
                    {
                        // One source stream or endpoint outage cannot cancel
                        // responsibility for later cycles/streams.
                        WriteFailure(ex);
                    }
                },
                WriteFailure,
                cancellationToken);
        },
        schedule,
        cancellationToken: stopping.Token);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
}

WriteLimitation();
return 0;

static CaptureServerReceiptState ValidateReceipt(
    string receipt,
    CaptureRuntimeQueueItem queued)
{
    using JsonDocument document = JsonDocument.Parse(receipt);
    JsonElement root = document.RootElement;
    long receiptSourcePosition = root.GetProperty("sourcePosition").GetInt64();
    if (receiptSourcePosition != queued.SourcePosition)
    {
        throw new InvalidDataException(
            $"Capture server receipt sourcePosition {receiptSourcePosition} " +
            $"does not match queued sourcePosition {queued.SourcePosition}.");
    }
    if (!root.TryGetProperty("status", out JsonElement statusElement)
        || statusElement.ValueKind != JsonValueKind.String
        || statusElement.GetString() is not ("new" or "already_accepted"))
    {
        throw new InvalidDataException(
            "Capture server receipt status must be new or already_accepted.");
    }
    if (!root.TryGetProperty(
            "observationUuid", out JsonElement observationUuidElement)
        || !observationUuidElement.TryGetGuid(out Guid observationUuid))
    {
        throw new InvalidDataException(
            "Capture server receipt observationUuid must be a valid UUID.");
    }
    if (!root.TryGetProperty("observation", out JsonElement observation)
        || !observation.TryGetProperty(
            "observationUuid", out JsonElement nestedObservationUuidElement)
        || !nestedObservationUuidElement.TryGetGuid(out Guid nestedObservationUuid)
        || nestedObservationUuid != observationUuid
        || !observation.TryGetProperty(
            "sourceStreamUuid", out JsonElement sourceStreamUuidElement)
        || !sourceStreamUuidElement.TryGetGuid(out Guid sourceStreamUuid)
        || !observation.TryGetProperty("locator", out JsonElement receiptLocator)
        || receiptLocator.GetProperty("kind").GetString() != "byte_range"
        || receiptLocator.GetProperty("byteOffset").GetInt64()
            != queued.DeterministicLocatorEvidence.ByteOffset
        || receiptLocator.GetProperty("byteLength").GetInt64()
            != queued.DeterministicLocatorEvidence.ByteLength)
    {
        throw new InvalidDataException(
            $"Capture server receipt observation identity or locator does not match " +
            $"queued sourcePosition {queued.SourcePosition}.");
    }

    return new CaptureServerReceiptState(
        receiptSourcePosition,
        queued.DeterministicLocatorEvidence.Identity,
        statusElement.GetString()!,
        observationUuid,
        sourceStreamUuid);
}

static void WriteLimitation() =>
    Console.Error.WriteLine(
        "LIMITATION: disabled non-production synthetic Codex transcript tracer; " +
        "not a live adapter, hook, historical importer, or supported capture product.");

static void WriteFailure(Exception failure)
{
    Console.Error.WriteLine(failure.Message);
    CaptureOutcomeSummary? outcome = failure switch
    {
        SafetyConfigurationException configuration => configuration.Outcome,
        SafetyScanException scan => scan.Outcome,
        _ => null
    };
    if (outcome is not null)
    {
        WriteOutcome(outcome);
    }
}

static void WriteOutcome(CaptureOutcomeSummary outcome) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        outcome,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");
