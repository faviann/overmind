using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MemSrv.Core;
using MemSrv.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureTests : HttpSeamTestBase
{
    [Fact]
    public async Task UnknownCredentialIsRejectedBeforeMalformedBodyOrMissingScannerConfiguration()
    {
        var options = RuntimeOptions();
        options.NeverStorePath = Path.Combine(
            Path.GetTempPath(), $"missing-never-store-{Guid.NewGuid():N}.yaml");
        await using var app = HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", $"unknown-capture-{Guid.NewGuid():N}");

            var response = await client.PostAsync(
                "/capture/v1/observations",
                new StringContent("not-json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task OperatorEnrollsRestrictedCodexCaptureAndReadsFallbackReceipt()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        var credentialPath = Path.Combine(Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(credentialPath, captureKey);
        try
        {
            var enrollment = await RunMemCtlAsync(
                "capture", "enroll", "codex-synthetic",
                "--harness", "codex",
                "--agent-id", "capture:codex-synthetic",
                "--credential-file", credentialPath);
            Assert.Contains("non-production", enrollment);

            using var agentOnCapture = CaptureClient(AgentAKey);
            var rejectedAgent = await agentOnCapture.PostAsync(
                "/capture/v1/observations",
                new StringContent("not-json", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedAgent.StatusCode);

            using var unknownCapture = CaptureClient($"unknown-capture-{Guid.NewGuid():N}");
            var rejectedUnknown = await unknownCapture.PostAsync(
                "/capture/v1/observations",
                new StringContent("not-json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedUnknown.StatusCode);

            using var captureOnMcp = CaptureClient(captureKey);
            var rejectedCapture = await captureOnMcp.PostAsync("/mcp", JsonContent.Create(new { }));
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedCapture.StatusCode);

            var accepted = await captureOnMcp.PostAsJsonAsync(
                "/capture/v1/observations", Observation(sourceSessionId, 0, "record-1", "hello"));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("new", receipt.GetProperty("status").GetString());
            Assert.Equal("capture/unscoped", receipt.GetProperty("effectiveNamespace").GetString());
            Assert.Equal("fallback", receipt.GetProperty("routeBasis").GetString());
            Assert.Equal(3, receipt.GetProperty("events").GetArrayLength());

            var observationUuid = receipt.GetProperty("observationUuid").GetGuid();
            var shown = await RunMemCtlAsync("capture", "receipt", observationUuid.ToString());
            var envelopes = shown.Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.Equal(3, envelopes.Length);
            Assert.All(envelopes, envelope =>
            {
                Assert.Equal(
                    ["contractVersion", "observation", "event", "relationships"],
                    envelope.EnumerateObject().Select(property => property.Name));
                Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
                Assert.Equal(
                    observationUuid,
                    envelope.GetProperty("observation").GetProperty("observationUuid").GetGuid());
                Assert.Equal(
                    [
                        "observationUuid", "sourceStreamUuid", "source", "locator",
                        "sourceTimestamp", "adapter", "safeSourcePayload", "scan", "capturedAt"
                    ],
                    envelope.GetProperty("observation")
                        .EnumerateObject().Select(property => property.Name));
                Assert.Equal(
                    [
                        "traceUuid", "sessionId", "agentId", "namespace", "partKey",
                        "partOrder", "kind", "actor", "occurredAt", "payloadVersion", "payload"
                    ],
                    envelope.GetProperty("event")
                        .EnumerateObject().Select(property => property.Name));
            });
            Assert.Equal(
                ["message", "tool_call", "tool_result"],
                envelopes.Select(envelope => envelope.GetProperty("event").GetProperty("kind").GetString()));
            var relationship = Assert.Single(
                envelopes[2].GetProperty("relationships").EnumerateArray());
            Assert.Equal("result_for", relationship.GetProperty("type").GetString());
            Assert.Equal(
                "call-1",
                relationship.GetProperty("target").GetProperty("nativeId").GetString());
            Assert.Equal(
                "tool_call",
                relationship.GetProperty("target").GetProperty("kind").GetString());
            Assert.Equal(
                ["sourceStreamUuid", "nativeId", "kind"],
                relationship.GetProperty("target").EnumerateObject().Select(property => property.Name));
        }
        finally
        {
            File.Delete(credentialPath);
        }
    }

    [Theory]
    [InlineData(AgentAKey)]
    [InlineData("mcap_short")]
    [InlineData("mcap_invalid!")]
    public async Task InvalidCaptureFormCannotBeEnrolledAsCaptureCredential(
        string invalidCredential)
    {
        var credentialPath = Path.Combine(Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(credentialPath, invalidCredential);
        try
        {
            var result = await RunMemCtlForResultAsync(
                null,
                "capture", "enroll", $"agent-key-rejected-{Guid.NewGuid():N}",
                "--harness", "codex",
                "--agent-id", "capture:rejected",
                "--credential-file", credentialPath);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("mcap_", result.Stderr);
        }
        finally
        {
            File.Delete(credentialPath);
        }
    }

    [Fact]
    public async Task CaptureBindingIdentityMustCrossNeverStoreBeforeEnrollment()
    {
        string seededSyntheticSecret = "AKIA" + "SYNTHETICFIXTURE";
        foreach (bool secretInStableName in new[] { true, false })
        {
            string captureKey = CaptureCredential();
            string credentialPath = Path.Combine(
                Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(credentialPath, captureKey);
            try
            {
                string stableName = secretInStableName
                    ? seededSyntheticSecret
                    : $"safe-binding-{Guid.NewGuid():N}";
                string agentId = secretInStableName
                    ? $"capture:safe-{Guid.NewGuid():N}"
                    : seededSyntheticSecret;
                var rejected = await RunMemCtlForResultAsync(
                    null,
                    "capture", "enroll", stableName,
                    "--harness", "codex",
                    "--agent-id", agentId,
                    "--credential-file", credentialPath);
                Assert.NotEqual(0, rejected.ExitCode);
                Assert.Contains("never-store", rejected.Stderr);
                Assert.DoesNotContain(seededSyntheticSecret, rejected.Stderr);

                var accepted = await RunMemCtlForResultAsync(
                    null,
                    "capture", "enroll", $"safe-binding-{Guid.NewGuid():N}",
                    "--harness", "codex",
                    "--agent-id", $"capture:safe-{Guid.NewGuid():N}",
                    "--credential-file", credentialPath);
                Assert.Equal(0, accepted.ExitCode);
            }
            finally
            {
                File.Delete(credentialPath);
            }
        }
    }

    [Fact]
    public async Task DurableReceiptRemainsReadableWhenScannerConfigurationIsUnavailable()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-readable-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);
        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation(sourceSessionId, 0, $"receipt-{Guid.NewGuid():N}", "durable"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();

        var shown = await RunMemCtlForResultAsync(
            new Dictionary<string, string>
            {
                ["MemSrv__NeverStorePath"] = Path.Combine(
                    Path.GetTempPath(), $"missing-never-store-{Guid.NewGuid():N}.yaml")
            },
            "capture", "receipt",
            receipt.GetProperty("observationUuid").GetGuid().ToString());

        Assert.Equal(0, shown.ExitCode);
        Assert.Equal(3, shown.Stdout.Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task RelationshipTargetStreamScopeRoundTripsWithoutInference()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-relationship-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);
        Guid explicitTargetStream = Guid.NewGuid();

        var omitted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RelationshipObservation(
                sourceSessionId, 0, $"relationship-{Guid.NewGuid():N}", null));
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var omittedReceipt = await omitted.Content.ReadFromJsonAsync<JsonElement>();
        var omittedHttpTarget = Assert.Single(
            Assert.Single(omittedReceipt.GetProperty("events").EnumerateArray())
                .GetProperty("relationships").EnumerateArray()).GetProperty("target");
        Assert.Equal(JsonValueKind.Null, omittedHttpTarget.GetProperty("sourceStreamUuid").ValueKind);
        var omittedEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "receipt",
            omittedReceipt.GetProperty("observationUuid").GetGuid().ToString())).RootElement;
        Assert.Equal(
            JsonValueKind.Null,
            Assert.Single(omittedEnvelope.GetProperty("relationships").EnumerateArray())
                .GetProperty("target").GetProperty("sourceStreamUuid").ValueKind);

        var explicitScope = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RelationshipObservation(
                sourceSessionId, 1, $"relationship-{Guid.NewGuid():N}", explicitTargetStream));
        Assert.Equal(HttpStatusCode.OK, explicitScope.StatusCode);
        var explicitReceipt = await explicitScope.Content.ReadFromJsonAsync<JsonElement>();
        var explicitHttpTarget = Assert.Single(
            Assert.Single(explicitReceipt.GetProperty("events").EnumerateArray())
                .GetProperty("relationships").EnumerateArray()).GetProperty("target");
        Assert.Equal(
            explicitTargetStream,
            explicitHttpTarget.GetProperty("sourceStreamUuid").GetGuid());
        var explicitEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "receipt",
            explicitReceipt.GetProperty("observationUuid").GetGuid().ToString())).RootElement;
        Assert.Equal(
            explicitTargetStream,
            Assert.Single(explicitEnvelope.GetProperty("relationships").EnumerateArray())
                .GetProperty("target").GetProperty("sourceStreamUuid").GetGuid());
    }

    [Fact]
    public async Task TypedLocatorAndSourceTimestampRoundTripWithoutInference()
    {
        var captureKey = CaptureCredential();
        string binding = $"codex-typed-{Guid.NewGuid():N}";
        string sourceSessionId = $"session-{Guid.NewGuid():N}";
        await EnrollAsync(binding, captureKey);
        using var client = CaptureClient(captureKey);
        var request = new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId = $"native-{Guid.NewGuid():N}" },
            sourceTimestamp = new
            {
                raw = "2026-07-14T12:00:00.123456789Z",
                parsed = (DateTimeOffset?)DateTimeOffset.Parse("2026-07-14T12:00:00.123456Z")
            },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { text = "timestamped" },
            events = new[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    occurredAt = (DateTimeOffset?)null,
                    payload = new { text = "timestamped" }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/capture/v1/observations", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        var shown = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString()))
            .RootElement;
        var observation = shown.GetProperty("observation");
        Assert.Equal("native_id", observation.GetProperty("locator").GetProperty("kind").GetString());
        Assert.Equal(
            ["kind", "nativeId"],
            observation.GetProperty("locator").EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            request.locator.nativeId,
            observation.GetProperty("locator").GetProperty("nativeId").GetString());
        Assert.Equal(
            "2026-07-14T12:00:00.123456789Z",
            observation.GetProperty("sourceTimestamp").GetProperty("raw").GetString());
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-14T12:00:00.123456Z"),
            observation.GetProperty("sourceTimestamp").GetProperty("parsed").GetDateTimeOffset());
        Assert.Equal(
            JsonValueKind.Null,
            shown.GetProperty("event").GetProperty("occurredAt").ValueKind);

        var rawOnly = new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = 1,
            locator = new { kind = "native_id", nativeId = $"native-{Guid.NewGuid():N}" },
            sourceTimestamp = new { raw = "source-clock:unknown-format", parsed = (DateTimeOffset?)null },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { text = "raw timestamp only" },
            events = new[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    occurredAt = (DateTimeOffset?)null,
                    payload = new { text = "raw timestamp only" }
                }
            }
        };
        var rawOnlyResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations", rawOnly);
        Assert.Equal(HttpStatusCode.OK, rawOnlyResponse.StatusCode);
        var rawOnlyReceipt = await rawOnlyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rawOnlyEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "receipt",
            rawOnlyReceipt.GetProperty("observationUuid").GetGuid().ToString())).RootElement;
        var returnedTimestamp = rawOnlyEnvelope
            .GetProperty("observation").GetProperty("sourceTimestamp");
        Assert.Equal("source-clock:unknown-format", returnedTimestamp.GetProperty("raw").GetString());
        Assert.Equal(JsonValueKind.Null, returnedTimestamp.GetProperty("parsed").ValueKind);
    }

    [Fact]
    public async Task TypedLocatorAcceptsExactlyItsKindSpecificFields()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-locator-shape-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        object[] invalidLocators =
        [
            new { kind = "native_id" },
            new
            {
                kind = "native_id",
                nativeId = $"native-{Guid.NewGuid():N}",
                byteOffset = 0L
            },
            new { kind = "byte_range", byteOffset = 0L },
            new
            {
                kind = "byte_range",
                nativeId = $"native-{Guid.NewGuid():N}",
                byteOffset = 0L,
                byteLength = 10L
            }
        ];

        foreach (var locator in invalidLocators)
        {
            using var response = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                InvalidLocatorObservation(sourceSessionId, locator));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task RetryIsAlreadyAcceptedAndChangedContentConflictsWithoutMutation()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync("codex-idempotency", captureKey);
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 0, "record-stable", "original"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstReceipt = await first.Content.ReadFromJsonAsync<JsonElement>();

        var second = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 1, "record-second", "second"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var retry = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 1, "record-stable", "original"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryReceipt = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(0, retryReceipt.GetProperty("sourcePosition").GetInt64());
        Assert.Equal(
            firstReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());

        var conflict = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 9, "record-stable", "changed"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var positionCollision = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 1, "different-locator", "original"));
        Assert.Equal(HttpStatusCode.Conflict, positionCollision.StatusCode);

        var shown = await RunMemCtlAsync(
            "capture", "receipt", firstReceipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.Contains("original", shown);
        Assert.DoesNotContain("changed", shown);
    }

    [Fact]
    public async Task DisabledTracerImportsSyntheticCodexMessageAndToolExchange()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync("codex-tracer", captureKey);
        string fixtureSessionId = UniqueSession();
        string fixtureCallId = $"call_{Guid.NewGuid():N}";
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-synthetic-{Guid.NewGuid():N}.jsonl");
        string fixture = await File.ReadAllTextAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        fixture = fixture
            .Replace("codex-synthetic-session", fixtureSessionId, StringComparison.Ordinal)
            .Replace("call_fixture_1", fixtureCallId, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixturePath, fixture, new UTF8Encoding(false));

        try
        {
            var disabled = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>());
            Assert.Equal(2, disabled.ExitCode);
            Assert.Empty(disabled.Stdout);
            Assert.Contains("disabled", disabled.Stderr);

            var enabled = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath
                });
            Assert.Equal(0, enabled.ExitCode);
            var receipts = enabled.Stdout.Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            Assert.Equal(3, receipts.Length);
            Assert.All(receipts, receipt => Assert.Equal("new", receipt.GetProperty("status").GetString()));
            Assert.Equal([0L, 1L, 2L], receipts.Select(receipt => receipt.GetProperty("sourcePosition").GetInt64()));
            Assert.Equal(
                ["message/0", "tool/1", "tool/2"],
                receipts.Select(receipt => Assert.Single(receipt.GetProperty("events").EnumerateArray())
                    .GetProperty("partKey").GetString()));
            Assert.Equal(
                "response_item",
                receipts[0].GetProperty("observation").GetProperty("source")
                    .GetProperty("recordType").GetString());
            var messageObservation = receipts[0].GetProperty("observation");
            Assert.Equal(
                receipts[0].GetProperty("observationUuid").GetGuid(),
                messageObservation.GetProperty("observationUuid").GetGuid());
            Assert.NotEqual(Guid.Empty, messageObservation.GetProperty("sourceStreamUuid").GetGuid());
            byte[] rewrittenFixtureBytes = await File.ReadAllBytesAsync(fixturePath);
            int firstNewline = Array.IndexOf(rewrittenFixtureBytes, (byte)'\n');
            Assert.Equal(
                "byte_range",
                messageObservation.GetProperty("locator").GetProperty("kind").GetString());
            Assert.Equal(
                ["kind", "byteOffset", "byteLength"],
                messageObservation.GetProperty("locator")
                    .EnumerateObject().Select(property => property.Name));
            Assert.Equal(0, messageObservation.GetProperty("locator").GetProperty("byteOffset").GetInt64());
            Assert.Equal(
                firstNewline,
                messageObservation.GetProperty("locator").GetProperty("byteLength").GetInt64());
            Assert.Equal(
                JsonValueKind.Null,
                messageObservation.GetProperty("sourceTimestamp").ValueKind);
            Assert.Equal(
                "synthetic",
                messageObservation.GetProperty("source").GetProperty("harnessVersion").GetString());
            Assert.Equal(
                "codex-synthetic-jsonl",
                messageObservation.GetProperty("adapter").GetProperty("name").GetString());
            Assert.Equal(
                "Show the working directory.",
                messageObservation.GetProperty("safeSourcePayload").GetProperty("text").GetString());
            Assert.Equal(
                "clean",
                messageObservation.GetProperty("scan").GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                messageObservation.GetProperty("scan").GetProperty("ruleSetVersion").GetString()));
            Assert.Equal(0, messageObservation.GetProperty("scan").GetProperty("redactionCount").GetInt32());
            Assert.NotEqual(
                default,
                messageObservation.GetProperty("capturedAt").GetDateTimeOffset());
            var messageEvent = Assert.Single(receipts[0].GetProperty("events").EnumerateArray());
            Assert.NotEqual(Guid.Empty, messageEvent.GetProperty("traceUuid").GetGuid());
            Assert.StartsWith("capture:", messageEvent.GetProperty("sessionId").GetString());
            Assert.Equal("capture:codex-tracer", messageEvent.GetProperty("agentId").GetString());
            Assert.Equal("capture/unscoped", messageEvent.GetProperty("namespace").GetString());
            Assert.Equal(0, messageEvent.GetProperty("partOrder").GetInt32());
            Assert.Equal("message", messageEvent.GetProperty("kind").GetString());
            Assert.Equal("user", messageEvent.GetProperty("actor").GetString());
            Assert.Equal(JsonValueKind.Null, messageEvent.GetProperty("occurredAt").ValueKind);
            Assert.Equal(1, messageEvent.GetProperty("payloadVersion").GetInt32());
            Assert.Equal(
                "Show the working directory.",
                messageEvent.GetProperty("payload").GetProperty("text").GetString());
            Assert.Empty(messageEvent.GetProperty("relationships").EnumerateArray());
            var resultEvent = Assert.Single(receipts[2].GetProperty("events").EnumerateArray());
            var resultRelationship = Assert.Single(
                resultEvent.GetProperty("relationships").EnumerateArray());
            Assert.Equal("result_for", resultRelationship.GetProperty("type").GetString());
            Assert.Equal(
                fixtureCallId,
                resultRelationship.GetProperty("target").GetProperty("nativeId").GetString());
            Assert.Equal(
                "tool_call",
                resultRelationship.GetProperty("target").GetProperty("kind").GetString());

            var messageEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
                "capture", "receipt", receipts[0].GetProperty("observationUuid").GetGuid().ToString()))
                .RootElement;
            Assert.Equal(
                ["contractVersion", "observation", "event", "relationships"],
                messageEnvelope.EnumerateObject().Select(property => property.Name));
            Assert.Equal("message", messageEnvelope.GetProperty("event").GetProperty("kind").GetString());
            Assert.Equal("user", messageEnvelope.GetProperty("event").GetProperty("actor").GetString());
            Assert.Equal(
                "Show the working directory.",
                messageEnvelope.GetProperty("event").GetProperty("payload").GetProperty("text").GetString());
            Assert.Empty(messageEnvelope.GetProperty("relationships").EnumerateArray());

            var callEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
                "capture", "receipt", receipts[1].GetProperty("observationUuid").GetGuid().ToString()))
                .RootElement;
            Assert.Equal("tool_call", callEnvelope.GetProperty("event").GetProperty("kind").GetString());
            Assert.Equal(
                fixtureCallId,
                callEnvelope.GetProperty("event").GetProperty("payload").GetProperty("callId").GetString());
            Assert.Equal(
                "pwd",
                callEnvelope.GetProperty("event").GetProperty("payload")
                    .GetProperty("arguments").GetProperty("command").GetString());

            var resultEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
                "capture", "receipt", receipts[2].GetProperty("observationUuid").GetGuid().ToString()))
                .RootElement;
            Assert.Equal(
                "tool_result",
                resultEnvelope.GetProperty("event").GetProperty("kind").GetString());
            Assert.Equal(
                "succeeded",
                resultEnvelope.GetProperty("event").GetProperty("payload").GetProperty("outcome").GetString());
            var canonicalRelationship = Assert.Single(
                resultEnvelope.GetProperty("relationships").EnumerateArray());
            Assert.Equal(
                fixtureCallId,
                canonicalRelationship.GetProperty("target").GetProperty("nativeId").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                canonicalRelationship.GetProperty("target").GetProperty("sourceStreamUuid").ValueKind);
            Assert.Contains("LIMITATION:", enabled.Stderr);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public async Task ObservationFanoutAndCheckpointAdvanceAreAtomic()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync("codex-atomic", captureKey);
        using var client = CaptureClient(captureKey);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 0, "atomic-1", "accepted"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var rejected = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ObservationWithDuplicatePartOrder(sourceSessionId, 1, "atomic-2"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM capture_observations WHERE locator_native_id = 'atomic-2')"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            """
            SELECT s.checkpoint_position
            FROM capture_source_streams s
            JOIN capture_source_bindings b USING (binding_uuid)
            WHERE s.source_session_id = @sourceSessionId AND b.stable_name = 'codex-atomic'
            """, new { sourceSessionId }));
    }

    [Fact]
    public async Task StreamRejectsGapsAndBacktrackingWithoutMovingTheAcceptedPrefix()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync("codex-prefix", captureKey);
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 0, "prefix-0", "zero"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var gap = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 2, "prefix-2", "gap"));
        Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        Assert.Contains("expected sourcePosition 1", await gap.Content.ReadAsStringAsync());

        var next = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 1, "prefix-1", "one"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var nextReceipt = await next.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, nextReceipt.GetProperty("sourcePosition").GetInt64());

        var backtrack = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation(sourceSessionId, 0, "different-prefix-0", "other"));
        Assert.Equal(HttpStatusCode.Conflict, backtrack.StatusCode);

        var third = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 2, "prefix-2", "two"));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task StreamKeepsItsFirstEffectiveRouteAfterBindingPolicyChanges()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync("codex-route-fixed", captureKey);
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 0, "route-0", "zero"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // No operator route-update command exists in this disabled slice. Per
        // docs/testing.md this is narrow mechanical setup; behavior remains
        // asserted only through the public capture receipts.
        await using (var connection = new NpgsqlConnection(AdminConnection))
        {
            await connection.ExecuteAsync(
                """
                UPDATE capture_source_bindings
                SET route_namespace = 'homelab'
                WHERE stable_name = 'codex-route-fixed'
                """);
        }

        var second = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 1, "route-1", "one"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var receipt = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("capture/unscoped", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("fallback", receipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task SafetyGateRedactsSyntheticSecretBeforeAnyCaptureAppend()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync("codex-safety", captureKey);
        using var client = CaptureClient(captureKey);
        string seededSyntheticSecret = "AKIA" + "SYNTHETICFIXTURE";

        var request = SafetyObservation(sourceSessionId, seededSyntheticSecret);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string rawRequest = JsonSerializer.Serialize(request, jsonOptions);
        var canonicalRequest = JsonSerializer.Deserialize<MemSrv.Core.CaptureObservationRequest>(
            rawRequest, jsonOptions)!;
        string canonicalRawRequest = JsonSerializer.Serialize(canonicalRequest, jsonOptions);
        string unkeyedRawHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRawRequest))).ToLowerInvariant();
        var response = await client.PostAsJsonAsync("/capture/v1/observations", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        string shown = await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.DoesNotContain(seededSyntheticSecret, shown);
        Assert.Contains("[REDACTED:aws-access-key-id]", shown);
        var envelope = JsonDocument.Parse(shown).RootElement;
        var scan = envelope.GetProperty("observation").GetProperty("scan");
        Assert.Equal("redacted", scan.GetProperty("status").GetString());
        Assert.Contains(
            "aws-access-key-id",
            scan.GetProperty("ruleIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "secret",
            scan.GetProperty("categories").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(8, scan.GetProperty("redactionCount").GetInt32());

        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM capture_observations
                WHERE safe_source_payload::text LIKE @pattern
                   OR source::text LIKE @pattern OR adapter::text LIKE @pattern
              UNION ALL
              SELECT 1 FROM captured_events WHERE payload::text LIKE @pattern
              UNION ALL
              SELECT 1 FROM captured_event_relationships
                WHERE target_native_id LIKE @pattern OR target_kind LIKE @pattern
            )
            """,
            new { pattern = $"%{seededSyntheticSecret}%" }));
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM capture_observations WHERE content_signature = @unkeyedRawHash
            )
            """,
            new { unkeyedRawHash }));
    }

    private HttpClient CaptureClient(string key)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static string CaptureCredential() => $"mcap_{Guid.NewGuid():N}";
    private static string UniqueSession() => $"synthetic-session-{Guid.NewGuid():N}";

    private async Task EnrollAsync(string name, string captureKey)
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, captureKey);
        try
        {
            await RunMemCtlAsync(
                "capture", "enroll", name,
                "--harness", "codex",
                "--agent-id", $"capture:{name}",
                "--credential-file", path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static object Observation(
        string sourceSessionId, long position, string nativeId, string message) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { message },
            events = new object[]
        {
            new { partKey = "message/0", partOrder = 0, kind = "message", actor = "user",
                payload = new { text = message } },
            new { partKey = "tool/1", partOrder = 1, kind = "tool_call", actor = "assistant",
                payload = new { callId = "call-1", tool = "shell", arguments = new { command = "pwd" } } },
            new { partKey = "tool/2", partOrder = 2, kind = "tool_result", actor = "tool",
                payload = new { callId = "call-1", outcome = "succeeded", output = "/workspace" },
                relationships = new[]
                {
                    new
                    {
                        type = "result_for",
                        target = new { nativeId = "call-1", kind = "tool_call" }
                    }
                } }
        }
        };

    private static object InvalidLocatorObservation(string sourceSessionId, object locator) => new
    {
        contractVersion = 1,
        sourceSessionId,
        sourcePosition = 0,
        locator,
        source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
        adapter = new { name = "codex-synthetic", version = "1" },
        sourcePayload = new { text = "invalid locator" },
        events = new[]
        {
            new
            {
                partKey = "message/0",
                partOrder = 0,
                kind = "message",
                actor = "user",
                payload = new { text = "invalid locator" }
            }
        }
    };

    private static object RelationshipObservation(
        string sourceSessionId,
        long position,
        string nativeId,
        Guid? targetSourceStreamUuid) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { text = "relationship" },
            events = new object[]
            {
                new
                {
                    partKey = "tool/0",
                    partOrder = 0,
                    kind = "tool_result",
                    actor = "tool",
                    payload = new { output = "done" },
                    relationships = new[]
                    {
                        new
                        {
                            type = "result_for",
                            target = new
                            {
                                sourceStreamUuid = targetSourceStreamUuid,
                                nativeId = $"call-{Guid.NewGuid():N}",
                                kind = "tool_call"
                            }
                        }
                    }
                }
            }
        };

    private static object ObservationWithDuplicatePartOrder(
        string sourceSessionId, long position, string nativeId) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { message = "must roll back" },
            events = new object[]
        {
            new { partKey = "message/a", partOrder = 0, kind = "message", actor = "user",
                payload = new { text = "one" } },
            new { partKey = "message/b", partOrder = 0, kind = "message", actor = "assistant",
                payload = new { text = "two" } }
        }
        };

    private static object SafetyObservation(string sourceSessionId, string secret) => new
    {
        contractVersion = 1,
        sourceSessionId,
        sourcePosition = 0,
        locator = new { kind = "native_id", nativeId = $"safety-{Guid.NewGuid():N}" },
        source = new { harness = "codex", harnessVersion = secret, recordType = secret },
        adapter = new { name = secret, version = secret },
        sourcePayload = new { message = secret },
        events = new object[]
        {
            new
            {
                partKey = "tool/0",
                partOrder = 0,
                kind = "tool_result",
                actor = "tool",
                payload = new { output = secret },
                relationships = new[]
                {
                    new
                    {
                        type = "result_for",
                        target = new { nativeId = secret, kind = secret }
                    }
                }
            }
        }
    };
}
