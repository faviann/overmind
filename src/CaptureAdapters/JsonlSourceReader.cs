using System.Security.Cryptography;
using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

public static class JsonlSourceReader
{
    public static async Task<IReadOnlyList<TrustedSourceObservation>> ReadAsync(
        string fixturePath,
        string sourceSessionId,
        bool terminalAtEndOfFile,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(fixturePath, cancellationToken);
        return Read(bytes, sourceSessionId, terminalAtEndOfFile);
    }

    public static IReadOnlyList<TrustedSourceObservation> Read(
        ReadOnlyMemory<byte> sourceBytes,
        string sourceSessionId,
        bool terminalAtEndOfFile)
    {
        ReadOnlySpan<byte> bytes = sourceBytes.Span;
        var observations = new List<TrustedSourceObservation>();
        int lineStart = 0;
        for (int index = 0; index <= bytes.Length; index++)
        {
            if (index != bytes.Length && bytes[index] != (byte)'\n')
            {
                continue;
            }

            bool atEnd = index == bytes.Length;
            int separatorLength = atEnd
                ? 0
                : index > lineStart && bytes[index - 1] == (byte)'\r' ? 2 : 1;
            int contentLength = index - lineStart - (separatorLength == 2 ? 1 : 0);
            int recordLength = contentLength + separatorLength;
            if (contentLength > 0)
            {
                JsonElement payload;
                try
                {
                    payload = JsonDocument.Parse(
                            sourceBytes.Slice(lineStart, contentLength))
                        .RootElement.Clone();
                }
                catch (JsonException)
                {
                    payload = JsonAdapterHelpers.Json(new
                    {
                        type = "malformed",
                        opaqueText = System.Text.Encoding.UTF8.GetString(
                            sourceBytes.Span.Slice(lineStart, contentLength))
                    });
                }

                string digest = Convert.ToHexString(
                        SHA256.HashData(bytes.Slice(lineStart, recordLength)))
                    .ToLowerInvariant();
                observations.Add(new TrustedSourceObservation(
                    sourceSessionId,
                    observations.Count,
                    new CaptureSourceLocator.ByteRange(lineStart, recordLength, digest),
                    CaptureSourceMaterialKind.PersistedRecord,
                    payload,
                    IsTerminal: !atEnd || terminalAtEndOfFile));
            }

            lineStart = index + 1;
        }

        return observations;
    }
}
