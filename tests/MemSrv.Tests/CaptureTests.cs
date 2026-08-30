using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task CaptureApiAndOperatorReadPersistExplicitBinaryOmissionAndSafeEvidence()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        string locator = $"binary-media-{Guid.NewGuid():N}";
        await EnrollAsync($"binary-media-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);
        object BinaryObservation(int[] bytes) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId = locator },
            source = new
            {
                harness = "codex",
                harnessVersion = "0.146.synthetic",
                recordType = "response_item",
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = "9" },
            sourcePayload = new
            {
                payload = new
                {
                    type = "message",
                    content = new object[]
                    {
                        new
                        {
                            type = "binary_content",
                            category = "image",
                            media_type = "image/png",
                            source_path = "/workspace/screenshot.png",
                            source_identity = "image-api-1",
                            capture_provenance = new
                            {
                                origin = "authenticated-api",
                                nested = new
                                {
                                    type = "binary_content",
                                    category = "attachment",
                                    text = "Safe nested provenance text.",
                                    byte_payload = new[] { 201, 202 }
                                }
                            },
                            text = "Visible image alt text.",
                            byte_payload = bytes
                        }
                    }
                }
            },
            events = new object[]
            {
                new
                {
                    partKey = "content/0:opaque",
                    partOrder = 0,
                    kind = "opaque",
                    actor = "user",
                    payload = new
                    {
                        source = new
                        {
                            type = "binary_content",
                            category = "image",
                            text = "Visible image alt text.",
                            byte_payload = bytes
                        }
                    }
                }
            }
        };

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            BinaryObservation([137, 80, 78, 71]));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        JsonElement receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("new", receipt.GetProperty("status").GetString());
        JsonElement importOutcome = receipt.GetProperty("outcome");
        Assert.Equal("degraded", importOutcome.GetProperty("captureFidelity").GetString());
        JsonElement importCounter = importOutcome.GetProperty("counters")[0];
        Assert.Equal(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            importCounter.GetProperty("reason").GetString());
        Assert.Equal(
            CaptureSizeBand.UpTo1MiB,
            importCounter.GetProperty("sizeBand").GetString());
        Assert.Equal(3, importCounter.GetProperty("count").GetInt64());
        Assert.DoesNotContain(
            "originalByteCount",
            importOutcome.GetRawText(),
            StringComparison.Ordinal);
        JsonElement safeBlock = receipt.GetProperty("observation")
            .GetProperty("safeSourcePayload").GetProperty("payload")
            .GetProperty("content")[0];
        Assert.False(safeBlock.TryGetProperty("byte_payload", out _));
        Assert.Equal(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            safeBlock.GetProperty("capture_fidelity_omission")
                .GetProperty("reason").GetString());
        Assert.Equal(4, safeBlock.GetProperty("capture_fidelity_omission")
            .GetProperty("originalByteCount").GetInt64());
        JsonElement omissionIdentity = safeBlock
            .GetProperty("capture_fidelity_omission")
            .GetProperty("sourceIdentity");
        Assert.Equal(
            sourceSessionId,
            omissionIdentity.GetProperty("externalSessionId").GetString());
        Assert.Equal(0, omissionIdentity.GetProperty("sourcePosition").GetInt64());
        Assert.Equal(
            "native_id",
            omissionIdentity.GetProperty("locatorKind").GetString());
        Assert.Equal("Visible image alt text.", safeBlock.GetProperty("text").GetString());
        JsonElement safeNestedProvenance = safeBlock.GetProperty("capture_provenance")
            .GetProperty("nested");
        Assert.False(safeNestedProvenance.TryGetProperty("byte_payload", out _));
        Assert.Equal(
            "Safe nested provenance text.",
            safeNestedProvenance.GetProperty("text").GetString());
        Assert.Equal(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            safeNestedProvenance.GetProperty("capture_fidelity_omission")
                .GetProperty("reason").GetString());
        Assert.False(
            safeBlock.GetProperty("capture_fidelity_omission")
                .TryGetProperty("captureProvenance", out _));
        Assert.Contains(
            $"omission:{CaptureFidelityPolicy.UnsupportedBinaryReason}",
            receipt.GetProperty("observation").GetProperty("scan").GetProperty("ruleIds")
                .EnumerateArray().Select(item => item.GetString()));

        var retry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            BinaryObservation([137, 80, 78, 71]));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        JsonElement retryReceipt = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            receipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());

        var changedBytes = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            BinaryObservation([1, 2, 3, 4]));
        Assert.Equal(HttpStatusCode.Conflict, changedBytes.StatusCode);

        string shown = await RunMemCtlAsync(
            "capture",
            "receipt",
            receipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.DoesNotContain("\"byte_payload\"", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("[137,80,78,71]", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("[201,202]", shown, StringComparison.Ordinal);
        JsonElement envelope = JsonDocument.Parse(
            shown.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0])
            .RootElement;
        Assert.True(JsonElement.DeepEquals(
            importOutcome,
            envelope.GetProperty("outcome")));
        JsonElement replayed = envelope.GetProperty("observation")
            .GetProperty("safeSourcePayload").GetProperty("payload")
            .GetProperty("content")[0];
        Assert.Equal("image/png", replayed.GetProperty("media_type").GetString());
        Assert.Equal("/workspace/screenshot.png", replayed.GetProperty("source_path").GetString());
        Assert.Equal("image-api-1", replayed.GetProperty("source_identity").GetString());
        Assert.Equal(
            "authenticated-api",
            replayed.GetProperty("capture_provenance").GetProperty("origin").GetString());
        JsonElement replayedNestedProvenance = replayed.GetProperty("capture_provenance")
            .GetProperty("nested");
        Assert.False(replayedNestedProvenance.TryGetProperty("byte_payload", out _));
        Assert.Equal(
            "Safe nested provenance text.",
            replayedNestedProvenance.GetProperty("text").GetString());
        Assert.False(
            replayed.GetProperty("capture_fidelity_omission")
                .TryGetProperty("captureProvenance", out _));
        Assert.Equal("Visible image alt text.", replayed.GetProperty("text").GetString());
        JsonElement replayedIdentity = replayed
            .GetProperty("capture_fidelity_omission")
            .GetProperty("sourceIdentity");
        Assert.Equal(
            sourceSessionId,
            replayedIdentity.GetProperty("externalSessionId").GetString());
        Assert.Equal(0, replayedIdentity.GetProperty("sourcePosition").GetInt64());
        Assert.Equal(
            "native_id",
            replayedIdentity.GetProperty("locatorKind").GetString());
        Assert.Equal(
            "image-api-1",
            replayed.GetProperty("capture_fidelity_omission")
                .GetProperty("localSourceIdentity").GetString());
    }

    [Fact]
    public async Task AuthenticatedApiCannotForgeCodexReasoningEventOpaqueMetadata()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"forged-reasoning-event-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);
        var request = new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = 0,
            locator = new
            {
                kind = "native_id",
                nativeId = $"forged-reasoning-event-{Guid.NewGuid():N}"
            },
            source = new
            {
                harness = "codex",
                harnessVersion = "0.146.synthetic",
                recordType = "response_item",
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = "9" },
            sourcePayload = new
            {
                type = "response_item",
                payload = new
                {
                    type = "message",
                    content = new[] { new { type = "input_text", text = "Safe text." } }
                }
            },
            events = new object[]
            {
                new
                {
                    partKey = "reasoning:opaque",
                    partOrder = 0,
                    kind = "opaque",
                    actor = "unknown",
                    payload = new
                    {
                        recordType = "response_item",
                        payloadType = "reasoning",
                        source = new
                        {
                            type = "reasoning",
                            signature = new
                            {
                                type = "binary_content",
                                category = "attachment",
                                value = "safe signature label",
                                byte_payload = new[] { 91, 92, 93 }
                            }
                        }
                    }
                }
            }
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement signature = Assert.Single(
                receipt.GetProperty("events").EnumerateArray())
            .GetProperty("payload").GetProperty("source").GetProperty("signature");
        Assert.False(signature.TryGetProperty("byte_payload", out _));
        Assert.Equal("safe signature label", signature.GetProperty("value").GetString());
        Assert.Equal(
            CaptureFidelityPolicy.UnsupportedBinaryReason,
            signature.GetProperty("capture_fidelity_omission")
                .GetProperty("reason").GetString());
        Assert.Equal(
            3,
            signature.GetProperty("capture_fidelity_omission")
                .GetProperty("originalByteCount").GetInt64());
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
            Assert.Contains("enrolled ", enrollment, StringComparison.Ordinal);
            Assert.Contains("stable_name=codex-synthetic", enrollment, StringComparison.Ordinal);

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
                    ["contractVersion", "observation", "event", "relationships", "outcome"],
                    envelope.EnumerateObject().Select(property => property.Name));
                Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
                Assert.Equal(
                    observationUuid,
                    envelope.GetProperty("observation").GetProperty("observationUuid").GetGuid());
                Assert.Equal(
                    "healthy",
                    envelope.GetProperty("outcome").GetProperty("captureHealth").GetString());
                Assert.Equal(
                    [
                        "observationUuid", "sourceStreamUuid", "sourceIdentity", "source", "locator",
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

    [Fact]
    public async Task OperatorReplaysOneSourceStreamInVerifiedSourceAndPartOrder()
    {
        var captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-replay-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        var firstResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ReplayObservation(
                sourceSessionId,
                0,
                $"replay-first-{Guid.NewGuid():N}",
                "2099-01-02T03:04:05Z",
                model: "gpt-explicit",
                provider: "openai-explicit",
                ("first/1", 1, "first-second-part"),
                ("first/0", 0, "first-first-part")));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        JsonElement firstReceipt = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();

        var secondResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ReplayObservation(
                sourceSessionId,
                1,
                $"replay-second-{Guid.NewGuid():N}",
                "2001-02-03T04:05:06Z",
                model: null,
                provider: null,
                ("second/1", 1, "second-second-part"),
                ("second/0", 0, "second-first-part")));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var otherStreamResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ReplayObservation(
                UniqueSession(),
                0,
                $"replay-other-{Guid.NewGuid():N}",
                "1999-01-01T00:00:00Z",
                model: "other-model",
                provider: "other-provider",
                ("other/0", 0, "must-not-appear")));
        Assert.Equal(HttpStatusCode.OK, otherStreamResponse.StatusCode);

        Guid sourceStreamUuid = firstReceipt
            .GetProperty("observation")
            .GetProperty("sourceStreamUuid")
            .GetGuid();
        JsonElement replay = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "replay", sourceStreamUuid.ToString())).RootElement.Clone();

        Assert.Equal(
            ["contractVersion", "sourceStreamUuid", "orderBasis", "events"],
            replay.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, replay.GetProperty("contractVersion").GetInt32());
        Assert.Equal(sourceStreamUuid, replay.GetProperty("sourceStreamUuid").GetGuid());
        Assert.Equal(
            "capture_observations.source_position",
            replay.GetProperty("orderBasis").GetProperty("observation").GetString());
        Assert.Equal(
            "captured_events.part_order",
            replay.GetProperty("orderBasis").GetProperty("event").GetString());

        JsonElement[] replayed = replay.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(4, replayed.Length);
        Assert.Equal(
            [0L, 0L, 1L, 1L],
            replayed.Select(item => item.GetProperty("sourcePosition").GetInt64()));
        Assert.Equal(
            ["first-first-part", "first-second-part", "second-first-part", "second-second-part"],
            replayed.Select(item => item
                .GetProperty("envelope")
                .GetProperty("event")
                .GetProperty("payload")
                .GetProperty("text")
                .GetString()));
        Assert.DoesNotContain(
            replayed,
            item => item
                .GetProperty("envelope")
                .GetProperty("event")
                .GetProperty("payload")
                .GetProperty("text")
                .GetString() == "must-not-appear");

        JsonElement firstObservation = replayed[0]
            .GetProperty("envelope")
            .GetProperty("observation");
        Assert.Equal("codex", firstObservation.GetProperty("source").GetProperty("harness").GetString());
        Assert.Equal(
            "synthetic-replay",
            firstObservation.GetProperty("source").GetProperty("harnessVersion").GetString());
        Assert.Equal(
            "gpt-explicit",
            firstObservation.GetProperty("source").GetProperty("model").GetString());
        Assert.Equal(
            "openai-explicit",
            firstObservation.GetProperty("source").GetProperty("provider").GetString());
        Assert.Equal("2", firstObservation.GetProperty("adapter").GetProperty("version").GetString());
        Assert.Equal(
            "native_id",
            firstObservation.GetProperty("locator").GetProperty("kind").GetString());

        JsonElement secondObservation = replayed[2]
            .GetProperty("envelope")
            .GetProperty("observation");
        Assert.Equal(JsonValueKind.Null, secondObservation.GetProperty("source").GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, secondObservation.GetProperty("source").GetProperty("provider").ValueKind);
    }

    [Fact]
    public async Task AuthorizedOperatorNavigatesSourceStatedSessionRelationshipsAtReadTime()
    {
        string binding = $"codex-navigation-{Guid.NewGuid():N}";
        string captureKey = CaptureCredential();
        string externalSessionId = $"navigation-parent-{Guid.NewGuid():N}";
        string childId = $"navigation-child-{Guid.NewGuid():N}";
        await EnrollAsync(binding, captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage childResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                externalSessionId,
                childId,
                $"navigation-child-record-{Guid.NewGuid():N}",
                parentNativeId: externalSessionId,
                workingDirectory: null));
        childResponse.EnsureSuccessStatusCode();
        JsonElement childReceipt = await childResponse.Content.ReadFromJsonAsync<JsonElement>();
        Guid childStreamUuid = childReceipt.GetProperty("observation")
            .GetProperty("sourceStreamUuid").GetGuid();
        Guid childObservationUuid = childReceipt.GetProperty("observationUuid").GetGuid();

        JsonElement dangling = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", childStreamUuid.ToString(),
            "--namespace", "capture/unscoped")).RootElement;
        Assert.Equal(
            ["contractVersion", "session", "relationships"],
            dangling.EnumerateObject().Select(property => property.Name));
        Assert.Equal("capture/unscoped", dangling.GetProperty("session")
            .GetProperty("namespace").GetString());
        JsonElement danglingEdge = Assert.Single(
            dangling.GetProperty("relationships").EnumerateArray());
        Assert.Equal(
            ["direction", "availability", "evidence", "session"],
            danglingEdge.EnumerateObject().Select(property => property.Name));
        Assert.Equal("outgoing", danglingEdge.GetProperty("direction").GetString());
        Assert.Equal("unavailable", danglingEdge.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, danglingEdge.GetProperty("session").ValueKind);
        Assert.Equal(
            ["relationshipType", "sourceTraceUuid", "sourceStreamUuid",
                "targetSourceStreamUuid", "targetNativeId", "targetKind"],
            danglingEdge.GetProperty("evidence")
                .EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            "parent_session",
            danglingEdge.GetProperty("evidence").GetProperty("relationshipType").GetString());
        Assert.Equal(
            childStreamUuid,
            danglingEdge.GetProperty("evidence").GetProperty("sourceStreamUuid").GetGuid());
        Assert.Equal(
            externalSessionId,
            danglingEdge.GetProperty("evidence").GetProperty("targetNativeId").GetString());
        Assert.Equal(
            "session",
            danglingEdge.GetProperty("evidence").GetProperty("targetKind").GetString());
        Assert.DoesNotContain(
            danglingEdge.EnumerateObject(),
            property => property.Name.Contains("confidence", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("order", StringComparison.OrdinalIgnoreCase));

        string canonicalBeforeParent = await RunMemCtlAsync(
            "capture", "receipt", childObservationUuid.ToString());

        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--special-namespace", "home=homelab",
            "--directory-route", "/workspace=special:home");
        using HttpResponseMessage parentResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                externalSessionId,
                childId: null,
                $"navigation-parent-record-{Guid.NewGuid():N}",
                parentNativeId: null,
                workingDirectory: "/workspace/parent"));
        parentResponse.EnsureSuccessStatusCode();
        JsonElement parentReceipt = await parentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Guid parentStreamUuid = parentReceipt.GetProperty("observation")
            .GetProperty("sourceStreamUuid").GetGuid();

        JsonElement authorityHidden = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", childStreamUuid.ToString(),
            "--namespace", "capture/unscoped")).RootElement;
        Assert.Equal(
            danglingEdge.GetRawText(),
            Assert.Single(authorityHidden.GetProperty("relationships").EnumerateArray())
                .GetRawText());
        Assert.Equal(
            canonicalBeforeParent,
            await RunMemCtlAsync("capture", "receipt", childObservationUuid.ToString()));

        JsonElement childNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", childStreamUuid.ToString(),
            "--namespace", "capture/unscoped",
            "--namespace", "homelab")).RootElement;
        JsonElement parentEdge = Assert.Single(
            childNavigation.GetProperty("relationships").EnumerateArray());
        Assert.Equal("available", parentEdge.GetProperty("availability").GetString());
        Assert.Equal(
            parentStreamUuid,
            parentEdge.GetProperty("session").GetProperty("sourceStreamUuid").GetGuid());
        Assert.Equal(
            "homelab",
            parentEdge.GetProperty("session").GetProperty("namespace").GetString());

        JsonElement parentNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", parentStreamUuid.ToString(),
            "--namespace", "capture/unscoped",
            "--namespace", "homelab")).RootElement;
        JsonElement childEdge = Assert.Single(
            parentNavigation.GetProperty("relationships").EnumerateArray());
        Assert.Equal("incoming", childEdge.GetProperty("direction").GetString());
        Assert.Equal("available", childEdge.GetProperty("availability").GetString());
        Assert.Equal(
            childStreamUuid,
            childEdge.GetProperty("session").GetProperty("sourceStreamUuid").GetGuid());

        var missingAuthority = await RunMemCtlForResultAsync(
            null, "capture", "navigate", childStreamUuid.ToString());
        Assert.NotEqual(0, missingAuthority.ExitCode);
        Assert.Contains("--namespace is required", missingAuthority.Stderr);

        var disallowedStart = await RunMemCtlForResultAsync(
            null,
            "capture", "navigate", parentStreamUuid.ToString(),
            "--namespace", "capture/unscoped");
        var unknownStart = await RunMemCtlForResultAsync(
            null,
            "capture", "navigate", Guid.NewGuid().ToString(),
            "--namespace", "capture/unscoped");
        Assert.Equal(disallowedStart.ExitCode, unknownStart.ExitCode);
        Assert.Equal(disallowedStart.Stdout, unknownStart.Stdout);
        Assert.Equal(disallowedStart.Stderr, unknownStart.Stderr);

        using HttpResponseMessage rootResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                $"navigation-root-{Guid.NewGuid():N}",
                childId: null,
                $"navigation-root-record-{Guid.NewGuid():N}",
                parentNativeId: null,
                workingDirectory: "/workspace/root"));
        rootResponse.EnsureSuccessStatusCode();
        JsonElement rootReceipt = await rootResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement rootNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate",
            rootReceipt.GetProperty("observation").GetProperty("sourceStreamUuid")
                .GetGuid().ToString(),
            "--namespace", "homelab")).RootElement;
        Assert.Empty(rootNavigation.GetProperty("relationships").EnumerateArray());

        string ambiguousNativeId = $"navigation-ambiguous-{Guid.NewGuid():N}";
        var candidatePrivateMarkers = new List<string>();
        foreach (var candidate in new[]
        {
            (ExternalSessionId: ambiguousNativeId, ChildId: (string?)null,
                Content: $"navigation-candidate-content-{Guid.NewGuid():N}"),
            (ExternalSessionId: $"other-family-{Guid.NewGuid():N}",
                ChildId: (string?)ambiguousNativeId,
                Content: $"navigation-candidate-content-{Guid.NewGuid():N}")
        })
        {
            using HttpResponseMessage candidateResponse = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                SessionRelationshipObservation(
                    candidate.ExternalSessionId,
                    candidate.ChildId,
                    candidate.Content,
                    parentNativeId: null,
                    workingDirectory: "/workspace/candidate"));
            candidateResponse.EnsureSuccessStatusCode();
            JsonElement candidateReceipt =
                await candidateResponse.Content.ReadFromJsonAsync<JsonElement>();
            candidatePrivateMarkers.AddRange(
            [
                candidateReceipt.GetProperty("observationUuid").GetGuid().ToString(),
                candidateReceipt.GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid().ToString(),
                candidateReceipt.GetProperty("events")[0]
                    .GetProperty("sessionId").GetString()!,
                candidateReceipt.GetProperty("effectiveNamespace").GetString()!,
                candidate.Content
            ]);
            if (!string.Equals(
                candidate.ExternalSessionId, ambiguousNativeId, StringComparison.Ordinal))
            {
                candidatePrivateMarkers.Add(candidate.ExternalSessionId);
            }
        }

        using HttpResponseMessage ambiguousSourceResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                $"navigation-source-family-{Guid.NewGuid():N}",
                $"navigation-source-child-{Guid.NewGuid():N}",
                $"navigation-ambiguous-source-{Guid.NewGuid():N}",
                parentNativeId: ambiguousNativeId,
                workingDirectory: null));
        ambiguousSourceResponse.EnsureSuccessStatusCode();
        JsonElement ambiguousSourceReceipt =
            await ambiguousSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement ambiguousNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate",
            ambiguousSourceReceipt.GetProperty("observation").GetProperty("sourceStreamUuid")
                .GetGuid().ToString(),
            "--namespace", "capture/unscoped",
            "--namespace", "homelab")).RootElement;
        JsonElement ambiguousEdge = Assert.Single(
            ambiguousNavigation.GetProperty("relationships").EnumerateArray());
        Assert.Equal("unavailable", ambiguousEdge.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, ambiguousEdge.GetProperty("session").ValueKind);
        Assert.Equal(
            ambiguousNativeId,
            ambiguousEdge.GetProperty("evidence").GetProperty("targetNativeId").GetString());
        string ambiguousOutput = ambiguousNavigation.GetRawText();
        Assert.All(
            candidatePrivateMarkers.Distinct(StringComparer.Ordinal),
            marker => Assert.DoesNotContain(
                marker, ambiguousOutput, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthorizedOperatorNavigatesEveryStoredSessionRelationshipInBothDirections()
    {
        string binding = $"codex-navigation-complete-{Guid.NewGuid():N}";
        string captureKey = CaptureCredential();
        string parentNativeId = $"navigation-parent-{Guid.NewGuid():N}";
        await EnrollAsync(binding, captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage parentResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                parentNativeId,
                childId: null,
                $"navigation-parent-record-{Guid.NewGuid():N}",
                parentNativeId: null,
                workingDirectory: null));
        parentResponse.EnsureSuccessStatusCode();
        JsonElement parentReceipt = await parentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Guid parentStreamUuid = parentReceipt.GetProperty("observation")
            .GetProperty("sourceStreamUuid").GetGuid();

        string[] relationshipTypes = ["parent_session", "spawned_by", "forked_from"];
        var relatedStreams = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (string relationshipType in relationshipTypes)
        {
            using HttpResponseMessage relatedResponse = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                SessionRelationshipObservation(
                    $"navigation-source-{relationshipType}-{Guid.NewGuid():N}",
                    $"navigation-child-{relationshipType}-{Guid.NewGuid():N}",
                    $"navigation-record-{relationshipType}-{Guid.NewGuid():N}",
                    parentNativeId,
                    workingDirectory: null,
                    relationshipType: relationshipType));
            relatedResponse.EnsureSuccessStatusCode();
            JsonElement relatedReceipt =
                await relatedResponse.Content.ReadFromJsonAsync<JsonElement>();
            relatedStreams.Add(
                relationshipType,
                relatedReceipt.GetProperty("observation")
                    .GetProperty("sourceStreamUuid").GetGuid());
        }

        JsonElement parentNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", parentStreamUuid.ToString(),
            "--namespace", "capture/unscoped")).RootElement;
        Dictionary<string, JsonElement> incomingByType = parentNavigation
            .GetProperty("relationships")
            .EnumerateArray()
            .ToDictionary(
                edge => edge.GetProperty("evidence")
                    .GetProperty("relationshipType").GetString()!,
                edge => edge.Clone(),
                StringComparer.Ordinal);
        Assert.Equal(relationshipTypes.Order(), incomingByType.Keys.Order());
        foreach ((string relationshipType, Guid relatedStreamUuid) in relatedStreams)
        {
            JsonElement incoming = incomingByType[relationshipType];
            Assert.Equal("incoming", incoming.GetProperty("direction").GetString());
            Assert.Equal("available", incoming.GetProperty("availability").GetString());
            Assert.Equal(
                relatedStreamUuid,
                incoming.GetProperty("evidence").GetProperty("sourceStreamUuid").GetGuid());
            Assert.Equal(
                relatedStreamUuid,
                incoming.GetProperty("session").GetProperty("sourceStreamUuid").GetGuid());

            JsonElement reverse = JsonDocument.Parse(await RunMemCtlAsync(
                "capture", "navigate", relatedStreamUuid.ToString(),
                "--namespace", "capture/unscoped")).RootElement;
            JsonElement outgoing = Assert.Single(
                reverse.GetProperty("relationships").EnumerateArray());
            Assert.Equal("outgoing", outgoing.GetProperty("direction").GetString());
            Assert.Equal("available", outgoing.GetProperty("availability").GetString());
            Assert.Equal(
                relationshipType,
                outgoing.GetProperty("evidence").GetProperty("relationshipType").GetString());
            Assert.Equal(
                parentNativeId,
                outgoing.GetProperty("evidence").GetProperty("targetNativeId").GetString());
            Assert.Equal(
                parentStreamUuid,
                outgoing.GetProperty("session").GetProperty("sourceStreamUuid").GetGuid());
        }

        string classificationNativeId = $"navigation-classification-{Guid.NewGuid():N}";
        string toolCallNativeId = $"navigation-tool-call-{Guid.NewGuid():N}";
        using HttpResponseMessage filteredResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                $"navigation-filtered-source-{Guid.NewGuid():N}",
                $"navigation-filtered-child-{Guid.NewGuid():N}",
                $"navigation-filtered-record-{Guid.NewGuid():N}",
                parentNativeId,
                workingDirectory: null,
                additionalRelationships:
                [
                    ("source_classification", classificationNativeId, "session"),
                    ("spawned_by", toolCallNativeId, "tool_call")
                ]));
        filteredResponse.EnsureSuccessStatusCode();
        JsonElement filteredReceipt =
            await filteredResponse.Content.ReadFromJsonAsync<JsonElement>();
        Guid filteredObservationUuid = filteredReceipt.GetProperty("observationUuid").GetGuid();
        string canonicalReceiptBeforeNavigation = await RunMemCtlAsync(
            "capture", "receipt", filteredObservationUuid.ToString());
        JsonElement canonicalEnvelope =
            JsonDocument.Parse(canonicalReceiptBeforeNavigation).RootElement;
        Assert.Equal(
            [
                ("parent_session", "session", parentNativeId),
                ("source_classification", "session", classificationNativeId),
                ("spawned_by", "tool_call", toolCallNativeId)
            ],
            canonicalEnvelope.GetProperty("relationships").EnumerateArray().Select(
                relationship => (
                    relationship.GetProperty("type").GetString()!,
                    relationship.GetProperty("target").GetProperty("kind").GetString()!,
                    relationship.GetProperty("target").GetProperty("nativeId").GetString()!
                )));
        JsonElement filteredNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate",
            filteredReceipt.GetProperty("observation")
                .GetProperty("sourceStreamUuid").GetGuid().ToString(),
            "--namespace", "capture/unscoped")).RootElement;
        JsonElement permittedEdge = Assert.Single(
            filteredNavigation.GetProperty("relationships").EnumerateArray());
        Assert.Equal(
            "parent_session",
            permittedEdge.GetProperty("evidence").GetProperty("relationshipType").GetString());
        Assert.Equal(
            "session",
            permittedEdge.GetProperty("evidence").GetProperty("targetKind").GetString());
        Assert.Equal("available", permittedEdge.GetProperty("availability").GetString());
        Assert.Equal(
            parentStreamUuid,
            permittedEdge.GetProperty("session").GetProperty("sourceStreamUuid").GetGuid());
        Assert.Equal(
            canonicalReceiptBeforeNavigation,
            await RunMemCtlAsync(
                "capture", "receipt", filteredObservationUuid.ToString()));
    }

    [Fact]
    public async Task UnavailableExplicitRelationshipTargetDoesNotLeakThroughPackagedNavigation()
    {
        string binding = $"codex-explicit-navigation-{Guid.NewGuid():N}";
        string captureKey = CaptureCredential();
        string targetSessionId = $"explicit-target-session-{Guid.NewGuid():N}";
        string targetContent = $"explicit-target-content-{Guid.NewGuid():N}";
        string safeNativeTargetId = $"source-stated-target-{Guid.NewGuid():N}";
        await EnrollAsync(binding, captureKey);
        await RunMemCtlAsync(
            "capture", "route-policy", binding,
            "--special-namespace", "home=homelab",
            "--directory-route", "/workspace=special:home");
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage targetResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                targetSessionId,
                childId: null,
                targetContent,
                parentNativeId: null,
                workingDirectory: "/workspace/target"));
        targetResponse.EnsureSuccessStatusCode();
        JsonElement targetReceipt = await targetResponse.Content.ReadFromJsonAsync<JsonElement>();
        Guid targetStreamUuid = targetReceipt.GetProperty("observation")
            .GetProperty("sourceStreamUuid").GetGuid();

        using HttpResponseMessage explicitSourceResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                $"explicit-source-session-{Guid.NewGuid():N}",
                childId: null,
                $"explicit-source-record-{Guid.NewGuid():N}",
                parentNativeId: safeNativeTargetId,
                workingDirectory: null,
                targetSourceStreamUuid: targetStreamUuid));
        explicitSourceResponse.EnsureSuccessStatusCode();
        JsonElement explicitSourceReceipt =
            await explicitSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Guid explicitSourceStreamUuid = explicitSourceReceipt.GetProperty("observation")
            .GetProperty("sourceStreamUuid").GetGuid();

        string canonicalReceipt = await RunMemCtlAsync(
            "capture", "receipt",
            explicitSourceReceipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.Contains(
            targetStreamUuid.ToString(),
            canonicalReceipt,
            StringComparison.OrdinalIgnoreCase);

        JsonElement explicitNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", explicitSourceStreamUuid.ToString(),
            "--namespace", "capture/unscoped")).RootElement;
        JsonElement explicitEdge = Assert.Single(
            explicitNavigation.GetProperty("relationships").EnumerateArray());
        JsonElement explicitEvidence = explicitEdge.GetProperty("evidence");
        Assert.Equal("unavailable", explicitEdge.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, explicitEdge.GetProperty("session").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            explicitEvidence.GetProperty("targetSourceStreamUuid").ValueKind);
        Assert.Equal(
            "parent_session",
            explicitEvidence.GetProperty("relationshipType").GetString());
        Assert.Equal(
            explicitSourceStreamUuid,
            explicitEvidence.GetProperty("sourceStreamUuid").GetGuid());
        Assert.NotEqual(
            targetStreamUuid,
            explicitEvidence.GetProperty("sourceTraceUuid").GetGuid());
        Assert.Equal(
            safeNativeTargetId,
            explicitEvidence.GetProperty("targetNativeId").GetString());
        Assert.Equal("session", explicitEvidence.GetProperty("targetKind").GetString());
        Assert.DoesNotContain(
            targetStreamUuid.ToString(),
            explicitNavigation.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("homelab", explicitNavigation.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            targetSessionId,
            explicitNavigation.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(targetContent, explicitNavigation.GetRawText(), StringComparison.Ordinal);

        using HttpResponseMessage nativeSourceResponse = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SessionRelationshipObservation(
                $"native-source-session-{Guid.NewGuid():N}",
                childId: null,
                $"native-source-record-{Guid.NewGuid():N}",
                parentNativeId: safeNativeTargetId,
                workingDirectory: null));
        nativeSourceResponse.EnsureSuccessStatusCode();
        JsonElement nativeSourceReceipt =
            await nativeSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement nativeNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate",
            nativeSourceReceipt.GetProperty("observation")
                .GetProperty("sourceStreamUuid").GetGuid().ToString(),
            "--namespace", "capture/unscoped")).RootElement;
        JsonElement nativeEdge = Assert.Single(
            nativeNavigation.GetProperty("relationships").EnumerateArray());
        JsonElement nativeEvidence = nativeEdge.GetProperty("evidence");
        Assert.Equal(
            nativeEdge.GetProperty("availability").GetRawText(),
            explicitEdge.GetProperty("availability").GetRawText());
        Assert.Equal(
            nativeEdge.GetProperty("session").GetRawText(),
            explicitEdge.GetProperty("session").GetRawText());
        Assert.Equal(
            nativeEvidence.GetProperty("targetSourceStreamUuid").GetRawText(),
            explicitEvidence.GetProperty("targetSourceStreamUuid").GetRawText());
        Assert.Equal(
            nativeEvidence.GetProperty("relationshipType").GetRawText(),
            explicitEvidence.GetProperty("relationshipType").GetRawText());
        Assert.Equal(
            nativeEvidence.GetProperty("targetNativeId").GetRawText(),
            explicitEvidence.GetProperty("targetNativeId").GetRawText());
        Assert.Equal(
            nativeEvidence.GetProperty("targetKind").GetRawText(),
            explicitEvidence.GetProperty("targetKind").GetRawText());

        JsonElement authorizedNavigation = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "navigate", explicitSourceStreamUuid.ToString(),
            "--namespace", "capture/unscoped",
            "--namespace", "homelab")).RootElement;
        JsonElement authorizedEdge = Assert.Single(
            authorizedNavigation.GetProperty("relationships").EnumerateArray());
        Assert.Equal("available", authorizedEdge.GetProperty("availability").GetString());
        Assert.Equal(
            targetStreamUuid,
            authorizedEdge.GetProperty("evidence")
                .GetProperty("targetSourceStreamUuid").GetGuid());
        Assert.Equal(
            targetStreamUuid,
            authorizedEdge.GetProperty("session").GetProperty("sourceStreamUuid").GetGuid());
        Assert.Equal(
            "homelab",
            authorizedEdge.GetProperty("session").GetProperty("namespace").GetString());
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
    public async Task ExplicitChildIdentityIsCanonicalOnHttpAndMemCtlAndRejectsAContradictoryLegacyClaim()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        await EnrollAsync($"codex-explicit-identity-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        var contradictory = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                "different-legacy-session",
                externalSessionId,
                childId,
                0,
                $"identity-{Guid.NewGuid():N}",
                "4",
                null,
                "canonical"));
        Assert.Equal(HttpStatusCode.BadRequest, contradictory.StatusCode);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                null,
                externalSessionId,
                childId,
                0,
                $"identity-{Guid.NewGuid():N}",
                "6",
                "0.144.synthetic",
                "canonical"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            externalSessionId,
            receipt.GetProperty("observation")
                .GetProperty("sourceIdentity").GetProperty("externalSessionId").GetString());
        Assert.Equal(
            childId,
            receipt.GetProperty("observation")
                .GetProperty("sourceIdentity").GetProperty("childId").GetString());

        var envelope = JsonDocument.Parse(await RunMemCtlAsync(
            "capture", "receipt",
            receipt.GetProperty("observationUuid").GetGuid().ToString())).RootElement;
        Assert.Equal(
            externalSessionId,
            envelope.GetProperty("observation")
                .GetProperty("sourceIdentity").GetProperty("externalSessionId").GetString());
        Assert.Equal(
            childId,
            envelope.GetProperty("observation")
                .GetProperty("sourceIdentity").GetProperty("childId").GetString());
    }

    [Fact]
    public async Task CodexAdapterUpgradeRetryConvergesButChangedSourceContentStillConflicts()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-upgrade-{Guid.NewGuid():N}";
        await EnrollAsync($"codex-adapter-upgrade-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "7",
                "0.144.synthetic",
                "same source record"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedReceipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();

        var falseProvenanceRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "8",
                "false-version",
                "same source record"));
        Assert.Equal(HttpStatusCode.Conflict, falseProvenanceRetry.StatusCode);

        var upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "8",
                "0.144.synthetic",
                "same source record"));
        Assert.Equal(HttpStatusCode.OK, upgradedRetry.StatusCode);
        var retryReceipt = await upgradedRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            acceptedReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());

        var changedSource = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "8",
                "0.144.synthetic",
                "changed source record"));
        Assert.Equal(HttpStatusCode.Conflict, changedSource.StatusCode);
    }

    [Fact]
    public async Task CodexAdapterVersionNineConvergesForAnUnchangedVersionEightRecord()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-v9-upgrade-{Guid.NewGuid():N}";
        await EnrollAsync($"codex-adapter-v9-upgrade-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "8",
                "0.144.synthetic",
                "unchanged source record"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        JsonElement acceptedReceipt =
            await accepted.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement acceptedOutcome = acceptedReceipt.GetProperty("outcome");
        Assert.Equal("complete", acceptedOutcome.GetProperty("captureFidelity").GetString());
        Assert.Empty(acceptedOutcome.GetProperty("counters").EnumerateArray());

        using HttpResponseMessage upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "9",
                "0.144.synthetic",
                "unchanged source record"));

        Assert.Equal(HttpStatusCode.OK, upgradedRetry.StatusCode);
        JsonElement retryReceipt =
            await upgradedRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            acceptedReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());
        Assert.True(JsonElement.DeepEquals(
            acceptedOutcome,
            retryReceipt.GetProperty("outcome")));
        JsonElement operatorEnvelope = JsonDocument.Parse(await RunMemCtlAsync(
            "capture",
            "receipt",
            acceptedReceipt.GetProperty("observationUuid").GetGuid().ToString()))
            .RootElement;
        Assert.True(JsonElement.DeepEquals(
            acceptedOutcome,
            operatorEnvelope.GetProperty("outcome")));
    }

    [Fact]
    public async Task CodexAdapterVersionTenConvergesForAnUnchangedVersionNineRecord()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-v10-upgrade-{Guid.NewGuid():N}";
        await EnrollAsync($"codex-adapter-v10-upgrade-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "9",
                "0.144.synthetic",
                "unchanged source record"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        JsonElement acceptedReceipt =
            await accepted.Content.ReadFromJsonAsync<JsonElement>();

        using HttpResponseMessage upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "10",
                "0.144.synthetic",
                "unchanged source record"));

        Assert.Equal(HttpStatusCode.OK, upgradedRetry.StatusCode);
        JsonElement retryReceipt =
            await upgradedRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            acceptedReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());
    }

    [Theory]
    [InlineData("malformed_json")]
    [InlineData("source_record_omission")]
    public async Task VersionTenConvergesForStructuredSourceOwnedTerminalDiscriminator(
        string recordType)
    {
        string captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-v10-source-owned-{Guid.NewGuid():N}";
        await EnrollAsync(
            $"codex-adapter-v10-source-owned-{Guid.NewGuid():N}",
            captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            StructuredSourceOwnedTerminalDiscriminatorObservation(
                externalSessionId,
                childId,
                locator,
                adapterVersion: "9",
                recordType));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        JsonElement acceptedReceipt =
            await accepted.Content.ReadFromJsonAsync<JsonElement>();

        using HttpResponseMessage upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            StructuredSourceOwnedTerminalDiscriminatorObservation(
                externalSessionId,
                childId,
                locator,
                adapterVersion: "10",
                recordType));

        Assert.Equal(HttpStatusCode.OK, upgradedRetry.StatusCode);
        JsonElement retryReceipt =
            await upgradedRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            acceptedReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());
    }

    [Theory]
    [InlineData("malformed_json")]
    [InlineData("source_record_omission")]
    public async Task VersionTenTerminalMalformedRepresentationsCannotMasqueradeAsVersionNine(
        string recordType)
    {
        string captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string locator = $"adapter-v10-terminal-malformed-{Guid.NewGuid():N}";
        await EnrollAsync(
            $"codex-adapter-v10-terminal-malformed-{Guid.NewGuid():N}",
            captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            TerminalMalformedRepresentationObservation(
                externalSessionId,
                locator,
                adapterVersion: "9",
                recordType));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using HttpResponseMessage masqueradingRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            TerminalMalformedRepresentationObservation(
                externalSessionId,
                locator,
                adapterVersion: "10",
                recordType));

        Assert.Equal(HttpStatusCode.Conflict, masqueradingRetry.StatusCode);
    }

    [Fact]
    public async Task VersionTenExactParseErrorEnvelopeCannotConvergeAsVersionNineEvenWhenOpaqueTextIsValidJson()
    {
        string captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string locator = $"adapter-v10-valid-json-lookalike-{Guid.NewGuid():N}";
        await EnrollAsync(
            $"codex-adapter-v10-valid-json-lookalike-{Guid.NewGuid():N}",
            captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            TerminalMalformedRepresentationObservation(
                externalSessionId,
                locator,
                adapterVersion: "9",
                recordType: "malformed_json",
                opaqueText: "{}"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using HttpResponseMessage upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            TerminalMalformedRepresentationObservation(
                externalSessionId,
                locator,
                adapterVersion: "10",
                recordType: "malformed_json",
                opaqueText: "{}"));

        Assert.Equal(HttpStatusCode.Conflict, upgradedRetry.StatusCode);
    }

    [Fact]
    public async Task VersionTenUnsupportedBinaryFidelityCannotMasqueradeAsVersionNine()
    {
        string captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string locator = $"adapter-v10-binary-{Guid.NewGuid():N}";
        await EnrollAsync($"codex-adapter-v10-binary-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RawBinaryContentObservation(
                externalSessionId,
                locator,
                adapterVersion: "9"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using HttpResponseMessage masqueradingRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RawBinaryContentObservation(
                externalSessionId,
                locator,
                adapterVersion: "10"));

        Assert.Equal(HttpStatusCode.Conflict, masqueradingRetry.StatusCode);
    }

    [Fact]
    public async Task VersionNineSourceOwnedBinaryOmissionLookalikeStillConvergesFromVersionEight()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-v9-source-lookalike-{Guid.NewGuid():N}";
        await EnrollAsync(
            $"codex-adapter-v9-source-lookalike-{Guid.NewGuid():N}",
            captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SourceOwnedBinaryOmissionLookalikeObservation(
                externalSessionId,
                childId,
                locator,
                adapterVersion: "8"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        JsonElement acceptedReceipt =
            await accepted.Content.ReadFromJsonAsync<JsonElement>();

        using HttpResponseMessage upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            SourceOwnedBinaryOmissionLookalikeObservation(
                externalSessionId,
                childId,
                locator,
                adapterVersion: "9"));

        Assert.Equal(HttpStatusCode.OK, upgradedRetry.StatusCode);
        JsonElement retryReceipt =
            await upgradedRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            acceptedReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());
    }

    [Fact]
    public async Task VersionNineRawBinaryContentCannotMasqueradeAsVersionEight()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string locator = $"adapter-v9-binary-{Guid.NewGuid():N}";
        await EnrollAsync($"codex-adapter-v9-binary-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RawBinaryContentObservation(
                externalSessionId,
                locator,
                adapterVersion: "8"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using HttpResponseMessage masqueradingRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            RawBinaryContentObservation(
                externalSessionId,
                locator,
                adapterVersion: "9"));

        Assert.Equal(HttpStatusCode.Conflict, masqueradingRetry.StatusCode);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    public async Task CodexAdapterUpgradeRetryConvergesFromEveryPreUpgradeAdapterVersion(
        string preUpgradeAdapterVersion)
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-upgrade-version-{Guid.NewGuid():N}";
        await EnrollAsync(
            $"codex-adapter-upgrade-version-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                preUpgradeAdapterVersion,
                "0.144.synthetic",
                "same source record"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedReceipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();

        var upgradedRetry = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ExplicitIdentityObservation(
                externalSessionId,
                externalSessionId,
                childId,
                0,
                locator,
                "7",
                "0.144.synthetic",
                "same source record"));

        Assert.Equal(HttpStatusCode.OK, upgradedRetry.StatusCode);
        var retryReceipt = await upgradedRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            acceptedReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());
    }

    [Fact]
    public async Task CodexAdapterUpgradeRetryConflictsWhenTheSameRecordDerivesChangedToolEvents()
    {
        var captureKey = CaptureCredential();
        string externalSessionId = $"external-{Guid.NewGuid():N}";
        string childId = $"child-{Guid.NewGuid():N}";
        string locator = $"adapter-upgrade-tool-event-{Guid.NewGuid():N}";
        await EnrollAsync(
            $"codex-adapter-upgrade-tool-event-{Guid.NewGuid():N}",
            captureKey);
        using var client = CaptureClient(captureKey);

        using var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            AdapterUpgradeToolObservation(
                externalSessionId,
                childId,
                locator,
                "6",
                lifecycleAsAnnotation: false));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var changedDerivedEvents = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            AdapterUpgradeToolObservation(
                externalSessionId,
                childId,
                locator,
                "7",
                lifecycleAsAnnotation: true));

        Assert.Equal(HttpStatusCode.Conflict, changedDerivedEvents.StatusCode);
    }

    [Fact]
    public async Task ChildGapDoesNotBlockParentOrSiblingCaptureCheckpoints()
    {
        var captureKey = CaptureCredential();
        await EnrollAsync($"codex-related-checkpoints-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);
        string externalSessionId = $"related-{Guid.NewGuid():N}";

        async Task<JsonElement> PostAsync(string childId, long position)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                ExplicitIdentityObservation(
                    externalSessionId,
                    externalSessionId,
                    childId,
                    position,
                    $"{childId}:{position}",
                    "8",
                    "0.144.synthetic",
                    $"{childId} at {position}"));
            string body = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        JsonElement parent = await PostAsync("parent", 0);
        JsonElement failedChild = await PostAsync("child", 1);
        JsonElement sibling = await PostAsync("sibling", 0);

        Assert.Equal("new", parent.GetProperty("status").GetString());
        Assert.Equal(
            "blocked_by_earlier_gap",
            failedChild.GetProperty("reason").GetString());
        Assert.Equal("new", sibling.GetProperty("status").GetString());

        JsonElement child = await PostAsync("child", 0);
        Assert.Equal("new", child.GetProperty("status").GetString());
        Guid[] sourceStreams =
        [
            parent.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid(),
            child.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid(),
            sibling.GetProperty("observation").GetProperty("sourceStreamUuid").GetGuid()
        ];
        Assert.Equal(3, sourceStreams.Distinct().Count());

        foreach (Guid sourceStream in sourceStreams)
        {
            JsonElement replay = JsonDocument.Parse(await RunMemCtlAsync(
                "capture", "replay", sourceStream.ToString())).RootElement;
            Assert.Equal(
                [0L],
                replay.GetProperty("events").EnumerateArray().Select(item =>
                    item.GetProperty("sourcePosition").GetInt64()));
            Assert.Equal(
                "capture_observations.source_position",
                replay.GetProperty("orderBasis").GetProperty("observation").GetString());
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
                new WriteSafetyGate(
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

    private static object ReplayObservation(
        string sourceSessionId,
        long position,
        string nativeId,
        string sourceTimestamp,
        string? model,
        string? provider,
        params (string PartKey, int PartOrder, string Text)[] parts) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            sourceTimestamp = new { raw = sourceTimestamp, parsed = sourceTimestamp },
            source = new
            {
                harness = "codex",
                harnessVersion = "synthetic-replay",
                recordType = "turn",
                model,
                provider
            },
            adapter = new { name = "codex-synthetic", version = "2" },
            sourcePayload = new { position },
            events = parts.Select(part => new
            {
                partKey = part.PartKey,
                partOrder = part.PartOrder,
                kind = "message",
                actor = "assistant",
                payload = new { text = part.Text }
            }).ToArray()
        };

    private static object ExplicitIdentityObservation(
        string? sourceSessionId,
        string externalSessionId,
        string childId,
        long position,
        string nativeId,
        string adapterVersion,
        string? harnessVersion,
        string message) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourceIdentity = new { externalSessionId, childId },
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            source = new
            {
                harness = "codex",
                harnessVersion,
                recordType = "session_meta",
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = adapterVersion },
            sourcePayload = new
            {
                type = "session_meta",
                payload = new
                {
                    session_id = externalSessionId,
                    id = childId,
                    thread_source = "subagent",
                    cli_version = "0.144.synthetic",
                    message
                }
            },
            events = new[]
            {
                new
                {
                    partKey = "metadata/0",
                    partOrder = 0,
                    kind = "lifecycle",
                    actor = "harness",
                    payload = new { message }
                }
            }
        };

    private static object RawBinaryContentObservation(
        string externalSessionId,
        string nativeId,
        string adapterVersion) => new
        {
            contractVersion = 1,
            sourceSessionId = externalSessionId,
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId },
            source = new
            {
                harness = "codex",
                harnessVersion = "0.146.synthetic",
                recordType = "response_item",
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = adapterVersion },
            sourcePayload = new
            {
                type = "response_item",
                payload = new
                {
                    type = "message",
                    content = new object[]
                    {
                        new
                        {
                            type = "binary_content",
                            category = "image",
                            text = "Visible image alt text.",
                            byte_payload = new[] { 137, 80, 78, 71 }
                        }
                    }
                }
            },
            events = new object[]
            {
                new
                {
                    partKey = "content/0:opaque",
                    partOrder = 0,
                    kind = "opaque",
                    actor = "user",
                    payload = new { text = "Visible image alt text." }
                }
            }
        };

    private static object TerminalMalformedRepresentationObservation(
        string externalSessionId,
        string locatorSeed,
        string adapterVersion,
        string recordType,
        string opaqueText = """{"type":"response_item","payload":""")
    {
        object safeRepresentation = recordType switch
        {
            "malformed_json" => new
            {
                opaqueText,
                parseError = new
                {
                    reason = "json_parse_error",
                    policyVersion = CaptureFidelityPolicy.CurrentVersion,
                    sourceIdentity = new
                    {
                        externalSessionId,
                        childId = (string?)null,
                        sourcePosition = 0,
                        locatorKind = "byte_range"
                    }
                }
            },
            "source_record_omission" => new
            {
                omission = new
                {
                    reason = "source_record_uninspectable",
                    originalByteCount = 9,
                    policyVersion = CaptureFidelityPolicy.CurrentVersion,
                    contentPolicy = "invalid_utf8",
                    sourceIdentity = new
                    {
                        externalSessionId,
                        childId = (string?)null,
                        sourcePosition = 0,
                        locatorKind = "byte_range"
                    }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(recordType))
        };
        long recordByteLength = recordType == "malformed_json"
            ? Encoding.UTF8.GetByteCount(opaqueText)
            : 9;

        return new
        {
            contractVersion = 1,
            sourceSessionId = externalSessionId,
            sourcePosition = 0,
            locator = new
            {
                kind = "byte_range",
                byteOffset = 0,
                byteLength = recordByteLength,
                sourceContentSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(locatorSeed))).ToLowerInvariant()
            },
            source = new
            {
                harness = "codex",
                harnessVersion = (string?)null,
                recordType,
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = adapterVersion },
            sourcePayload = safeRepresentation,
            events = new object[]
            {
                new
                {
                    partKey = "record:opaque",
                    partOrder = 0,
                    kind = "opaque",
                    actor = "unknown",
                    payload = new
                    {
                        recordType,
                        payloadType = (string?)null,
                        source = safeRepresentation
                    },
                    relationships = Array.Empty<object>()
                }
            }
        };
    }

    private static object StructuredSourceOwnedTerminalDiscriminatorObservation(
        string externalSessionId,
        string childId,
        string locatorSeed,
        string adapterVersion,
        string recordType)
    {
        object sourcePayload = new
        {
            type = recordType,
            message = "unchanged structured source-owned record"
        };
        return new
        {
            contractVersion = 1,
            sourceSessionId = externalSessionId,
            sourceIdentity = new { externalSessionId, childId },
            sourcePosition = 0,
            locator = new
            {
                kind = "byte_range",
                byteOffset = 0,
                byteLength = 32,
                sourceContentSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(locatorSeed))).ToLowerInvariant()
            },
            source = new
            {
                harness = "codex",
                harnessVersion = "0.154.synthetic",
                recordType,
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = adapterVersion },
            sourcePayload,
            events = new object[]
            {
                new
                {
                    partKey = "opaque/0",
                    partOrder = 0,
                    kind = "opaque",
                    actor = "unknown",
                    payload = new { recordType, payloadType = (string?)null, source = sourcePayload }
                }
            }
        };
    }

    private static object SourceOwnedBinaryOmissionLookalikeObservation(
        string externalSessionId,
        string childId,
        string nativeId,
        string adapterVersion) => new
        {
            contractVersion = 1,
            sourceSessionId = externalSessionId,
            sourceIdentity = new { externalSessionId, childId },
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId },
            source = new
            {
                harness = "codex",
                harnessVersion = "0.144.synthetic",
                recordType = "session_meta",
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = adapterVersion },
            sourcePayload = new
            {
                type = "session_meta",
                payload = new
                {
                    session_id = externalSessionId,
                    id = childId,
                    thread_source = "subagent",
                    capture_fidelity_omission = new
                    {
                        reason = CaptureFidelityPolicy.UnsupportedBinaryReason,
                        category = "image",
                        originalByteCount = 2,
                        policyVersion = CaptureFidelityPolicy.CurrentVersion
                    }
                }
            },
            events = new[]
            {
                new
                {
                    partKey = "metadata/0",
                    partOrder = 0,
                    kind = "lifecycle",
                    actor = "harness",
                    payload = new { message = "unchanged source-owned lookalike" }
                }
            }
        };

    private static object SessionRelationshipObservation(
        string externalSessionId,
        string? childId,
        string nativeId,
        string? parentNativeId,
        string? workingDirectory,
        Guid? targetSourceStreamUuid = null,
        string relationshipType = "parent_session",
        IReadOnlyCollection<(string Type, string NativeId, string Kind)>?
            additionalRelationships = null)
    {
        var relationships = new List<object>();
        if (parentNativeId is not null)
        {
            relationships.Add(new
            {
                type = relationshipType,
                target = new
                {
                    sourceStreamUuid = targetSourceStreamUuid,
                    nativeId = parentNativeId,
                    kind = "session"
                }
            });
        }
        relationships.AddRange(
            additionalRelationships?.Select(relationship => (object)new
            {
                type = relationship.Type,
                target = new
                {
                    sourceStreamUuid = (Guid?)null,
                    nativeId = relationship.NativeId,
                    kind = relationship.Kind
                }
            }) ?? []);

        return new
        {
            contractVersion = 1,
            sourceIdentity = new { externalSessionId, childId },
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId },
            routeEvidence = workingDirectory is null
                ? null
                : new { workingDirectory, remotes = Array.Empty<object>() },
            source = new
            {
                harness = "codex",
                harnessVersion = "synthetic",
                recordType = "session_meta"
            },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { externalSessionId, childId },
            events = new object[]
            {
                new
                {
                    partKey = "metadata/0",
                    partOrder = 0,
                    kind = "lifecycle",
                    actor = "harness",
                    payload = new { label = childId ?? externalSessionId },
                    relationships = relationships.ToArray()
                }
            }
        };
    }

    private static object AdapterUpgradeToolObservation(
        string externalSessionId,
        string childId,
        string nativeId,
        string adapterVersion,
        bool lifecycleAsAnnotation) => new
        {
            contractVersion = 1,
            sourceSessionId = externalSessionId,
            sourceIdentity = new { externalSessionId, childId },
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId },
            source = new
            {
                harness = "codex",
                harnessVersion = "0.145.synthetic",
                recordType = "event_msg",
                materialKind = "persisted_record"
            },
            adapter = new { name = "codex-synthetic-jsonl", version = adapterVersion },
            sourcePayload = new
            {
                cli_version = "0.145.synthetic",
                type = "event_msg",
                payload = new
                {
                    type = "exec_command_end",
                    call_id = "exec-upgrade",
                    status = "completed",
                    stdout = "unchanged output",
                    stderr = "",
                    exit_code = 0
                }
            },
            events = lifecycleAsAnnotation
                ? new object[]
                {
                    new
                    {
                        partKey = "view:exec_command_end",
                        partOrder = 0,
                        kind = "annotation",
                        actor = "harness",
                        payload = new
                        {
                            view = "exec_command_end",
                            source = new
                            {
                                type = "exec_command_end",
                                call_id = "exec-upgrade",
                                status = "completed",
                                stdout = "unchanged output",
                                stderr = "",
                                exit_code = 0
                            }
                        }
                    }
                }
                : new object[]
                {
                    new
                    {
                        partKey = "opaque/0",
                        partOrder = 0,
                        kind = "opaque",
                        actor = "unknown",
                        payload = new
                        {
                            recordType = "event_msg",
                            payloadType = "exec_command_end",
                            source = new
                            {
                                type = "exec_command_end",
                                call_id = "exec-upgrade",
                                status = "completed",
                                stdout = "unchanged output",
                                stderr = "",
                                exit_code = 0
                            }
                        }
                    }
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
