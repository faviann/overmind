using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    JsonElement Payload,
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
    int lineLength = index - lineStart;
    if (lineLength > 0 && fixtureBytes[index - 1] == (byte)'\r')
    {
        lineLength--;
    }
    if (lineLength > 0)
    {
        string line = Encoding.UTF8.GetString(fixtureBytes, lineStart, lineLength);
        records.Add((
            JsonDocument.Parse(line).RootElement.Clone(),
            lineStart,
            lineLength,
            Convert.ToHexString(
                SHA256.HashData(fixtureBytes.AsSpan(lineStart, lineLength)))
                .ToLowerInvariant()));
    }
    lineStart = index + 1;
}

if (records.Count != 3)
{
    throw new InvalidOperationException("Synthetic Codex fixture must contain exactly three JSONL records.");
}
if (records[0].Payload.GetProperty("type").GetString() != "response_item"
    || records[0].Payload.GetProperty("item_type").GetString() != "message"
    || records[0].Payload.GetProperty("role").GetString() != "user")
{
    throw new InvalidOperationException(
        "Synthetic Codex message must be a model-facing response_item/message with role user.");
}

string sessionId = records[0].Payload.GetProperty("session_id").GetString()
    ?? throw new InvalidOperationException("Fixture session_id is required.");
var events = new object[]
{
    new
    {
        partKey = "message/0",
        partOrder = 0,
        kind = "message",
        actor = "user",
        payload = new { text = records[0].Payload.GetProperty("text").GetString() }
    },
    new
    {
        partKey = "tool/1",
        partOrder = 0,
        kind = "tool_call",
        actor = "assistant",
        payload = new
        {
            callId = records[1].Payload.GetProperty("call_id").GetString(),
            tool = records[1].Payload.GetProperty("name").GetString(),
            arguments = records[1].Payload.GetProperty("arguments")
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
            callId = records[2].Payload.GetProperty("call_id").GetString(),
            outcome = "succeeded",
            output = records[2].Payload.GetProperty("output").GetString()
        },
        relationships = new[]
        {
            new
            {
                type = "result_for",
                target = new
                {
                    nativeId = records[2].Payload.GetProperty("call_id").GetString(),
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
        source = new
        {
            harness = "codex",
            harnessVersion = "synthetic",
            recordType = records[position].Payload.GetProperty("type").GetString()
        },
        adapter = new { name = "codex-synthetic-jsonl", version = "1" },
        sourcePayload = records[position].Payload,
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
