using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureInstructionTests : HttpSeamTestBase
{
    [Fact]
    public async Task OperatorInstructionIsBindingScopedAndAcknowledgementIsReplaySafe()
    {
        string firstCredential = $"mcap_{Guid.NewGuid():N}";
        string secondCredential = $"mcap_{Guid.NewGuid():N}";
        string firstBinding = $"instruction-first-{Guid.NewGuid():N}";
        string secondBinding = $"instruction-second-{Guid.NewGuid():N}";
        await EnrollAsync(firstBinding, firstCredential);
        await EnrollAsync(secondBinding, secondCredential);

        string output = await RunMemCtlAsync(
            "capture", "instruct", firstBinding, "scan", "--by", "operator:test");
        Guid instructionId = Guid.Parse(output.Split("instruction ")[1].Split(' ')[0]);

        using var first = CaptureClient(firstCredential);
        JsonElement pending = await first.GetFromJsonAsync<JsonElement>(
            "/capture/v1/instructions");
        JsonElement instruction = Assert.Single(pending.GetProperty("instructions").EnumerateArray());
        Assert.Equal(instructionId, instruction.GetProperty("instructionId").GetGuid());
        Assert.Equal("scan", instruction.GetProperty("operation").GetString());

        using var second = CaptureClient(secondCredential);
        JsonElement unrelated = await second.GetFromJsonAsync<JsonElement>(
            "/capture/v1/instructions");
        Assert.Empty(unrelated.GetProperty("instructions").EnumerateArray());

        using HttpResponseMessage acknowledged = await first.PostAsync(
            $"/capture/v1/instructions/{instructionId}/acknowledge", content: null);
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        using HttpResponseMessage replayed = await first.PostAsync(
            $"/capture/v1/instructions/{instructionId}/acknowledge", content: null);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(
            (await acknowledged.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("acknowledgedAt").GetDateTimeOffset(),
            (await replayed.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("acknowledgedAt").GetDateTimeOffset());
        Assert.Empty((await first.GetFromJsonAsync<JsonElement>("/capture/v1/instructions"))
            .GetProperty("instructions").EnumerateArray());

        using HttpResponseMessage crossBinding = await second.PostAsync(
            $"/capture/v1/instructions/{instructionId}/acknowledge",
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, crossBinding.StatusCode);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("/var/lib/private")]
    [InlineData("reset-terminal")]
    [InlineData("replace-credential")]
    public async Task OperatorCannotCreateOperationsOutsideClosedVocabulary(string operation)
    {
        string credential = $"mcap_{Guid.NewGuid():N}";
        string binding = $"closed-instruction-{Guid.NewGuid():N}";
        await EnrollAsync(binding, credential);

        var result = await RunMemCtlForResultAsync(
            null, "capture", "instruct", binding, operation, "--by", "operator:test");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("scan, retry, pause, or resume", result.Stderr);
    }

    [Fact]
    public async Task InvalidCredentialIsRejectedBeforeAcknowledgementBodyIsRead()
    {
        using var client = CaptureClient($"mcap_{Guid.NewGuid():N}");
        using HttpResponseMessage response = await client.PostAsync(
            $"/capture/v1/instructions/{Guid.NewGuid()}/acknowledge",
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OwningCredentialCannotAcknowledgeWithDiagnosticBody()
    {
        string credential = $"mcap_{Guid.NewGuid():N}";
        string binding = $"bodyless-instruction-{Guid.NewGuid():N}";
        await EnrollAsync(binding, credential);
        string output = await RunMemCtlAsync(
            "capture", "instruct", binding, "retry", "--by", "operator:test");
        Guid instructionId = Guid.Parse(output.Split("instruction ")[1].Split(' ')[0]);

        using var client = CaptureClient(credential);
        using HttpResponseMessage rejected = await client.PostAsync(
            $"/capture/v1/instructions/{instructionId}/acknowledge",
            new StringContent("private diagnostic", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(await rejected.Content.ReadAsByteArrayAsync());
        JsonElement pending = await client.GetFromJsonAsync<JsonElement>(
            "/capture/v1/instructions");
        Assert.Equal(
            instructionId,
            Assert.Single(pending.GetProperty("instructions").EnumerateArray())
                .GetProperty("instructionId").GetGuid());
    }

    [Fact]
    public async Task OwningCredentialCannotAcknowledgeWithChunkedBody()
    {
        string credential = $"mcap_{Guid.NewGuid():N}";
        string binding = $"chunked-instruction-{Guid.NewGuid():N}";
        await EnrollAsync(binding, credential);
        string output = await RunMemCtlAsync(
            "capture", "instruct", binding, "scan", "--by", "operator:test");
        Guid instructionId = Guid.Parse(output.Split("instruction ")[1].Split(' ')[0]);

        using var client = CaptureClient(credential);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/capture/v1/instructions/{instructionId}/acknowledge")
        {
            Content = new UnknownLengthContent("private chunk")
        };
        request.Headers.TransferEncodingChunked = true;
        using HttpResponseMessage rejected = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        JsonElement pending = await client.GetFromJsonAsync<JsonElement>(
            "/capture/v1/instructions");
        Assert.Single(pending.GetProperty("instructions").EnumerateArray());
    }

    private async Task EnrollAsync(string stableName, string credential)
    {
        string credentialPath;
        await using (var credentialFile = await PrivateCredentialFile.CreateAsync(credential))
        {
            credentialPath = credentialFile.Path;
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(credentialPath));
            }
            await RunMemCtlAsync(
                "capture", "enroll", stableName,
                "--harness", "codex",
                "--agent-id", $"capture:{stableName}",
                "--credential-file", credentialPath);
        }
        Assert.False(File.Exists(credentialPath));
    }

    private HttpClient CaptureClient(string credential)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private sealed class UnknownLengthContent(string content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(content)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class PrivateCredentialFile : IAsyncDisposable
    {
        private PrivateCredentialFile(string path) => Path = path;

        public string Path { get; }

        public static async Task<PrivateCredentialFile> CreateAsync(string credential)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
            try
            {
                var options = new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                await using var stream = new FileStream(path, options);
                await using var writer = new StreamWriter(stream, leaveOpen: false);
                await writer.WriteAsync(credential);
                await writer.FlushAsync();
                return new PrivateCredentialFile(path);
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(Path);
            return ValueTask.CompletedTask;
        }
    }

}
