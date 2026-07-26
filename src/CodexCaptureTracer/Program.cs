using CaptureAdapters;
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
const string sessionId = "codex-synthetic-rollout-v1";

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
    var receipts = await DisabledCaptureRuntime.RunFixtureAsync(
        new CodexJsonlAdapter(),
        fixturePath,
        sessionId,
        new Uri(endpoint, UriKind.Absolute),
        credential);
    foreach (string receipt in receipts)
    {
        Console.WriteLine(receipt);
    }
}
catch (CaptureDeliveryException ex)
{
    Console.Error.WriteLine(ex.Message);
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
