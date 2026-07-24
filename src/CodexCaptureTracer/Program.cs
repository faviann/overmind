using System.Net.Http.Headers;
using System.Net.Http.Json;
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
var records = new List<JsonElement>();
foreach (string line in await File.ReadAllLinesAsync(fixturePath))
{
    if (!string.IsNullOrWhiteSpace(line))
    {
        records.Add(JsonDocument.Parse(line).RootElement.Clone());
    }
}

if (records.Count != 3)
{
    throw new InvalidOperationException("Synthetic Codex fixture must contain exactly three JSONL records.");
}

string sessionId = records[0].GetProperty("session_id").GetString()
    ?? throw new InvalidOperationException("Fixture session_id is required.");
var events = new object[]
{
    new
    {
        partKey = "message/0",
        partOrder = 0,
        kind = "message",
        actor = "user",
        payload = new { text = records[0].GetProperty("text").GetString() }
    },
    new
    {
        partKey = "tool/1",
        partOrder = 1,
        kind = "tool_call",
        actor = "assistant",
        payload = new
        {
            callId = records[1].GetProperty("call_id").GetString(),
            tool = records[1].GetProperty("name").GetString(),
            arguments = records[1].GetProperty("arguments")
        }
    },
    new
    {
        partKey = "tool/2",
        partOrder = 2,
        kind = "tool_result",
        actor = "tool",
        payload = new
        {
            callId = records[2].GetProperty("call_id").GetString(),
            outcome = "succeeded",
            output = records[2].GetProperty("output").GetString()
        },
        relationships = new[]
        {
            new
            {
                type = "result_for",
                targetNativeId = records[2].GetProperty("call_id").GetString(),
                targetKind = "tool_call"
            }
        }
    }
};
var observation = new
{
    contractVersion = 1,
    sourceSessionId = sessionId,
    sourceLocator = $"synthetic-jsonl:1-3",
    source = new { harness = "codex", harnessVersion = "synthetic", recordType = "fixture_exchange" },
    adapter = new { name = "codex-synthetic-jsonl", version = "1" },
    sourcePayload = new { records },
    events
};

using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
var response = await client.PostAsJsonAsync($"{endpoint}/capture/v1/observations", observation);
string responseText = await response.Content.ReadAsStringAsync();
if (!response.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Synthetic capture failed with HTTP {(int)response.StatusCode}: {responseText}");
    return 1;
}

Console.WriteLine(responseText);
Console.Error.WriteLine(
    "LIMITATION: disabled non-production synthetic Codex fixture tracer; " +
    "not a live adapter, scheduler, hook, or supported capture product.");
return 0;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");
