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
        "only for the synthetic non-production fixture.");
    return 2;
}

string endpoint = Required("OVERMIND_CAPTURE_URL").TrimEnd('/');
string credential = Required("OVERMIND_CAPTURE_CREDENTIAL");
string fixturePath = Required("OVERMIND_CODEX_FIXTURE");
string stateDirectory =
    Environment.GetEnvironmentVariable("OVERMIND_CAPTURE_STATE_DIR")
    ?? fixturePath + ".overmind-state";
const string sessionId = "codex-synthetic-rollout-v1";

// Fail closed before any source material is read: a tracer whose rule set is
// missing, empty, invalid, duplicated, unsupported, or un-loadable refuses to
// run and says why on stderr. Diagnostics never reach stdout.
var captureOptions = Configuration.Load(Directory.GetCurrentDirectory());
var safetyGate = new NeverStoreGate(
    captureOptions.NeverStorePath, captureOptions.NeverStoreLiteralsPath);
if (!safetyGate.IsConfigured)
{
    Console.Error.WriteLine(
        $"Codex capture tracer refuses to run: {safetyGate.FailureReason}. " +
        "Capture is unhealthy until the never-store rule set loads.");
    return 3;
}

var sourceRecords = await JsonlSourceReader.ReadAsync(
    fixturePath, sessionId, terminalAtEndOfFile: true);
if (sourceRecords.Count != 3)
{
    throw new InvalidOperationException(
        "Synthetic Codex fixture must contain exactly three JSONL records.");
}
if (sourceRecords.Any(record =>
        record.SourcePayload.GetProperty("type").GetString() != "response_item"
        || record.SourcePayload.GetProperty("timestamp").ValueKind != JsonValueKind.String))
{
    throw new InvalidOperationException(
        "Every synthetic Codex record must be a timestamped response_item rollout record.");
}

var message = sourceRecords[0].SourcePayload.GetProperty("payload");
var call = sourceRecords[1].SourcePayload.GetProperty("payload");
var result = sourceRecords[2].SourcePayload.GetProperty("payload");
if (message.GetProperty("type").GetString() != "message"
    || message.GetProperty("role").GetString() != "user"
    || message.GetProperty("content").ValueKind != JsonValueKind.Array
    || message.GetProperty("content").GetArrayLength() != 1
    || message.GetProperty("content")[0].GetProperty("type").GetString() != "input_text"
    || call.GetProperty("type").GetString() != "function_call"
    || call.GetProperty("arguments").ValueKind != JsonValueKind.String
    || result.GetProperty("type").GetString() != "function_call_output")
{
    throw new InvalidOperationException(
        "Synthetic Codex fixture must contain message, function_call, and " +
        "function_call_output response_item payloads in order.");
}

string callId = call.GetProperty("call_id").GetString()
    ?? throw new InvalidOperationException("Synthetic function_call call_id is required.");
if (!string.Equals(
        callId,
        result.GetProperty("call_id").GetString(),
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Synthetic tool result must match the function_call call_id.");
}

try
{
    var runtimeState = new FileCaptureRuntimeState(stateDirectory);
    await CodexCaptureClaimer.ClaimCompletedAsync(
        new CodexJsonlAdapter(),
        fixturePath,
        sessionId,
        runtimeState,
        safetyGate);

    CaptureRuntimeStreamState stream = (await runtimeState.ReadAsync()).Streams.Single(value =>
        string.Equals(value.SourceStream, sessionId, StringComparison.Ordinal));
    await DisabledCaptureRuntime.RunClaimedFixtureAsync(
        new CodexJsonlAdapter(),
        fixturePath,
        sessionId,
        stream.Queue,
        new Uri(endpoint, UriKind.Absolute),
        credential,
        safetyGate,
        async (receipt, queued, cancellationToken) =>
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
                || string.IsNullOrWhiteSpace(statusElement.GetString()))
            {
                throw new InvalidDataException(
                    "Capture server receipt status must be a nonblank string.");
            }
            if (!root.TryGetProperty(
                    "observationUuid", out JsonElement observationUuidElement)
                || !observationUuidElement.TryGetGuid(out Guid observationUuid))
            {
                throw new InvalidDataException(
                    "Capture server receipt observationUuid must be a valid UUID.");
            }
            await runtimeState.RecordServerReceiptAsync(
                sessionId,
                new CaptureServerReceiptState(
                    receiptSourcePosition,
                    queued.DeterministicLocatorEvidence.Identity,
                    statusElement.GetString()!,
                    observationUuid),
                cancellationToken);
            Console.WriteLine(receipt);
        });
}
catch (CaptureDeliveryException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (SafetyScanException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}
catch (SafetyConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}
catch (CapturePrefixChangedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 4;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Capture delivery failed: {ex.Message}");
    return 1;
}

Console.Error.WriteLine(
    "LIMITATION: disabled non-production synthetic Codex fixture tracer; " +
    "not a live adapter, scheduler, hook, or supported capture product.");
return 0;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");
