using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

public sealed record CodexTranscriptStream(
    string Path,
    string SourceStream,
    bool TerminalAtEndOfFile = false,
    string? TranscriptIdentity = null,
    CaptureSourceIdentity? SourceIdentity = null,
    Exception? IdentityFailure = null);

/// <summary>
/// Enumerates the configured synthetic Codex transcript location afresh for
/// every scan cycle. Stream identity is stable for an absolute source path and
/// does not depend on enumeration order.
/// </summary>
public static class CodexTranscriptDiscovery
{
    public static IReadOnlyList<CodexTranscriptStream>
        EnumerateCurrentSessionsAndResponsibleArchives(
            string sessionsRoot,
            string archiveRoot,
            IReadOnlyDictionary<string, string> responsibleSourceStreamsByTranscriptIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        ArgumentNullException.ThrowIfNull(responsibleSourceStreamsByTranscriptIdentity);
        string fullSessionsRoot = Path.GetFullPath(sessionsRoot);
        string fullArchiveRoot = Path.GetFullPath(archiveRoot);
        if (!Directory.Exists(fullSessionsRoot))
        {
            throw new DirectoryNotFoundException("Configured Codex sessions root is unavailable.");
        }
        if (!Directory.Exists(fullArchiveRoot))
        {
            throw new DirectoryNotFoundException("Configured Codex archive root is unavailable.");
        }

        CodexTranscriptStream[] current = Directory.EnumerateFiles(
                fullSessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => DescribeProduction(path, terminalAtEndOfFile: false))
            .Where(stream => stream.TranscriptIdentity is null
                || !responsibleSourceStreamsByTranscriptIdentity.TryGetValue(
                    stream.TranscriptIdentity, out string? responsibleSourceStream)
                || string.Equals(
                    stream.SourceStream, responsibleSourceStream, StringComparison.Ordinal))
            .ToArray();
        CodexTranscriptStream[] responsibleArchives = Directory.EnumerateFiles(
                fullArchiveRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new
            {
                Path = path,
                TranscriptIdentity = ProductionTranscriptIdentity(path)
            })
            .Where(candidate => responsibleSourceStreamsByTranscriptIdentity.ContainsKey(
                candidate.TranscriptIdentity))
            .Select(candidate => DescribeProduction(
                candidate.Path, terminalAtEndOfFile: true))
            .Where(stream => stream.TranscriptIdentity is not null
                && responsibleSourceStreamsByTranscriptIdentity.TryGetValue(
                    stream.TranscriptIdentity, out string? responsibleSourceStream)
                && string.Equals(
                    stream.SourceStream, responsibleSourceStream, StringComparison.Ordinal))
            .ToArray();
        CodexTranscriptStream[] streams = [.. current, .. responsibleArchives];
        ThrowIfAmbiguous(streams);
        return streams;
    }

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

