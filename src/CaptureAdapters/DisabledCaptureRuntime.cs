using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

/// <summary>
/// Harness-neutral execution seam for the explicitly disabled fixture tracer.
/// It owns fixture positions, the local safety gate, and HTTP delivery, while
/// an injected adapter owns only source interpretation.
///
/// The runtime crosses the SAME governed gate the server crosses, before it
/// emits anything. There is no local durable queue in this slice, so "before
/// durable local persistence" means "before the observation leaves this
/// process"; the server then scans independently before canonical append.
/// </summary>
public static class DisabledCaptureRuntime
{
    public static async Task<IReadOnlyList<string>> RunFixtureAsync(
        ICaptureSourceAdapter adapter,
        string fixturePath,
        string sourceSessionId,
        Uri captureEndpoint,
        string credential,
        NeverStoreGate safetyGate,
        CancellationToken cancellationToken = default)
    {
        var sourceRecords = await JsonlSourceReader.ReadAsync(
            fixturePath, sourceSessionId, terminalAtEndOfFile: true, cancellationToken);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        var receipts = new List<string>();

        foreach (var sourceRecord in sourceRecords)
        {
            var outcome = adapter.Adapt(sourceRecord);
            if (outcome is CaptureSourcePositionOutcome.Incomplete)
            {
                break;
            }

            var terminal = (CaptureSourcePositionOutcome.Terminal)outcome;
            string observationJson = JsonSerializer.Serialize(
                terminal.Observation, JsonDefaults.Options);
            safetyGate.AssertObservationWithinBudget(observationJson);
            string safeObservationJson = safetyGate.RedactJson(observationJson);
            using var content = new StringContent(
                safeObservationJson, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(
                new Uri(captureEndpoint, "/capture/v1/observations"),
                content,
                cancellationToken);
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new CaptureDeliveryException(
                    terminal.SourcePosition, response.StatusCode, responseText);
            }
            receipts.Add(responseText);
        }

        return receipts;
    }
}

public sealed class CaptureDeliveryException(
    long sourcePosition,
    HttpStatusCode statusCode,
    string responseBody)
    : Exception(
        $"Capture failed at source position {sourcePosition} " +
        $"with HTTP {(int)statusCode}: {responseBody}")
{
    public long SourcePosition { get; } = sourcePosition;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
