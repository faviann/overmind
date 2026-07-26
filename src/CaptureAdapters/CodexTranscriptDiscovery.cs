using System.Security.Cryptography;
using System.Text;

namespace CaptureAdapters;

public sealed record CodexTranscriptStream(
    string Path,
    string SourceStream,
    bool TerminalAtEndOfFile = false,
    string? TranscriptIdentity = null);

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

        CodexTranscriptStream[] streams = paths
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Describe(fullLocation, path))
            .ToArray();
        if (streams
            .GroupBy(stream => stream.TranscriptIdentity, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Configured Codex transcript discovery contains ambiguous duplicate " +
                "logical identities.");
        }
        return streams;
    }

    private static CodexTranscriptStream Describe(string configuredLocation, string path)
    {
        bool terminalAtEndOfFile = false;
        string identityPath = path;
        if (Directory.Exists(configuredLocation))
        {
            string relative = Path.GetRelativePath(configuredLocation, path);
            string[] parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            bool activeSession = parts.Length > 1
                && string.Equals(parts[0], "sessions", StringComparison.Ordinal);
            bool archivedSession = parts.Length == 2
                && string.Equals(
                    parts[0], "archived_sessions", StringComparison.Ordinal);
            if (activeSession || archivedSession)
            {
                terminalAtEndOfFile = archivedSession;
                identityPath = string.Join(
                    "\n",
                    "codex-session-basename/v1",
                    configuredLocation,
                    Path.GetFileName(path));
            }
        }

        string digest = Digest(identityPath);
        return new CodexTranscriptStream(
            path,
            $"codex-synthetic-{digest[..24]}",
            terminalAtEndOfFile,
            digest);
    }

    private static string Digest(string identityPath)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)))
            .ToLowerInvariant();
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
