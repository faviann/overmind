using System.Security.Cryptography;
using System.Text;
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
        bool terminalAtEndOfFile) =>
        Read(
            sourceBytes,
            new CaptureSourceIdentity(sourceSessionId),
            terminalAtEndOfFile);

    public static IReadOnlyList<TrustedSourceObservation> Read(
        ReadOnlyMemory<byte> sourceBytes,
        CaptureSourceIdentity sourceIdentity,
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
                long sourcePosition = observations.Count;
                string digest = Convert.ToHexString(
                        SHA256.HashData(bytes.Slice(lineStart, recordLength)))
                    .ToLowerInvariant();
                var locator = new CaptureSourceLocator.ByteRange(
                    lineStart, recordLength, digest);
                JsonElement payload;
                CaptureSourceRecordInterpretation interpretation;
                try
                {
                    payload = JsonDocument.Parse(
                            sourceBytes.Slice(lineStart, contentLength))
                        .RootElement.Clone();
                    interpretation = CaptureSourceRecordInterpretation.Structured;
                }
                catch (JsonException)
                {
                    try
                    {
                        string opaqueText = new UTF8Encoding(
                                encoderShouldEmitUTF8Identifier: false,
                                throwOnInvalidBytes: true)
                            .GetString(bytes.Slice(lineStart, contentLength));
                        payload = JsonAdapterHelpers.Json(new
                        {
                            opaqueText,
                            parseError = new
                            {
                                reason = CaptureFidelityPolicy.MalformedJsonReason,
                                policyVersion = CaptureFidelityPolicy.CurrentVersion,
                                sourceIdentity = SourceProvenance(
                                    sourceIdentity, sourcePosition, "byte_range")
                            }
                        });
                        interpretation =
                            CaptureSourceRecordInterpretation.MalformedReadableText;
                    }
                    catch (DecoderFallbackException)
                    {
                        payload = JsonAdapterHelpers.Json(new
                        {
                            omission = new
                            {
                                reason =
                                    CaptureFidelityPolicy.UninspectableSourceRecordReason,
                                originalByteCount = contentLength,
                                policyVersion = CaptureFidelityPolicy.CurrentVersion,
                                contentPolicy = CaptureFidelityPolicy.InvalidUtf8ContentPolicy,
                                sourceIdentity = SourceProvenance(
                                    sourceIdentity, sourcePosition, "byte_range")
                            }
                        });
                        interpretation = CaptureSourceRecordInterpretation.Uninspectable;
                    }
                }

                observations.Add(new TrustedSourceObservation(
                    sourceIdentity,
                    sourcePosition,
                    locator,
                    CaptureSourceMaterialKind.PersistedRecord,
                    payload,
                    IsTerminal: !atEnd || terminalAtEndOfFile,
                    interpretation));
            }

            lineStart = index + 1;
        }

        return observations;
    }

    private static object SourceProvenance(
        CaptureSourceIdentity sourceIdentity,
        long sourcePosition,
        string locatorKind) =>
        new
        {
            externalSessionId = sourceIdentity.ExternalSessionId,
            childId = sourceIdentity.ChildId,
            sourcePosition,
            locatorKind
        };
}
