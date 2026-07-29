using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptureAdapters;
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

            var oversized = await client.PostAsync(
                "/capture/v1/observations",
                new StringContent(
                    new string('x', 1_000_001),
                    Encoding.UTF8,
                    "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, oversized.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task KnownCredentialRejectsOversizedBodyBeforeParsingOrPersistence()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-body-limit-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        var oversized = await client.PostAsync(
            "/capture/v1/observations",
            new StringContent(
                new string('x', 1_000_001),
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation(
                sourceSessionId,
                0,
                $"body-limit-{Guid.NewGuid():N}",
                "accepted after oversized rejection"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, receipt.GetProperty("sourcePosition").GetInt64());
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
                        "sourceTimestamp", "routeEvidence", "adapter",
                        "safeSourcePayload", "scan", "capturedAt"
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
        foreach (string secretField in new[] { "stable_name", "harness", "agent_id" })
        {
            string captureKey = CaptureCredential();
            string credentialPath = Path.Combine(
                Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(credentialPath, captureKey);
            try
            {
                string stableName = secretField == "stable_name"
                    ? seededSyntheticSecret
                    : $"safe-binding-{Guid.NewGuid():N}";
                string harness = secretField == "harness"
                    ? seededSyntheticSecret
                    : "codex";
                string agentId = secretField == "agent_id"
                    ? seededSyntheticSecret
                    : $"capture:safe-{Guid.NewGuid():N}";
                var rejected = await RunMemCtlForResultAsync(
                    null,
                    "capture", "enroll", stableName,
                    "--harness", harness,
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
            new
            {
                kind = "native_id",
                nativeId = $"native-{Guid.NewGuid():N}",
                sourceContentSha256 = new string('a', 64)
            },
            new { kind = "byte_range", byteOffset = 0L },
            new { kind = "byte_range", byteOffset = 0L, byteLength = 10L },
            new
            {
                kind = "byte_range",
                byteOffset = 0L,
                byteLength = 10L,
                sourceContentSha256 = new string('A', 64)
            },
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
        var secondReceipt = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("established", secondReceipt.GetProperty("routeBasis").GetString());

        var retry = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(sourceSessionId, 1, "record-stable", "original"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryReceipt = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal("established", retryReceipt.GetProperty("routeBasis").GetString());
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
        string fixtureCallId = $"call_{Guid.NewGuid():N}";
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-synthetic-{Guid.NewGuid():N}.jsonl");
        string fixture = await File.ReadAllTextAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        fixture = fixture.Replace(
            "call_fixture_1", fixtureCallId, StringComparison.Ordinal);
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
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = RuntimeStateDirectory(fixturePath)
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
                ["content/0:message", "tool/1", "tool/2"],
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
                firstNewline + 1,
                messageObservation.GetProperty("locator").GetProperty("byteLength").GetInt64());
            Assert.Equal(
                "2026-07-14T12:00:00.000Z",
                messageObservation.GetProperty("sourceTimestamp").GetProperty("raw").GetString());
            Assert.Equal(
                DateTimeOffset.Parse("2026-07-14T12:00:00.000Z"),
                messageObservation.GetProperty("sourceTimestamp").GetProperty("parsed")
                    .GetDateTimeOffset());
            Assert.Equal(
                "synthetic",
                messageObservation.GetProperty("source").GetProperty("harnessVersion").GetString());
            Assert.Equal(
                "codex-synthetic-jsonl",
                messageObservation.GetProperty("adapter").GetProperty("name").GetString());
            Assert.Equal(
                "Show the working directory.",
                messageObservation.GetProperty("safeSourcePayload")
                    .GetProperty("payload").GetProperty("content")[0]
                    .GetProperty("text").GetString());
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
            CaptureRuntimeStreamState localStream = Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams);
            Assert.Equal(2, localStream.EnqueuedThrough);
            Assert.Empty(localStream.Queue);
            Assert.Equal(2, localStream.LastServerReceipt?.SourcePosition);
            Assert.Equal("new", localStream.LastServerReceipt?.Status);
            Assert.Equal(
                receipts[2].GetProperty("observationUuid").GetGuid(),
                localStream.LastServerReceipt?.ObservationUuid);
            Assert.Contains("LIMITATION:", enabled.Stderr);
        }
        finally
        {
            File.Delete(fixturePath);
            DeleteRuntimeState(fixturePath);
        }
    }

    [Fact]
    public async Task PackagedTracerAndOperatorExposeVersionedCodexMessagePartsAndRetryIdentities()
    {
        const string seededSyntheticSecret = "AKIA" + "SYNTHETICFIXTURE";
        string captureKey = CaptureCredential();
        await EnrollAsync($"codex-message-parts-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-message-parts-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string transcriptPath = Path.Combine(transcriptRoot, "messages.jsonl");
        string firstStateDirectory = Path.Combine(directory, "state-first");
        string retryStateDirectory = Path.Combine(directory, "state-retry");
        Directory.CreateDirectory(transcriptRoot);
        File.Copy(
            Path.Combine(
                _root,
                "fixtures/adapter-conformance/codex-cli-0.144.messages.synthetic.jsonl"),
            transcriptPath);

        Dictionary<string, string> EnvironmentFor(string stateDirectory) => new()
        {
            ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
            ["OVERMIND_CAPTURE_URL"] = _baseUrl,
            ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
            ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
            ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "60000",
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
        };

        async Task<JsonElement[]> CaptureOnceAsync(
            string stateDirectory, string expectedStatus)
        {
            using var process = TestProcessRunner.StartCaptureTracer(
                EnvironmentFor(stateDirectory));
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                var receipts = new JsonElement[6];
                for (int index = 0; index < receipts.Length; index++)
                {
                    receipts[index] = await ReadTracerReceiptAsync(process);
                }
                Assert.All(
                    receipts,
                    receipt => Assert.Equal(
                        expectedStatus, receipt.GetProperty("status").GetString()));
                return receipts;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                await stderr;
            }
        }

        async Task<JsonElement[]> ReadOperatorReceiptAsync(JsonElement receipt)
        {
            string shown = await RunMemCtlAsync(
                "capture",
                "receipt",
                receipt.GetProperty("observationUuid").GetGuid().ToString());
            Assert.DoesNotContain(seededSyntheticSecret, shown, StringComparison.Ordinal);
            return shown.Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
        }

        try
        {
            JsonElement[] accepted = await CaptureOnceAsync(firstStateDirectory, "new");
            Assert.Equal(
                ["content/0:message", "content/1:message"],
                accepted[0].GetProperty("events").EnumerateArray()
                    .Select(item => item.GetProperty("partKey").GetString()));
            Assert.Equal(
                [0, 1],
                accepted[0].GetProperty("events").EnumerateArray()
                    .Select(item => item.GetProperty("partOrder").GetInt32()));
            Assert.Equal(
                ["user", "user"],
                accepted[0].GetProperty("events").EnumerateArray()
                    .Select(item => item.GetProperty("actor").GetString()));
            Assert.Equal(
                "annotation",
                Assert.Single(accepted[1].GetProperty("events").EnumerateArray())
                    .GetProperty("kind").GetString());
            Assert.Equal(
                "developer",
                Assert.Single(accepted[2].GetProperty("events").EnumerateArray())
                    .GetProperty("actor").GetString());
            Assert.Equal(
                "system",
                Assert.Single(accepted[3].GetProperty("events").EnumerateArray())
                    .GetProperty("actor").GetString());
            Assert.Equal(
                ["assistant", "assistant"],
                accepted[4].GetProperty("events").EnumerateArray()
                    .Select(item => item.GetProperty("actor").GetString()));
            Assert.True(
                accepted[0].GetProperty("observation").GetProperty("safeSourcePayload")
                    .GetProperty("additiveMessageFixtureField")
                    .GetProperty("retained").GetBoolean());

            JsonElement[] userEnvelopes = await ReadOperatorReceiptAsync(accepted[0]);
            JsonElement[] developerEnvelopes = await ReadOperatorReceiptAsync(accepted[2]);
            JsonElement[] systemEnvelopes = await ReadOperatorReceiptAsync(accepted[3]);
            JsonElement[] assistantEnvelopes = await ReadOperatorReceiptAsync(accepted[4]);

            Assert.Equal(2, userEnvelopes.Length);
            Assert.Equal(
                ["content/0:message", "content/1:message"],
                userEnvelopes.Select(
                    envelope => envelope.GetProperty("event").GetProperty("partKey").GetString()));
            Assert.Equal(
                ["First user part.", "Second user part."],
                userEnvelopes.Select(
                    envelope => envelope.GetProperty("event").GetProperty("payload")
                        .GetProperty("text").GetString()));
            Assert.Equal(
                ["user", "user"],
                userEnvelopes.Select(
                    envelope => envelope.GetProperty("event").GetProperty("actor").GetString()));
            Assert.Equal(
                "[REDACTED:aws-access-key-id]",
                userEnvelopes[0].GetProperty("observation").GetProperty("safeSourcePayload")
                    .GetProperty("payload").GetProperty("content")[0]
                    .GetProperty("futureContentField").GetString());
            Assert.All(userEnvelopes, envelope =>
            {
                JsonElement payload = envelope.GetProperty("event").GetProperty("payload");
                Assert.Equal(["text"], payload.EnumerateObject().Select(property => property.Name));
                Assert.False(payload.TryGetProperty("futureContentField", out _));
            });

            JsonElement developerEnvelope = Assert.Single(developerEnvelopes);
            Assert.Equal(
                "developer",
                developerEnvelope.GetProperty("event").GetProperty("actor").GetString());
            Assert.Equal(
                "Developer instruction.",
                developerEnvelope.GetProperty("event").GetProperty("payload")
                    .GetProperty("text").GetString());

            JsonElement systemEnvelope = Assert.Single(systemEnvelopes);
            Assert.Equal(
                "system",
                systemEnvelope.GetProperty("event").GetProperty("actor").GetString());
            Assert.Equal(
                "System instruction.",
                systemEnvelope.GetProperty("event").GetProperty("payload")
                    .GetProperty("text").GetString());
            Assert.False(
                systemEnvelope.GetProperty("event").GetProperty("payload")
                    .TryGetProperty("futureMessageField", out _));

            Assert.Equal(2, assistantEnvelopes.Length);
            Assert.Equal(
                ["assistant", "assistant"],
                assistantEnvelopes.Select(
                    envelope => envelope.GetProperty("event").GetProperty("actor").GetString()));
            Assert.Equal(
                ["First assistant part.", "Second assistant part."],
                assistantEnvelopes.Select(
                    envelope => envelope.GetProperty("event").GetProperty("payload")
                        .GetProperty("text").GetString()));
            Assert.All(
                assistantEnvelopes,
                envelope => Assert.Equal(
                    ["text"],
                    envelope.GetProperty("event").GetProperty("payload")
                        .EnumerateObject().Select(property => property.Name)));

            JsonElement[] retried = await CaptureOnceAsync(
                retryStateDirectory, "already_accepted");
            Assert.Equal(
                accepted.Select(receipt => receipt.GetProperty("observationUuid").GetGuid()),
                retried.Select(receipt => receipt.GetProperty("observationUuid").GetGuid()));
            Assert.Equal(
                accepted.SelectMany(receipt => receipt.GetProperty("events").EnumerateArray())
                    .Select(item => item.GetProperty("traceUuid").GetGuid()),
                retried.SelectMany(receipt => receipt.GetProperty("events").EnumerateArray())
                    .Select(item => item.GetProperty("traceUuid").GetGuid()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerStartupDiscoversEveryExistingStreamBeforeFirstWakeup()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-startup-discovery-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-startup-discovery-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(transcriptRoot);
        string firstRecord = (await File.ReadAllLinesAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl")))[0] + "\n";
        await File.WriteAllTextAsync(
            Path.Combine(transcriptRoot, "first.jsonl"),
            firstRecord,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(transcriptRoot, "second.jsonl"),
            firstRecord,
            new UTF8Encoding(false));

        using var process = TestProcessRunner.StartCaptureTracer(
            new Dictionary<string, string>
            {
                ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
                ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
                // No scheduled wakeup can occur during the test. Both receipts
                // therefore prove the immediate startup enumeration.
                ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "60000",
                ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
            });
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        try
        {
            JsonElement first = await ReadTracerReceiptAsync(process);
            JsonElement second = await ReadTracerReceiptAsync(process);
            Assert.Equal(0, first.GetProperty("sourcePosition").GetInt64());
            Assert.Equal(0, second.GetProperty("sourcePosition").GetInt64());
            Assert.NotEqual(
                first.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid(),
                second.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid());

            foreach (JsonElement receipt in new[] { first, second })
            {
                string canonical = await RunMemCtlAsync(
                    "capture",
                    "receipt",
                    receipt.GetProperty("observationUuid").GetGuid().ToString());
                Assert.Contains("\"kind\":\"message\"", canonical);
            }

            CaptureRuntimeSnapshot startupState =
                await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
            Assert.Equal(2, startupState.Streams.Count);
            Assert.All(startupState.Streams, stream => Assert.Empty(stream.Queue));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await stderr;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerStartupResumesEachRetainedStreamAtItsNextPosition()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-startup-resume-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-startup-resume-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string stateDirectory = Path.Combine(directory, "state");
        string firstTranscript = Path.Combine(transcriptRoot, "first.jsonl");
        string secondTranscript = Path.Combine(transcriptRoot, "second.jsonl");
        Directory.CreateDirectory(transcriptRoot);
        string[] records = await File.ReadAllLinesAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        await File.WriteAllTextAsync(
            firstTranscript, records[0] + "\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            secondTranscript,
            records[0] + "\n" + records[1] + "\n",
            new UTF8Encoding(false));

        Dictionary<string, string> environment = new()
        {
            ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
            ["OVERMIND_CAPTURE_URL"] = _baseUrl,
            ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
            ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
            ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "60000",
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
        };
        Guid firstStreamUuid = Guid.Empty;
        Guid secondStreamUuid = Guid.Empty;

        try
        {
            using (var seed = TestProcessRunner.StartCaptureTracer(environment))
            {
                Task<string> seedStderr = seed.StandardError.ReadToEndAsync();
                JsonElement[] seeded =
                [
                    await ReadTracerReceiptAsync(seed),
                    await ReadTracerReceiptAsync(seed),
                    await ReadTracerReceiptAsync(seed)
                ];
                Assert.Equal(
                    [0L, 0L, 1L],
                    seeded.Select(receipt =>
                        receipt.GetProperty("sourcePosition").GetInt64()));
                firstStreamUuid = seeded[0].GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid();
                secondStreamUuid = seeded[1].GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid();
                Assert.NotEqual(firstStreamUuid, secondStreamUuid);
                Assert.Equal(
                    secondStreamUuid,
                    seeded[2].GetProperty("observation")
                        .GetProperty("sourceStreamUuid").GetGuid());
                seed.Kill(entireProcessTree: true);
                await seed.WaitForExitAsync();
                await seedStderr;
            }

            CaptureRuntimeSnapshot retained =
                await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
            Assert.Equal(
                [0L, 1L],
                retained.Streams
                    .OrderBy(stream => stream.SourceStream, StringComparer.Ordinal)
                    .Select(stream => stream.EnqueuedThrough!.Value)
                    .OrderBy(position => position));
            Assert.All(retained.Streams, stream => Assert.Empty(stream.Queue));

            await File.AppendAllTextAsync(
                firstTranscript, records[1] + "\n", new UTF8Encoding(false));
            await File.AppendAllTextAsync(
                secondTranscript, records[2] + "\n", new UTF8Encoding(false));

            using var resumed = TestProcessRunner.StartCaptureTracer(environment);
            Task<string> resumedStderr = resumed.StandardError.ReadToEndAsync();
            JsonElement[] resumedReceipts =
            [
                await ReadTracerReceiptAsync(resumed),
                await ReadTracerReceiptAsync(resumed)
            ];
            Assert.Equal(
                [1L, 2L],
                resumedReceipts.Select(receipt =>
                    receipt.GetProperty("sourcePosition").GetInt64()));
            Assert.Equal(
                firstStreamUuid,
                resumedReceipts[0].GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid());
            Assert.Equal(
                secondStreamUuid,
                resumedReceipts[1].GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid());

            foreach (JsonElement receipt in resumedReceipts)
            {
                string canonical = await RunMemCtlAsync(
                    "capture",
                    "receipt",
                    receipt.GetProperty("observationUuid").GetGuid().ToString());
                Assert.Contains("\"contractVersion\":1", canonical);
            }

            CaptureRuntimeSnapshot converged =
                await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
            Assert.Equal(
                [1L, 2L],
                converged.Streams
                    .Select(stream => stream.EnqueuedThrough!.Value)
                    .OrderBy(position => position));
            Assert.All(converged.Streams, stream => Assert.Empty(stream.Queue));

            resumed.Kill(entireProcessTree: true);
            await resumed.WaitForExitAsync();
            await resumedStderr;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerCapturesCompletedAppendsAndNewStreamsAcrossCycles()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-scheduled-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-scheduled-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string stateDirectory = Path.Combine(directory, "state");
        string firstTranscript = Path.Combine(transcriptRoot, "first.jsonl");
        string startupTranscript = Path.Combine(transcriptRoot, "startup-second.jsonl");
        string laterTranscript = Path.Combine(transcriptRoot, "later-third.jsonl");
        Directory.CreateDirectory(transcriptRoot);
        string[] records = await File.ReadAllLinesAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        await File.WriteAllTextAsync(
            firstTranscript,
            records[0] + "\n" + records[1],
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            startupTranscript,
            records[0] + "\n",
            new UTF8Encoding(false));

        using var process = TestProcessRunner.StartCaptureTracer(
            new Dictionary<string, string>
            {
                ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
                ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
                ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "50",
                ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "20"
            });
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        try
        {
            JsonElement first = await ReadTracerReceiptAsync(process);
            Assert.Equal(0, first.GetProperty("sourcePosition").GetInt64());
            JsonElement startupSecond = await ReadTracerReceiptAsync(process);
            Assert.Equal(0, startupSecond.GetProperty("sourcePosition").GetInt64());
            Assert.NotEqual(
                first.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid(),
                startupSecond.GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid());

            await Task.Delay(250);
            CaptureRuntimeSnapshot startupState =
                await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
            Assert.Equal(2, startupState.Streams.Count);
            Assert.All(startupState.Streams, stream =>
            {
                Assert.Equal(0, stream.EnqueuedThrough);
                Assert.Empty(stream.Queue);
            });

            await File.AppendAllTextAsync(
                firstTranscript, "\n", new UTF8Encoding(false));
            JsonElement second = await ReadTracerReceiptAsync(process);
            Assert.Equal(1, second.GetProperty("sourcePosition").GetInt64());

            int split = records[2].Length / 2;
            await File.AppendAllTextAsync(
                firstTranscript,
                records[2][..split],
                new UTF8Encoding(false));
            await Task.Delay(250);
            CaptureRuntimeStreamState activeStream = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams,
                stream => stream.EnqueuedThrough == 1);
            Assert.Equal(1, activeStream.EnqueuedThrough);
            Assert.Empty(activeStream.Queue);

            await File.AppendAllTextAsync(
                firstTranscript,
                records[2][split..] + "\n",
                new UTF8Encoding(false));
            JsonElement third = await ReadTracerReceiptAsync(process);
            Assert.Equal(2, third.GetProperty("sourcePosition").GetInt64());

            await File.WriteAllTextAsync(
                laterTranscript,
                records[0] + "\n",
                new UTF8Encoding(false));
            JsonElement discovered = await ReadTracerReceiptAsync(process);
            Assert.Equal(0, discovered.GetProperty("sourcePosition").GetInt64());
            Assert.NotEqual(
                first.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid(),
                discovered.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid());

            foreach (JsonElement receipt in
                new[] { first, startupSecond, second, third, discovered })
            {
                string canonical = await RunMemCtlAsync(
                    "capture",
                    "receipt",
                    receipt.GetProperty("observationUuid").GetGuid().ToString());
                Assert.Contains("\"contractVersion\":1", canonical);
                Assert.Contains("\"event\":", canonical);
            }

            CaptureRuntimeSnapshot finalState =
                await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
            Assert.Equal(3, finalState.Streams.Count);
            Assert.All(finalState.Streams, stream => Assert.Empty(stream.Queue));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await stderr;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerReleasesFinalRecordOnlyAfterStableArchiveEvidence()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-scheduled-archive-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-scheduled-archive-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string activeDirectory = Path.Combine(transcriptRoot, "sessions", "2026", "07");
        string archiveDirectory = Path.Combine(transcriptRoot, "archived_sessions");
        string stateDirectory = Path.Combine(directory, "state");
        string activePath = Path.Combine(activeDirectory, "session.jsonl");
        string archivedPath = Path.Combine(archiveDirectory, "session.jsonl");
        Directory.CreateDirectory(activeDirectory);
        Directory.CreateDirectory(archiveDirectory);
        string[] records = await File.ReadAllLinesAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        await File.WriteAllTextAsync(
            activePath,
            records[0] + "\n" + records[1],
            new UTF8Encoding(false));

        using var process = TestProcessRunner.StartCaptureTracer(
            new Dictionary<string, string>
            {
                ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
                ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
                ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "50",
                ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
            });
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        try
        {
            JsonElement completedPrefix = await ReadTracerReceiptAsync(process);
            Assert.Equal(0, completedPrefix.GetProperty("sourcePosition").GetInt64());
            Guid sourceStreamUuid = completedPrefix.GetProperty("observation")
                .GetProperty("sourceStreamUuid").GetGuid();

            await Task.Delay(250);
            CaptureRuntimeStreamState active = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(0, active.EnqueuedThrough);
            Assert.Empty(active.Queue);

            File.Move(activePath, archivedPath);
            JsonElement terminal = await ReadTracerReceiptAsync(process);
            Assert.Equal(1, terminal.GetProperty("sourcePosition").GetInt64());
            Assert.Equal(
                sourceStreamUuid,
                terminal.GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid());

            CaptureRuntimeStreamState archived = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(1, archived.EnqueuedThrough);
            Assert.Empty(archived.Queue);

            string canonical = await RunMemCtlAsync(
                "capture",
                "receipt",
                terminal.GetProperty("observationUuid").GetGuid().ToString());
            Assert.Contains("\"kind\":\"tool_call\"", canonical);
            Assert.Equal(
                new CaptureLedgerMechanics(2, 2, 0, 1),
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await stderr;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerRestartConvergesQueuedOutageWithoutAWakeup()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-scheduled-restart-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-scheduled-restart-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(transcriptRoot);
        File.Copy(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"),
            Path.Combine(transcriptRoot, "restart.jsonl"));
        int unavailablePort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            unavailablePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        Dictionary<string, string> EnvironmentFor(string captureUrl) => new()
        {
            ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
            ["OVERMIND_CAPTURE_URL"] = captureUrl,
            ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
            ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
            ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "75",
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
        };

        try
        {
            using (var outage = TestProcessRunner.StartCaptureTracer(
                EnvironmentFor($"http://127.0.0.1:{unavailablePort}")))
            {
                Task<string> outageStdout = outage.StandardOutput.ReadToEndAsync();
                Task<string> outageStderr = outage.StandardError.ReadToEndAsync();
                await WaitUntilAsync(async () =>
                {
                    CaptureRuntimeSnapshot snapshot =
                        await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
                    return snapshot.Streams.SingleOrDefault()?.Queue.Count == 3;
                });
                outage.Kill(entireProcessTree: true);
                await outage.WaitForExitAsync();
                Assert.Empty(await outageStdout);
                await outageStderr;
            }

            CaptureRuntimeStreamState retained = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal([0L, 1L, 2L], retained.Queue.Select(item => item.SourcePosition));
            Assert.Null(retained.LastServerReceipt);

            using var resumed = TestProcessRunner.StartCaptureTracer(EnvironmentFor(_baseUrl));
            Task<string> resumedStderr = resumed.StandardError.ReadToEndAsync();
            JsonElement[] receipts =
            [
                await ReadTracerReceiptAsync(resumed),
                await ReadTracerReceiptAsync(resumed),
                await ReadTracerReceiptAsync(resumed)
            ];
            Assert.Equal(
                [0L, 1L, 2L],
                receipts.Select(receipt =>
                    receipt.GetProperty("sourcePosition").GetInt64()));
            Assert.All(receipts, receipt =>
                Assert.Equal("new", receipt.GetProperty("status").GetString()));
            string canonical = await RunMemCtlAsync(
                "capture",
                "receipt",
                receipts[2].GetProperty("observationUuid").GetGuid().ToString());
            Assert.Contains("\"kind\":\"tool_result\"", canonical);

            resumed.Kill(entireProcessTree: true);
            await resumed.WaitForExitAsync();
            await resumedStderr;
            Assert.Empty(Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams).Queue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerRetriesAWithheldResponseWithoutRestart()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-scheduled-timeout-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-scheduled-timeout-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(transcriptRoot);
        string firstRecord = (await File.ReadAllLinesAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl")))[0] + "\n";
        await File.WriteAllTextAsync(
            Path.Combine(transcriptRoot, "timeout.jsonl"),
            firstRecord,
            new UTF8Encoding(false));
        int proxyPort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        using var proxy = new HttpListener();
        proxy.Prefixes.Add($"http://127.0.0.1:{proxyPort}/");
        proxy.Start();
        var secondAttemptAccepted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> server = WithholdFirstAndForwardSecondAsync(
            proxy,
            captureKey,
            secondAttemptAccepted,
            releaseSecondAttempt);

        using var process = TestProcessRunner.StartCaptureTracer(
            new Dictionary<string, string>
            {
                ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                ["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{proxyPort}",
                ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
                ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
                ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "50",
                ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
            });
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await secondAttemptAccepted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(process.HasExited);
            CaptureRuntimeStreamState retained = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal([0L], retained.Queue.Select(item => item.SourcePosition));
            Assert.Null(retained.LastServerReceipt);

            releaseSecondAttempt.SetResult();
            JsonElement retry = await ReadTracerReceiptAsync(process);
            JsonElement accepted = await server;
            Assert.Equal("new", retry.GetProperty("status").GetString());
            Assert.Equal(
                accepted.GetProperty("observationUuid").GetGuid(),
                retry.GetProperty("observationUuid").GetGuid());
            Guid sourceStreamUuid = retry.GetProperty("observation")
                .GetProperty("sourceStreamUuid").GetGuid();

            CaptureRuntimeStreamState converged = Assert.Single(
                (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
            Assert.Equal(0, converged.EnqueuedThrough);
            Assert.Empty(converged.Queue);
            Assert.Equal(
                "new",
                converged.LastServerReceipt?.Status);
            string canonical = await RunMemCtlAsync(
                "capture",
                "receipt",
                retry.GetProperty("observationUuid").GetGuid().ToString());
            Assert.Contains("\"kind\":\"message\"", canonical);
            Assert.Equal(
                new CaptureLedgerMechanics(1, 1, 0, 0),
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid));
        }
        finally
        {
            releaseSecondAttempt.TrySetResult();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await stderr;
            proxy.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledPackagedTracerRestartConvergesAfterLostSuccessResponse()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-scheduled-lost-{Guid.NewGuid():N}", captureKey);
        string directory = Path.Combine(
            Path.GetTempPath(), $"codex-scheduled-lost-{Guid.NewGuid():N}");
        string transcriptRoot = Path.Combine(directory, "transcripts");
        string stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(transcriptRoot);
        File.Copy(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"),
            Path.Combine(transcriptRoot, "lost-response.jsonl"));
        int proxyPort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        using var proxy = new HttpListener();
        proxy.Prefixes.Add($"http://127.0.0.1:{proxyPort}/");
        proxy.Start();

        Dictionary<string, string> EnvironmentFor(string captureUrl) => new()
        {
            ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
            ["OVERMIND_CAPTURE_URL"] = captureUrl,
            ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
            ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = transcriptRoot,
            ["OVERMIND_CAPTURE_STATE_DIR"] = stateDirectory,
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = "60000",
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = "0"
        };

        try
        {
            Task<JsonElement[]> committed =
                CommitToolResultAndLoseResponseAsync(proxy, captureKey);
            using (var ambiguous = TestProcessRunner.StartCaptureTracer(
                EnvironmentFor($"http://127.0.0.1:{proxyPort}")))
            {
                Task<string> ambiguousStderr = ambiguous.StandardError.ReadToEndAsync();
                JsonElement[] deliveredBeforeLoss =
                [
                    await ReadTracerReceiptAsync(ambiguous),
                    await ReadTracerReceiptAsync(ambiguous)
                ];
                Assert.Equal(
                    [0L, 1L],
                    deliveredBeforeLoss.Select(receipt =>
                        receipt.GetProperty("sourcePosition").GetInt64()));

                JsonElement committedResult = (await committed)[2];
                await WaitUntilAsync(async () =>
                {
                    CaptureRuntimeSnapshot snapshot =
                        await new FileCaptureRuntimeState(stateDirectory).ReadAsync();
                    return snapshot.Streams.SingleOrDefault()?.Queue
                        .Select(item => item.SourcePosition)
                        .SequenceEqual([2L]) == true;
                });
                CaptureRuntimeStreamState retained = Assert.Single(
                    (await new FileCaptureRuntimeState(stateDirectory).ReadAsync()).Streams);
                Assert.Equal([2L], retained.Queue.Select(item => item.SourcePosition));
                Assert.Equal(1, retained.LastServerReceipt?.SourcePosition);

                string canonicalBeforeRestart = await RunMemCtlAsync(
                    "capture",
                    "receipt",
                    committedResult.GetProperty("observationUuid").GetGuid().ToString());
                Guid sourceStreamUuid = committedResult.GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid();
                CaptureLedgerMechanics mechanicsBeforeRestart =
                    await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid);
                Assert.Equal(
                    new CaptureLedgerMechanics(3, 3, 1, 2),
                    mechanicsBeforeRestart);

                ambiguous.Kill(entireProcessTree: true);
                await ambiguous.WaitForExitAsync();
                await ambiguousStderr;

                using var resumed = TestProcessRunner.StartCaptureTracer(
                    EnvironmentFor(_baseUrl));
                Task<string> resumedStderr = resumed.StandardError.ReadToEndAsync();
                JsonElement retry = await ReadTracerReceiptAsync(resumed);
                Assert.Equal(
                    "already_accepted",
                    retry.GetProperty("status").GetString());
                Assert.Equal(2, retry.GetProperty("sourcePosition").GetInt64());
                Assert.Equal(
                    committedResult.GetProperty("observationUuid").GetGuid(),
                    retry.GetProperty("observationUuid").GetGuid());
                Assert.Empty(Assert.Single(
                    (await new FileCaptureRuntimeState(stateDirectory).ReadAsync())
                        .Streams).Queue);
                Assert.Equal(
                    canonicalBeforeRestart,
                    await RunMemCtlAsync(
                        "capture",
                        "receipt",
                        committedResult.GetProperty("observationUuid").GetGuid().ToString()));
                Assert.Equal(
                    mechanicsBeforeRestart,
                    await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid));

                resumed.Kill(entireProcessTree: true);
                await resumed.WaitForExitAsync();
                await resumedStderr;
            }
        }
        finally
        {
            proxy.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTracerRestartResumesEarliestResponsibilityAfterEndpointOutage()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-outage-{Guid.NewGuid():N}", captureKey);
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-outage-{Guid.NewGuid():N}.jsonl");
        File.Copy(Path.Combine(_root, "fixtures/codex-synthetic.jsonl"), fixturePath);
        int unavailablePort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            unavailablePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        try
        {
            var outage = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{unavailablePort}",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = RuntimeStateDirectory(fixturePath)
                });
            Assert.Equal(1, outage.ExitCode);
            Assert.Empty(outage.Stdout);
            CaptureRuntimeStreamState retained = Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams);
            Assert.Equal([0L, 1L, 2L], retained.Queue.Select(item => item.SourcePosition));
            Assert.Null(retained.LastServerReceipt);

            var resumed = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(0, resumed.ExitCode);
            JsonElement[] receipts = ParseReceiptLines(resumed.Stdout);
            Assert.Equal([0L, 1L, 2L], receipts.Select(
                receipt => receipt.GetProperty("sourcePosition").GetInt64()));
            Assert.All(receipts, receipt =>
                Assert.Equal("new", receipt.GetProperty("status").GetString()));
            Assert.Empty(Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams).Queue);
        }
        finally
        {
            File.Delete(fixturePath);
            DeleteRuntimeState(fixturePath);
        }
    }

    [Fact]
    public async Task PackagedTracerConvergesAfterToolResultCommitResponseIsLost()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-ambiguous-{Guid.NewGuid():N}", captureKey);
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-ambiguous-{Guid.NewGuid():N}.jsonl");
        File.Copy(Path.Combine(_root, "fixtures/codex-synthetic.jsonl"), fixturePath);
        int proxyPort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        using var proxy = new HttpListener();
        proxy.Prefixes.Add($"http://127.0.0.1:{proxyPort}/");
        proxy.Start();

        try
        {
            Task<JsonElement[]> committed = CommitToolResultAndLoseResponseAsync(
                proxy, captureKey);
            var ambiguous = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{proxyPort}",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = RuntimeStateDirectory(fixturePath)
                });
            JsonElement[] committedReceipts = await committed;
            JsonElement committedResult = committedReceipts[2];

            Assert.NotEqual(0, ambiguous.ExitCode);
            JsonElement[] deliveredBeforeLoss = ParseReceiptLines(ambiguous.Stdout);
            Assert.Equal([0L, 1L], deliveredBeforeLoss.Select(
                receipt => receipt.GetProperty("sourcePosition").GetInt64()));
            CaptureRuntimeStreamState retained = Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams);
            Assert.Equal([2L], retained.Queue.Select(item => item.SourcePosition));
            Assert.Equal(1, retained.LastServerReceipt?.SourcePosition);

            string canonicalBeforeRetry = await RunMemCtlAsync(
                "capture", "receipt",
                committedResult.GetProperty("observationUuid").GetGuid().ToString());
            JsonElement resultEnvelope = JsonDocument.Parse(canonicalBeforeRetry).RootElement;
            Assert.Equal(
                "tool_result",
                resultEnvelope.GetProperty("event").GetProperty("kind").GetString());
            Assert.Single(resultEnvelope.GetProperty("relationships").EnumerateArray());
            Guid sourceStreamUuid = committedResult.GetProperty("observation")
                .GetProperty("sourceStreamUuid").GetGuid();
            CaptureLedgerMechanics beforeRetry =
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid);
            Assert.Equal(new CaptureLedgerMechanics(3, 3, 1, 2), beforeRetry);

            var retry = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(0, retry.ExitCode);
            JsonElement retryReceipt = Assert.Single(ParseReceiptLines(retry.Stdout));
            Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
            Assert.Equal(2, retryReceipt.GetProperty("sourcePosition").GetInt64());
            Assert.Equal(
                committedResult.GetProperty("observationUuid").GetGuid(),
                retryReceipt.GetProperty("observationUuid").GetGuid());
            Assert.Empty(Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams).Queue);
            string canonicalAfterRetry = await RunMemCtlAsync(
                "capture", "receipt",
                committedResult.GetProperty("observationUuid").GetGuid().ToString());
            Assert.Equal(canonicalBeforeRetry, canonicalAfterRetry);
            Assert.Equal(
                beforeRetry,
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid));
        }
        finally
        {
            proxy.Stop();
            File.Delete(fixturePath);
            DeleteRuntimeState(fixturePath);
        }
    }

    [Fact]
    public async Task RuntimeStopsWhenVerifiedFixtureBytesChangeWithoutChangingJson()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-byte-identity-{Guid.NewGuid():N}", captureKey);
        string fixtureCallId = $"call_{Guid.NewGuid():N}";
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-byte-identity-{Guid.NewGuid():N}.jsonl");
        string fixture = await File.ReadAllTextAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        fixture = fixture.Replace(
            "call_fixture_1", fixtureCallId, StringComparison.Ordinal);
        string firstBytes = fixture.Replace(
            "\"type\":\"response_item\"",
            "\"type\": \"response_item\"",
            StringComparison.Ordinal);
        string changedBytes = fixture.Replace(
            "\"type\":\"response_item\"",
            "\"type\" :\"response_item\"",
            StringComparison.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(firstBytes),
            Encoding.UTF8.GetByteCount(changedBytes));
        int proxyPort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        using var proxy = new HttpListener();
        proxy.Prefixes.Add($"http://127.0.0.1:{proxyPort}/");
        proxy.Start();

        try
        {
            await File.WriteAllTextAsync(fixturePath, firstBytes, new UTF8Encoding(false));
            Task forwardFirst = ForwardFirstThenFailSecondAsync(proxy, captureKey);
            var first = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = $"http://127.0.0.1:{proxyPort}",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = RuntimeStateDirectory(fixturePath)
                });
            await forwardFirst;
            Assert.Equal(1, first.ExitCode);
            JsonElement firstReceipt = Assert.Single(ParseReceiptLines(first.Stdout));
            Guid observationUuid = firstReceipt.GetProperty("observationUuid").GetGuid();
            Guid sourceStreamUuid = firstReceipt.GetProperty("observation")
                .GetProperty("sourceStreamUuid").GetGuid();
            string beforeConflict = await RunMemCtlAsync(
                "capture", "receipt", observationUuid.ToString());
            JsonElement canonicalBeforeConflict =
                JsonDocument.Parse(beforeConflict).RootElement;
            Assert.Equal(
                "message",
                canonicalBeforeConflict.GetProperty("event").GetProperty("kind").GetString());
            Assert.Empty(
                canonicalBeforeConflict.GetProperty("relationships").EnumerateArray());
            CaptureLedgerMechanics mechanicsBeforeConflict =
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid);
            Assert.Equal(
                new CaptureLedgerMechanics(1, 1, 0, 0),
                mechanicsBeforeConflict);
            CaptureRuntimeStreamState retained = Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams);
            Assert.Equal([1L, 2L], retained.Queue.Select(item => item.SourcePosition));
            Assert.Equal(0, retained.LastServerReceipt?.SourcePosition);

            await File.WriteAllTextAsync(fixturePath, changedBytes, new UTF8Encoding(false));
            var conflict = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(4, conflict.ExitCode);
            Assert.Empty(conflict.Stdout);
            Assert.Contains("verified_prefix_changed", conflict.Stderr);

            var runtimeState =
                new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath));
            CaptureRuntimeSnapshot stopped = await runtimeState.ReadAsync();
            CaptureRuntimeStreamState stoppedStream = Assert.Single(stopped.Streams);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.VerifiedPrefixChanged,
                    null),
                stoppedStream.Stop);
            Assert.Equal([1L, 2L], stoppedStream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(2, stoppedStream.EnqueuedThrough);
            Assert.Equal(0, stoppedStream.LastServerReceipt?.SourcePosition);
            Assert.Equal(
                beforeConflict,
                await RunMemCtlAsync("capture", "receipt", observationUuid.ToString()));
            Assert.Equal(
                mechanicsBeforeConflict,
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid));

            var repeated = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(4, repeated.ExitCode);
            Assert.Empty(repeated.Stdout);
            Assert.Contains("verified_prefix_changed", repeated.Stderr);
            Assert.Equal(
                JsonSerializer.Serialize(stopped),
                JsonSerializer.Serialize(await runtimeState.ReadAsync()));

            string afterConflict = await RunMemCtlAsync(
                "capture", "receipt", observationUuid.ToString());
            Assert.Equal(beforeConflict, afterConflict);
            Assert.Equal(
                mechanicsBeforeConflict,
                await ReadCaptureLedgerMechanicsAsync(sourceStreamUuid));
        }
        finally
        {
            proxy.Stop();
            File.Delete(fixturePath);
            DeleteRuntimeState(fixturePath);
        }
    }

    [Fact]
    public async Task RuntimeStopsWhenOnlyVerifiedJsonlSeparatorsChange()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-separator-identity-{Guid.NewGuid():N}", captureKey);
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-separator-identity-{Guid.NewGuid():N}.jsonl");
        string fixture = await File.ReadAllTextAsync(
            Path.Combine(_root, "fixtures/codex-synthetic.jsonl"));
        fixture = fixture.Replace(
            "call_fixture_1", $"call_{Guid.NewGuid():N}", StringComparison.Ordinal);

        try
        {
            await File.WriteAllTextAsync(fixturePath, fixture, new UTF8Encoding(false));
            var first = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(0, first.ExitCode);
            var firstReceipt = JsonDocument.Parse(first.Stdout.Split(
                Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]).RootElement;
            Guid observationUuid = firstReceipt.GetProperty("observationUuid").GetGuid();
            string beforeConflict = await RunMemCtlAsync(
                "capture", "receipt", observationUuid.ToString());

            string crlfFixture = fixture.Replace("\n", "\r\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(fixturePath, crlfFixture, new UTF8Encoding(false));
            var conflict = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(4, conflict.ExitCode);
            Assert.Empty(conflict.Stdout);
            Assert.Contains("verified_prefix_changed", conflict.Stderr);
            CaptureRuntimeStreamState stopped = Assert.Single(
                (await new FileCaptureRuntimeState(RuntimeStateDirectory(fixturePath))
                    .ReadAsync()).Streams);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.VerifiedPrefixChanged,
                    null),
                stopped.Stop);
            Assert.Equal(2, stopped.EnqueuedThrough);
            Assert.Empty(stopped.Queue);
            Assert.Equal(2, stopped.LastServerReceipt?.SourcePosition);
            Assert.Equal(
                beforeConflict,
                await RunMemCtlAsync("capture", "receipt", observationUuid.ToString()));
        }
        finally
        {
            File.Delete(fixturePath);
            DeleteRuntimeState(fixturePath);
        }
    }

    [Fact]
    public async Task PackagedTracerStopsAndBlocksLaterQueuedWorkBehindAnEarlierServerGap()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-runtime-gap-{Guid.NewGuid():N}", captureKey);
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-runtime-gap-{Guid.NewGuid():N}.jsonl");
        string stateDirectory = RuntimeStateDirectory(fixturePath);
        File.Copy(Path.Combine(_root, "fixtures/codex-synthetic.jsonl"), fixturePath);

        try
        {
            var state = new FileCaptureRuntimeState(stateDirectory);
            IReadOnlyList<CaptureRuntimeQueueItem> claims =
                await CodexCaptureClaimer.ClaimCompletedAsync(
                    new CodexJsonlAdapter(),
                    fixturePath,
                    "codex-synthetic-rollout-v1",
                    state,
                    new NeverStoreGate(Path.Combine(_root, "config/never_store.yaml")));
            Assert.Equal([0L, 1L, 2L], claims.Select(item => item.SourcePosition));
            CaptureRuntimeQueueItem first = claims[0];
            await state.RecordServerReceiptAsync(
                first.SourceStream,
                new CaptureServerReceiptState(
                    first.SourcePosition,
                    first.DeterministicLocatorEvidence.Identity,
                    "new",
                    Guid.NewGuid(),
                    Guid.NewGuid()));

            var conflict = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(4, conflict.ExitCode);
            Assert.Empty(conflict.Stdout);
            Assert.Contains("blocked_by_earlier_gap", conflict.Stderr);
            Assert.DoesNotContain(
                "response_item",
                conflict.Stderr,
                StringComparison.Ordinal);

            CaptureRuntimeSnapshot stopped = await state.ReadAsync();
            CaptureRuntimeStreamState stoppedStream = Assert.Single(stopped.Streams);
            Assert.Equal(
                new CaptureRuntimeStopState(
                    CaptureRuntimeStopCode.BlockedByEarlierGap,
                    1),
                stoppedStream.Stop);
            Assert.Equal([1L, 2L], stoppedStream.Queue.Select(item => item.SourcePosition));
            Assert.Equal(2, stoppedStream.EnqueuedThrough);
            Assert.Equal(0, stoppedStream.LastServerReceipt?.SourcePosition);

            var repeated = await RunEnabledTracerAsync(captureKey, fixturePath);
            Assert.Equal(4, repeated.ExitCode);
            Assert.Empty(repeated.Stdout);
            Assert.Contains("blocked_by_earlier_gap", repeated.Stderr);
            Assert.Equal(
                JsonSerializer.Serialize(stopped),
                JsonSerializer.Serialize(await state.ReadAsync()));
        }
        finally
        {
            File.Delete(fixturePath);
            DeleteRuntimeState(fixturePath);
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
        JsonElement gapReceipt = await gap.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "blocked_by_earlier_gap",
            gapReceipt.GetProperty("reason").GetString());
        Assert.Contains(
            "expected sourcePosition 1",
            gapReceipt.GetProperty("error").GetString());

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
    public async Task EstablishedStreamKeepsItsRouteWhileNewSessionsUseProspectivePolicy()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        string binding = $"codex-route-fixed-{Guid.NewGuid():N}";
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "faviann/*");
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                sourceSessionId,
                0,
                "route-0",
                "/workspace/project",
                [new { name = "origin", url = "https://github.com/faviann/overmind.git" }]));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstReceipt = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repo/faviann/overmind", firstReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("origin", firstReceipt.GetProperty("routeBasis").GetString());

        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--special-namespace", "home=homelab",
            "--directory-route", "/workspace=special:home");

        var second = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(sourceSessionId, 1, "route-1", "/workspace", []));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var receipt = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repo/faviann/overmind", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("established", receipt.GetProperty("routeBasis").GetString());

        var newSession = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(UniqueSession(), 0, "prospective-route-0", "/workspace/new", []));
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
        var newReceipt = await newSession.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("homelab", newReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("directory_mapping", newReceipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task OperatorPolicyRoutesNormalizedOriginToAnAllowedRepositoryNamespace()
    {
        string binding = $"codex-origin-route-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "faviann/*");
        using var client = CaptureClient(captureKey);

        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                sourceSessionId,
                0,
                "origin-route-0",
                "/workspace/elsewhere",
                [
                    new { name = "upstream", url = "https://github.com/other/project.git" },
                    new { name = "origin", url = "git@github.com:Faviann/Overmind.git" }
                ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repo/faviann/overmind", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("origin", receipt.GetProperty("routeBasis").GetString());
        string shown = await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString());
        var envelope = JsonDocument.Parse(shown).RootElement;
        Assert.Equal(
            "repo/faviann/overmind",
            envelope.GetProperty("event").GetProperty("namespace").GetString());
    }

    [Fact]
    public async Task RoutePolicyStoreCanonicalizesRoutingInputsForEveryCaller()
    {
        string binding = $"codex-store-canonicalization-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        await EnrollAsync(binding, captureKey);
        var options = RuntimeOptions();
        await new CaptureRoutePolicyStore(
                options.ConnectionString,
                new NeverStoreGate(
                    options.NeverStorePath, options.NeverStoreLiteralsPath))
            .ReplaceAsync(
                binding,
                new CaptureRoutingPolicy(
                    ["FAVIANN/*"],
                    [
                        new CaptureRouteOverride(
                            "git@GitHub.com:FAVIANN/OVERMIND.git",
                            "repo/FAVIANN/OVERMIND")
                    ],
                    [
                        new CaptureDirectoryRoute(
                            "/workspace/other/../Overmind/",
                            "repo/FAVIANN/OVERMIND")
                    ],
                    []));
        using var client = CaptureClient(captureKey);

        var overrideResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "canonical-override-0",
                "/elsewhere",
                [
                    new
                    {
                        name = "origin",
                        url = "https://github.com/faviann/overmind"
                    }
                ]));
        Assert.Equal(HttpStatusCode.OK, overrideResponse.StatusCode);
        var overrideReceipt = await overrideResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "repo/faviann/overmind",
            overrideReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("override", overrideReceipt.GetProperty("routeBasis").GetString());

        var directoryResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "canonical-directory-0",
                "/workspace/Overmind/src",
                []));
        Assert.Equal(HttpStatusCode.OK, directoryResponse.StatusCode);
        var directoryReceipt = await directoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "repo/faviann/overmind",
            directoryReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("directory_mapping", directoryReceipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task OperatorPolicyRejectsUnauthorizedRepositoryRouteTargetsAtomically()
    {
        string binding = $"codex-unauthorized-target-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "faviann/*");

        var result = await RunMemCtlForResultAsync(
            null,
            "capture", "route-policy", binding,
            "--allow-repository", "faviann/*",
            "--remote-override",
            "https://github.com/faviann/overmind.git=repo/OTHER/PROJECT",
            "--directory-route",
            "/workspace/overmind=repo/OTHER/PROJECT");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("outside the binding's allowed repository patterns", result.Stderr);

        using var client = CaptureClient(captureKey);
        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "unchanged-policy-0",
                "/workspace",
                [
                    new
                    {
                        name = "origin",
                        url = "https://github.com/faviann/overmind.git"
                    }
                ]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "repo/faviann/overmind",
            receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("origin", receipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task ExplicitRemoteOverridePrefersOriginAndPreservesOtherRemotesAsEvidence()
    {
        string binding = $"codex-override-route-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "other/*",
            "--special-namespace", "home=homelab",
            "--remote-override", "https://github.com/other/project.git=repo/other/project",
            "--remote-override", "git@github.com:Faviann/Overmind.git=special:home");
        using var client = CaptureClient(captureKey);

        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "override-route-0",
                "/workspace",
                [
                    new { name = "upstream", url = "https://github.com/other/project.git" },
                    new { name = "origin", url = "git@github.com:Faviann/Overmind.git" }
                ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("homelab", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("override", receipt.GetProperty("routeBasis").GetString());
        string shown = await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString());
        var remotes = JsonDocument.Parse(shown).RootElement
            .GetProperty("observation").GetProperty("routeEvidence")
            .GetProperty("remotes");
        Assert.Equal(["upstream", "origin"], remotes.EnumerateArray()
            .Select(remote => remote.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ExplicitNonOriginRemoteOverridesUseSourceEvidenceOrder()
    {
        string binding = $"codex-non-origin-override-order-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "other/*",
            "--special-namespace", "home=homelab",
            "--remote-override", "https://github.com/other/project.git=repo/other/project",
            "--remote-override", "git@github.com:Faviann/Overmind.git=special:home");
        using var client = CaptureClient(captureKey);

        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "non-origin-override-order-0",
                "/workspace",
                [
                    new { name = "z-first", url = "git@github.com:Faviann/Overmind.git" },
                    new { name = "a-second", url = "https://github.com/other/project.git" }
                ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("homelab", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("override", receipt.GetProperty("routeBasis").GetString());
        string shown = await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString());
        var remotes = JsonDocument.Parse(shown).RootElement
            .GetProperty("observation").GetProperty("routeEvidence")
            .GetProperty("remotes");
        Assert.Equal(["z-first", "a-second"], remotes.EnumerateArray()
            .Select(remote => remote.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task LongestDirectoryRouteWinsAndUnconfiguredNonOriginRemoteIsProvenanceOnly()
    {
        string binding = $"codex-directory-route-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "faviann/*",
            "--special-namespace", "home=homelab",
            "--directory-route", "/workspace=special:home",
            "--directory-route", "/workspace/overmind=repo/faviann/overmind");
        using var client = CaptureClient(captureKey);

        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "directory-route-0",
                "/workspace/overmind/src",
                [new { name = "upstream", url = "https://github.com/faviann/ignored.git" }]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repo/faviann/overmind", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("directory_mapping", receipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task RepositoryRoutingIsBindingScopedAndNamespaceEnsureIsIdempotent()
    {
        string allowedBinding = $"codex-repo-allowed-{Guid.NewGuid():N}";
        string deniedBinding = $"codex-repo-denied-{Guid.NewGuid():N}";
        var allowedKey = CaptureCredential();
        var deniedKey = CaptureCredential();
        await EnrollAsync(allowedBinding, allowedKey);
        await EnrollAsync(deniedBinding, deniedKey);
        await RunMemCtlAsync(
            "capture", "route-policy", allowedBinding,
            "--allow-repository", "faviann/*");
        await RunMemCtlAsync(
            "capture", "route-policy", deniedBinding,
            "--allow-repository", "other/*");
        object[] remotes =
            [new { name = "origin", url = "https://github.com/faviann/overmind.git" }];

        using var allowedClient = CaptureClient(allowedKey);
        foreach (string locator in new[] { "binding-allowed-0", "binding-allowed-1" })
        {
            var response = await allowedClient.PostAsJsonAsync(
                "/capture/v1/observations",
                RoutedObservation(UniqueSession(), 0, locator, "/workspace", remotes));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "repo/faviann/overmind",
                receipt.GetProperty("effectiveNamespace").GetString());
            Assert.Equal("origin", receipt.GetProperty("routeBasis").GetString());
        }

        using var deniedClient = CaptureClient(deniedKey);
        var denied = await deniedClient.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(), 0, "binding-denied-0", "/workspace", remotes));
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        var deniedReceipt = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "capture/unscoped",
            deniedReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("fallback", deniedReceipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task UnsafeRouteEvidenceCannotSelectANewRouteOrChangeAnEstablishedRoute()
    {
        string binding = $"codex-route-safety-{Guid.NewGuid():N}";
        string captureKey = CaptureCredential();
        string seededSyntheticSecret = "AKIA" + "SYNTHETICFIXTURE";
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--allow-repository", "faviann/*");
        using var client = CaptureClient(captureKey);

        var unsafeNewResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                UniqueSession(),
                0,
                "unsafe-new-route-0",
                "/workspace",
                [
                    new
                    {
                        name = "origin",
                        url = $"https://github.com/faviann/{seededSyntheticSecret}.git"
                    }
                ]));

        Assert.Equal(HttpStatusCode.OK, unsafeNewResponse.StatusCode);
        var unsafeNewReceipt =
            await unsafeNewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "capture/unscoped",
            unsafeNewReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("fallback", unsafeNewReceipt.GetProperty("routeBasis").GetString());
        string unsafeNewShown = await RunMemCtlAsync(
            "capture",
            "receipt",
            unsafeNewReceipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.DoesNotContain(seededSyntheticSecret, unsafeNewShown);
        Assert.Contains("[REDACTED:aws-access-key-id]", unsafeNewShown);

        string establishedSession = UniqueSession();
        var establishedResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                establishedSession,
                0,
                "established-route-0",
                "/workspace",
                [
                    new
                    {
                        name = "origin",
                        url = "https://github.com/faviann/overmind.git"
                    }
                ]));
        Assert.Equal(HttpStatusCode.OK, establishedResponse.StatusCode);
        var establishedReceipt =
            await establishedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "repo/faviann/overmind",
            establishedReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("origin", establishedReceipt.GetProperty("routeBasis").GetString());

        var unsafeEstablishedResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RoutedObservation(
                establishedSession,
                1,
                "established-route-1",
                "/workspace",
                [
                    new
                    {
                        name = "origin",
                        url = $"https://github.com/faviann/{seededSyntheticSecret}.git"
                    }
                ]));

        Assert.Equal(HttpStatusCode.OK, unsafeEstablishedResponse.StatusCode);
        var unsafeEstablishedReceipt =
            await unsafeEstablishedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "repo/faviann/overmind",
            unsafeEstablishedReceipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal(
            "established",
            unsafeEstablishedReceipt.GetProperty("routeBasis").GetString());
        string unsafeEstablishedShown = await RunMemCtlAsync(
            "capture",
            "receipt",
            unsafeEstablishedReceipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.DoesNotContain(seededSyntheticSecret, unsafeEstablishedShown);
        Assert.Contains("[REDACTED:aws-access-key-id]", unsafeEstablishedShown);
    }

    [Fact]
    public async Task PayloadNamespaceClaimsCannotExpandCaptureRoutingAuthority()
    {
        string binding = $"codex-namespace-claim-{Guid.NewGuid():N}";
        var captureKey = CaptureCredential();
        await EnrollAsync(binding, captureKey);
        using var client = CaptureClient(captureKey);

        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            NamespaceClaimObservation(UniqueSession(), "homelab", "memory-system"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("capture/unscoped", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("fallback", receipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task OperatorCannotPersistSyntheticSecretInCaptureRoutePolicy()
    {
        string binding = $"codex-policy-safety-{Guid.NewGuid():N}";
        string seededSyntheticSecret = "AKIA" + "SYNTHETICFIXTURE";
        await EnrollAsync(binding, CaptureCredential());

        var result = await RunMemCtlForResultAsync(
            null,
            "capture", "route-policy", binding,
            "--special-namespace", $"{seededSyntheticSecret}=homelab");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("never-store", result.Stderr);
        Assert.DoesNotContain(seededSyntheticSecret, result.Stderr);
    }

    [Fact]
    public async Task CaptureRoutePolicyHonorsOperatorProvisionedLiterals()
    {
        string binding = $"codex-policy-literal-safety-{Guid.NewGuid():N}";
        const string configuredLiteral = "synthetic-route-policy-literal-0001";
        string literalsPath = Path.Combine(
            Path.GetTempPath(), $"never-store-route-literals-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(literalsPath, configuredLiteral);
        await EnrollAsync(binding, CaptureCredential());

        try
        {
            var result = await RunMemCtlForResultAsync(
                new Dictionary<string, string>
                {
                    ["MEMSRV_NEVER_STORE_LITERALS_PATH"] = literalsPath
                },
                "capture", "route-policy", binding,
                "--special-namespace", $"{configuredLiteral}=homelab");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("never-store", result.Stderr);
            Assert.DoesNotContain(configuredLiteral, result.Stderr);
        }
        finally
        {
            File.Delete(literalsPath);
        }
    }

    [Theory]
    [InlineData("reserved=memory-system", "Reserved namespace")]
    [InlineData("reserved-family=capture/private", "Reserved namespace")]
    [InlineData("missing=does-not-exist", "must already exist")]
    [InlineData("repository=repo/faviann/overmind", "allowed repository pattern")]
    public async Task SpecialNamespacePolicyRejectsReservedOrUnprovisionedTargets(
        string mapping,
        string expectedError)
    {
        string binding = $"codex-special-denied-{Guid.NewGuid():N}";
        await EnrollAsync(binding, CaptureCredential());

        var result = await RunMemCtlForResultAsync(
            null,
            "capture", "route-policy", binding,
            "--special-namespace", mapping);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Stderr);
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
            "provider_token",
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

    private Task<(int ExitCode, string Stdout, string Stderr)> RunEnabledTracerAsync(
        string captureKey,
        string fixturePath) =>
        TestProcessRunner.RunCaptureTracerToExitAsync(
            new Dictionary<string, string>
            {
                ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                ["OVERMIND_CODEX_FIXTURE"] = fixturePath,
                ["OVERMIND_CAPTURE_STATE_DIR"] = RuntimeStateDirectory(fixturePath)
            });

    private async Task<JsonElement[]> CommitToolResultAndLoseResponseAsync(
        HttpListener listener, string captureKey)
    {
        var receipts = new List<JsonElement>();
        for (int position = 0; position < 3; position++)
        {
            HttpListenerContext context = await listener.GetContextAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            using var content = new StreamContent(context.Request.InputStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var client = CaptureClient(captureKey);
            using HttpResponseMessage response = await client.PostAsync(
                "/capture/v1/observations", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string responseBody = await response.Content.ReadAsStringAsync();
            receipts.Add(JsonDocument.Parse(responseBody).RootElement.Clone());

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            if (position < 2)
            {
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseBody);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes);
            }
            else
            {
                // The relationship-bearing tool result committed, but the
                // proxy returns no usable receipt.
                context.Response.ContentLength64 = 0;
            }
            context.Response.Close();
        }
        return [.. receipts];
    }

    private async Task ForwardFirstThenFailSecondAsync(
        HttpListener listener,
        string captureKey)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HttpListenerContext first = await listener.GetContextAsync()
            .WaitAsync(timeout.Token);
        using (var request = new StreamContent(first.Request.InputStream))
        {
            request.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var client = CaptureClient(captureKey);
            using HttpResponseMessage response = await client.PostAsync(
                "/capture/v1/observations", request, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            first.Response.StatusCode = (int)response.StatusCode;
            first.Response.ContentType = "application/json";
            first.Response.ContentLength64 = bytes.Length;
            await first.Response.OutputStream.WriteAsync(bytes, timeout.Token);
            first.Response.Close();
        }

        HttpListenerContext second = await listener.GetContextAsync()
            .WaitAsync(timeout.Token);
        second.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
        second.Response.ContentLength64 = 0;
        second.Response.Close();
    }

    private async Task<JsonElement> WithholdFirstAndForwardSecondAsync(
        HttpListener listener,
        string captureKey,
        TaskCompletionSource secondAttemptAccepted,
        TaskCompletionSource releaseSecondAttempt)
    {
        HttpListenerContext first = await listener.GetContextAsync()
            .WaitAsync(TimeSpan.FromSeconds(15));
        using (var reader = new StreamReader(
            first.Request.InputStream, Encoding.UTF8, leaveOpen: true))
        {
            await reader.ReadToEndAsync();
        }

        HttpListenerContext second = await listener.GetContextAsync()
            .WaitAsync(TimeSpan.FromSeconds(15));
        secondAttemptAccepted.SetResult();
        await releaseSecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15));

        using var content = new StreamContent(second.Request.InputStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var client = CaptureClient(captureKey);
        using HttpResponseMessage response = await client.PostAsync(
            "/capture/v1/observations", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();
        byte[] responseBytes = Encoding.UTF8.GetBytes(responseBody);
        second.Response.StatusCode = (int)HttpStatusCode.OK;
        second.Response.ContentType = "application/json";
        second.Response.ContentLength64 = responseBytes.Length;
        await second.Response.OutputStream.WriteAsync(responseBytes);
        second.Response.Close();
        first.Response.Abort();
        return JsonDocument.Parse(responseBody).RootElement.Clone();
    }

    private static JsonElement[] ParseReceiptLines(string stdout) =>
        stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static async Task<JsonElement> ReadTracerReceiptAsync(Process process)
    {
        string? line = await process.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(15));
        if (string.IsNullOrWhiteSpace(line))
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail(
                $"Scheduled capture tracer exited before a receipt (exit={process.ExitCode}).");
        }
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private async Task<CaptureLedgerMechanics> ReadCaptureLedgerMechanicsAsync(
        Guid sourceStreamUuid)
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        long observations = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM capture_observations WHERE stream_uuid = @sourceStreamUuid",
            new { sourceStreamUuid });
        long events = await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*)
            FROM captured_events e
            JOIN capture_observations o USING (observation_uuid)
            WHERE o.stream_uuid = @sourceStreamUuid
            """,
            new { sourceStreamUuid });
        long relationships = await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*)
            FROM captured_event_relationships r
            JOIN captured_events e ON e.trace_uuid = r.source_trace_uuid
            JOIN capture_observations o USING (observation_uuid)
            WHERE o.stream_uuid = @sourceStreamUuid
            """,
            new { sourceStreamUuid });
        long checkpoint = await connection.ExecuteScalarAsync<long>(
            """
            SELECT checkpoint_position
            FROM capture_source_streams
            WHERE stream_uuid = @sourceStreamUuid
            """,
            new { sourceStreamUuid });
        return new CaptureLedgerMechanics(
            observations, events, relationships, checkpoint);
    }

    private sealed record CaptureLedgerMechanics(
        long Observations,
        long Events,
        long Relationships,
        long Checkpoint);

    private static string RuntimeStateDirectory(string fixturePath) =>
        fixturePath + ".overmind-state";

    private static void DeleteRuntimeState(string fixturePath)
    {
        string directory = RuntimeStateDirectory(fixturePath);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
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

    private static object RoutedObservation(
        string sourceSessionId,
        long position,
        string nativeId,
        string workingDirectory,
        object[] remotes) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            routeEvidence = new { workingDirectory, remotes },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { message = "routed" },
            events = new[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    payload = new { text = "routed" }
                }
            }
        };

    private static object NamespaceClaimObservation(
        string sourceSessionId,
        string topLevelNamespace,
        string payloadNamespace) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = 0,
            @namespace = topLevelNamespace,
            locator = new { kind = "native_id", nativeId = $"namespace-claim-{Guid.NewGuid():N}" },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { @namespace = payloadNamespace },
            events = new[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    payload = new { text = "claim denied" }
                }
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
