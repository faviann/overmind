using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
byte[] fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
var records = new List<(
    JsonElement Record,
    long ByteOffset,
    long ByteLength,
    string SourceContentSha256)>();
int lineStart = 0;
for (int index = 0; index <= fixtureBytes.Length; index++)
{
    if (index != fixtureBytes.Length && fixtureBytes[index] != (byte)'\n')
    {
        continue;
    }
    int separatorLength = index == fixtureBytes.Length
        ? 0
        : index > lineStart && fixtureBytes[index - 1] == (byte)'\r' ? 2 : 1;
    int contentLength = index - lineStart - (separatorLength == 2 ? 1 : 0);
    int recordLength = contentLength + separatorLength;
    if (contentLength > 0)
    {
        records.Add((
            JsonDocument.Parse(fixtureBytes.AsMemory(lineStart, contentLength))
                .RootElement.Clone(),
            lineStart,
            recordLength,
            Convert.ToHexString(
                SHA256.HashData(fixtureBytes.AsSpan(lineStart, recordLength)))
                .ToLowerInvariant()));
    }
    lineStart = index + 1;
}

if (records.Count != 3)
{
    throw new InvalidOperationException("Synthetic Codex fixture must contain exactly three JSONL records.");
}
if (records.Any(record =>
        record.Record.GetProperty("type").GetString() != "response_item"
        || record.Record.GetProperty("timestamp").ValueKind != JsonValueKind.String))
{
    throw new InvalidOperationException(
        "Every synthetic Codex record must be a timestamped response_item rollout record.");
}

var message = records[0].Record.GetProperty("payload");
var call = records[1].Record.GetProperty("payload");
var result = records[2].Record.GetProperty("payload");
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

const string sessionId = "codex-synthetic-rollout-v1";
string callId = call.GetProperty("call_id").GetString()
    ?? throw new InvalidOperationException("Synthetic function_call call_id is required.");
if (!string.Equals(
        callId,
        result.GetProperty("call_id").GetString(),
        StringComparison.Ordinal))
{
    throw new InvalidOperationException("Synthetic tool result must match the function_call call_id.");
}
var arguments = JsonDocument.Parse(
    call.GetProperty("arguments").GetString()
    ?? throw new InvalidOperationException("Synthetic function_call arguments are required."))
    .RootElement.Clone();
var events = new object[]
{
    new
    {
        partKey = "message/0",
        partOrder = 0,
        kind = "message",
        actor = "user",
        payload = new { text = message.GetProperty("content")[0].GetProperty("text").GetString() }
    },
    new
    {
        partKey = "tool/1",
        partOrder = 0,
        kind = "tool_call",
        actor = "assistant",
        payload = new
        {
            callId,
            tool = call.GetProperty("name").GetString(),
            arguments
        }
    },
    new
    {
        partKey = "tool/2",
        partOrder = 0,
        kind = "tool_result",
        actor = "tool",
        payload = new
        {
            callId,
            outcome = "succeeded",
            output = result.GetProperty("output").GetString()
        },
        relationships = new[]
        {
            new
            {
                type = "result_for",
                target = new
                {
                    nativeId = callId,
                    kind = "tool_call"
                }
            }
        }
    }
};

using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
for (var position = 0; position < records.Count; position++)
{
    var observation = new
    {
        contractVersion = 1,
        sourceSessionId = sessionId,
        sourcePosition = position,
        locator = new
        {
            kind = "byte_range",
            byteOffset = records[position].ByteOffset,
            byteLength = records[position].ByteLength,
            sourceContentSha256 = records[position].SourceContentSha256
        },
        sourceTimestamp = SourceTimestamp(records[position].Record),
        source = new
        {
            harness = "codex",
            harnessVersion = "synthetic",
            recordType = records[position].Record.GetProperty("type").GetString()
        },
        adapter = new { name = "codex-synthetic-jsonl", version = "1" },
        sourcePayload = records[position].Record,
        events = new[] { events[position] }
    };
    var response = await client.PostAsJsonAsync($"{endpoint}/capture/v1/observations", observation);
    string responseText = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine(
            $"Synthetic capture failed at source position {position} " +
            $"with HTTP {(int)response.StatusCode}: {responseText}");
        return 1;
    }

    Console.WriteLine(responseText);
}

Console.Error.WriteLine(
    "LIMITATION: disabled non-production synthetic Codex fixture tracer; " +
    "not a live adapter, scheduler, hook, or supported capture product.");
return 0;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");

static object SourceTimestamp(JsonElement record)
{
    string raw = record.GetProperty("timestamp").GetString()
        ?? throw new InvalidOperationException("Synthetic rollout timestamp is required.");
    DateTimeOffset? parsed = DateTimeOffset.TryParse(
        raw,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind,
        out var value)
        ? value
        : null;
    return new { raw, parsed };
}
