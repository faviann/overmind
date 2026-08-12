using CaptureAdapters;
using MemSrv.Core;
using System.Text.Json;

const string LegacySyntheticEnableValue = "synthetic-non-production";
bool legacySyntheticDiagnostics = string.Equals(
    Environment.GetEnvironmentVariable("OVERMIND_CODEX_CAPTURE_ENABLE"),
    LegacySyntheticEnableValue,
    StringComparison.Ordinal);
bool runOnce = string.Equals(
    Environment.GetEnvironmentVariable("OVERMIND_CAPTURE_RUN_ONCE"),
    "true",
    StringComparison.OrdinalIgnoreCase);

string endpoint;
string credential;
string transcriptRoot;
string stateDirectory;
bool useLegacySyntheticDiscovery;
try
{
    endpoint = Required("OVERMIND_CAPTURE_URL").TrimEnd('/');
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
    {
        throw new InvalidOperationException("OVERMIND_CAPTURE_URL must be an absolute URL.");
    }
    credential = Required("OVERMIND_CAPTURE_CREDENTIAL");
    if (!IsRestrictedCaptureCredential(credential))
    {
        throw new InvalidOperationException(
            "OVERMIND_CAPTURE_CREDENTIAL must be a restricted capture credential.");
    }
    useLegacySyntheticDiscovery = legacySyntheticDiagnostics
        && string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("OVERMIND_CODEX_SESSIONS_ROOT"));
    transcriptRoot = Path.GetFullPath(Required(
        useLegacySyntheticDiscovery
            ? "OVERMIND_CODEX_TRANSCRIPT_ROOT"
            : "OVERMIND_CODEX_SESSIONS_ROOT"));
    stateDirectory = Path.GetFullPath(
        Environment.GetEnvironmentVariable("OVERMIND_CAPTURE_STATE_DIR")
        ?? transcriptRoot + ".overmind-state");
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
{
    WriteDiagnostic("capture_runtime_configuration_invalid");
    return 2;
}

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
    WriteDiagnostic(
        "capture_runtime_configuration_invalid",
        "safety_policy_unavailable");
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
        _ = await runtimeState.DeliverAuthorizedAsync(
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
        WriteDiagnostic("capture_delivery_accepted");
    }
}

CaptureRescanSchedule schedule;
try
{
    schedule = CaptureRescanConfiguration.Load();
}
catch (InvalidOperationException)
{
    WriteDiagnostic("capture_runtime_configuration_invalid", "invalid_scan_schedule");
    return 2;
}
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

try
{
    async Task ScanCycleAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CodexTranscriptStream> streams;
        try
        {
            streams = useLegacySyntheticDiscovery
                ? CodexTranscriptDiscovery.Enumerate(transcriptRoot)
                : CodexTranscriptDiscovery.EnumerateCurrentSessions(transcriptRoot);
        }
        catch (Exception ex) when (IsExpectedFilesystemFailure(ex))
        {
            WriteFailure(ex);
            return;
        }
        await CodexTranscriptScanCycle.RunAsync(
            streams,
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
                    or SafetyConfigurationException
                    || IsExpectedFilesystemFailure(ex))
                {
                    // One source stream or endpoint outage cannot cancel
                    // responsibility for later cycles/streams.
                    WriteFailure(ex);
                }
            },
            WriteFailure,
            cancellationToken);
    }

    if (runOnce)
    {
        await ScanCycleAsync(stopping.Token);
    }
    else
    {
        await CaptureRescanScheduler.RunAsync(
            ScanCycleAsync,
            schedule,
            cancellationToken: stopping.Token);
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
}

WriteDiagnostic("capture_runtime_stopped");
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

static void WriteFailure(Exception failure)
{
    WriteDiagnostic("capture_cycle_failed", FailureCode(failure));
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

static string FailureCode(Exception failure) => failure switch
{
    DirectoryNotFoundException => "transcript_root_unavailable",
    FileNotFoundException => "transcript_stream_unavailable",
    UnauthorizedAccessException => "transcript_access_denied",
    IOException => "transcript_io_unavailable",
    CaptureDeliveryException => "delivery_failed",
    HttpRequestException => "endpoint_unavailable",
    CapturePrefixChangedException => "verified_prefix_changed",
    CaptureStreamStoppedException => "stream_stopped",
    CaptureRuntimeConcurrencyException => "state_concurrency",
    InvalidDataException => "invalid_source_or_receipt",
    JsonException => "invalid_json",
    SafetyScanException => "safety_scan_failed",
    SafetyConfigurationException => "safety_configuration_failed",
    _ => "scan_failed"
};

static bool IsExpectedFilesystemFailure(Exception failure) =>
    failure is IOException or UnauthorizedAccessException;

static void WriteDiagnostic(string eventName, string? reason = null) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        new { @event = eventName, adapter = "codex", reason },
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static void WriteOutcome(CaptureOutcomeSummary outcome) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        outcome,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");

static bool IsRestrictedCaptureCredential(string value) =>
    value.StartsWith("mcap_", StringComparison.Ordinal)
    && value.Length >= 37
    && value.AsSpan(5).IndexOfAnyExcept(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".AsSpan()) < 0;
