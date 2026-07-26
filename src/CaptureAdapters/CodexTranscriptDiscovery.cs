using System.Security.Cryptography;
using System.Text;

namespace CaptureAdapters;

public sealed record CodexTranscriptStream(string Path, string SourceStream);

/// <summary>
/// Enumerates the configured synthetic Codex transcript location afresh for
/// every scan cycle. Stream identity is stable for an absolute source path and
/// does not depend on enumeration order.
/// </summary>
public static class CodexTranscriptDiscovery
{
    public static IReadOnlyList<CodexTranscriptStream> Enumerate(string configuredLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredLocation);
        string fullLocation = Path.GetFullPath(configuredLocation);
        IEnumerable<string> paths;
        if (File.Exists(fullLocation))
        {
            paths = [fullLocation];
        }
        else if (Directory.Exists(fullLocation))
        {
            paths = Directory.EnumerateFiles(
                fullLocation, "*.jsonl", SearchOption.AllDirectories);
        }
        else
        {
            throw new DirectoryNotFoundException(
                $"Configured Codex transcript location '{fullLocation}' does not exist.");
        }

        return paths
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new CodexTranscriptStream(path, SourceStreamFor(path)))
            .ToArray();
    }

    private static string SourceStreamFor(string path)
    {
        string digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant();
        return $"codex-synthetic-{digest[..24]}";
    }
}

/// <summary>
/// Owns one enumerated scan cycle's per-stream filesystem isolation. A source
/// may disappear or become unreadable after enumeration without preventing
/// later streams in this cycle or the scheduler's next cycle.
/// </summary>
public static class CodexTranscriptScanCycle
{
    public static async Task RunAsync(
        IReadOnlyList<CodexTranscriptStream> streams,
        Func<CodexTranscriptStream, CancellationToken, Task> scanStream,
        Action<Exception> reportFailure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(scanStream);
        ArgumentNullException.ThrowIfNull(reportFailure);

        foreach (CodexTranscriptStream stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await scanStream(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reportFailure(ex);
            }
        }
    }
}
