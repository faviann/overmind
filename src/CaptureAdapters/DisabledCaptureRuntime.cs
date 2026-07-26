using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CaptureAdapters;

/// <summary>
/// Harness-neutral execution seam for the explicitly disabled fixture tracer.
/// It owns fixture positions and HTTP delivery, while an injected adapter owns
/// only source interpretation.
/// </summary>
public static class DisabledCaptureRuntime
{
    public static async Task<IReadOnlyList<string>> RunFixtureAsync(
        ICaptureSourceAdapter adapter,
        string fixturePath,
        string sourceSessionId,
        Uri captureEndpoint,
        string credential,
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
            using var response = await client.PostAsJsonAsync(
                new Uri(captureEndpoint, "/capture/v1/observations"),
                terminal.Observation,
                JsonDefaults.Options,
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