        return DescribeAll(fullLocation, paths);
    }

    private static IReadOnlyList<CodexTranscriptStream> DescribeAll(
        string configuredLocation,
        IEnumerable<string> paths)
    {
        CodexTranscriptStream[] streams = paths
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Describe(configuredLocation, path))
            .ToArray();
        ThrowIfAmbiguous(streams);
        return streams;
    }

    private static CodexTranscriptStream DescribeProduction(
        string path,
        bool terminalAtEndOfFile)
    {
        string identityMaterial = ProductionIdentityMaterial(path);
        string transcriptIdentity = Digest(identityMaterial);
        CodexTranscriptStream described =
            Describe(path, identityMaterial, terminalAtEndOfFile);
        return described.IdentityFailure is null
            ? described with { TranscriptIdentity = transcriptIdentity }
            : described;
    }

    private static string ProductionTranscriptIdentity(string path) =>
        Digest(ProductionIdentityMaterial(path));

    private static string ProductionIdentityMaterial(string path) =>
        string.Join(
            "\n",
            "codex-production-session-basename/v1",
            Path.GetFileName(path));

    private static void ThrowIfAmbiguous(IReadOnlyList<CodexTranscriptStream> streams)
    {
        if (streams
            .Where(stream => stream.TranscriptIdentity is not null)
            .GroupBy(stream => stream.TranscriptIdentity, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Configured Codex transcript discovery contains ambiguous duplicate " +
                "logical identities.");
        }
        if (streams
            .Where(stream => !string.IsNullOrWhiteSpace(stream.SourceStream)
                && stream.IdentityFailure is null)
            .GroupBy(stream => stream.SourceStream, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Configured Codex transcript discovery contains ambiguous duplicate " +
                "source streams.");
        }
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

        return Describe(path, identityPath, terminalAtEndOfFile);
    }

    private static CodexTranscriptStream Describe(
        string path,
        string identityPath,
        bool terminalAtEndOfFile)
    {
        CaptureSourceIdentity? sourceIdentity;
        try
        {
            sourceIdentity = ReadSourceIdentity(path);
        }
        catch (Exception ex)
            when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // One unreadable or self-contradictory rollout is that stream's
            // failure, carried to its own scan rather than guessing an identity
            // or cancelling the whole cycle.
            return new CodexTranscriptStream(path, UnresolvedSourceStream, IdentityFailure: ex);
        }

        string digest = sourceIdentity is null
            ? Digest(identityPath)
            : Digest(JsonSerializer.Serialize(new
            {
                version = "codex-source-identity/v1",
                sourceIdentity.ExternalSessionId,
                sourceIdentity.ChildId
            }));
        return new CodexTranscriptStream(
            path,
            $"codex-synthetic-{digest[..24]}",
            terminalAtEndOfFile,
            digest,
            sourceIdentity);
    }

    private const string UnresolvedSourceStream = "codex-synthetic-unresolved";

    private static CaptureSourceIdentity? ReadSourceIdentity(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement record;
            try
            {
                record = JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            if (record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("type", out JsonElement type)
                || !string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal)
                || !record.TryGetProperty("payload", out JsonElement payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? threadId = NullableString(payload, "id");
            string? externalSessionId = NullableString(payload, "session_id") ?? threadId;
            if (string.IsNullOrWhiteSpace(externalSessionId))
            {
                throw new InvalidDataException(
                    $"Codex session metadata in '{path}' has no external session identity.");
            }

            bool? sourceClass = SourceClass(payload);
            bool? threadClass = ThreadClass(payload);
            if (sourceClass is not null && threadClass is not null
                && sourceClass != threadClass)
            {
                throw new InvalidDataException(
                    $"Codex session metadata in '{path}' has contradictory child classification.");
            }
            bool isChild = sourceClass == true || threadClass == true;
            if (isChild && string.IsNullOrWhiteSpace(threadId))
            {
                throw new InvalidDataException(
                    $"Codex child session metadata in '{path}' has no observed thread identity.");
            }
            return new CaptureSourceIdentity(externalSessionId, isChild ? threadId : null);
        }
        return null;
    }

    private static bool? SourceClass(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out JsonElement source)
            || source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (source.ValueKind == JsonValueKind.String)
        {
            return string.Equals(source.GetString(), "subagent", StringComparison.OrdinalIgnoreCase);
        }
        if (source.ValueKind == JsonValueKind.Object)
        {
            if (source.TryGetProperty("subagent", out _)
                || source.TryGetProperty("sub_agent", out _))
            {
                return true;
            }
            if (source.TryGetProperty("internal", out _)
                || source.TryGetProperty("custom", out _))
            {
                return false;
            }
            return null;
        }
        return null;
    }

    private static bool? ThreadClass(JsonElement payload)
    {
        if (!payload.TryGetProperty("thread_source", out JsonElement source)
            || source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        return source.ValueKind == JsonValueKind.String
            ? string.Equals(source.GetString(), "subagent", StringComparison.OrdinalIgnoreCase)
            : null;
    }

    private static string? NullableString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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
            if (stream.IdentityFailure is not null)
            {
                reportFailure(stream.IdentityFailure);
                continue;
            }

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
