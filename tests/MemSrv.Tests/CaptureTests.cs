using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureTests : HttpSeamTestBase
{
    [Fact]
    public async Task OperatorEnrollsRestrictedCodexCaptureAndReadsFallbackReceipt()
    {
        var captureKey = $"capture-fixture-{Guid.NewGuid():N}";
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
                "/capture/v1/observations", Observation(0, "record-1", "hello"));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var receipt = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("new", receipt.GetProperty("status").GetString());
            Assert.Equal("capture/unscoped", receipt.GetProperty("effectiveNamespace").GetString());
            Assert.Equal("fallback", receipt.GetProperty("routeBasis").GetString());
            Assert.Equal(3, receipt.GetProperty("events").GetArrayLength());

            var observationUuid = receipt.GetProperty("observationUuid").GetGuid();
            var shown = await RunMemCtlAsync("capture", "receipt", observationUuid.ToString());
            Assert.Contains("status=new", shown);
            Assert.Contains("namespace=capture/unscoped", shown);
            Assert.Contains("route=fallback", shown);
            Assert.Contains("LIMITATION:", shown);
        }
        finally
        {
            File.Delete(credentialPath);
        }
    }

    [Fact]
    public async Task RetryIsAlreadyAcceptedAndChangedContentConflictsWithoutMutation()
    {
        var captureKey = $"capture-idempotency-{Guid.NewGuid():N}";
        await EnrollAsync("codex-idempotency", captureKey);
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "record-stable", "original"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstReceipt = await first.Content.ReadFromJsonAsync<JsonElement>();

        var retry = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "record-stable", "original"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryReceipt = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_accepted", retryReceipt.GetProperty("status").GetString());
        Assert.Equal(
            firstReceipt.GetProperty("observationUuid").GetGuid(),
            retryReceipt.GetProperty("observationUuid").GetGuid());

        var conflict = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "record-stable", "changed"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var shown = await RunMemCtlAsync(
            "capture", "receipt", firstReceipt.GetProperty("observationUuid").GetGuid().ToString());
        Assert.Contains("original", shown);
        Assert.DoesNotContain("changed", shown);
    }

    [Fact]
    public async Task DisabledTracerImportsSyntheticCodexMessageAndToolExchange()
    {
        var captureKey = $"capture-tracer-{Guid.NewGuid():N}";
        await EnrollAsync("codex-tracer", captureKey);

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
                ["OVERMIND_CODEX_FIXTURE"] = Path.Combine(_root, "fixtures/codex-synthetic.jsonl")
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
        var resultEvent = Assert.Single(receipts[2].GetProperty("events").EnumerateArray());
        var resultRelationship = Assert.Single(
            resultEvent.GetProperty("relationships").EnumerateArray());
        Assert.Equal("result_for", resultRelationship.GetProperty("type").GetString());
        Assert.Equal("call_fixture_1", resultRelationship.GetProperty("targetNativeId").GetString());
        Assert.Equal("tool_call", resultRelationship.GetProperty("targetKind").GetString());
        Assert.Contains("LIMITATION:", enabled.Stderr);
    }

    [Fact]
    public async Task ObservationFanoutAndCheckpointAdvanceAreAtomic()
    {
        var captureKey = $"capture-atomic-{Guid.NewGuid():N}";
        await EnrollAsync("codex-atomic", captureKey);
        using var client = CaptureClient(captureKey);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "atomic-1", "accepted"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var rejected = await client.PostAsJsonAsync(
            "/capture/v1/observations", ObservationWithDuplicatePartOrder(1, "atomic-2"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM capture_observations WHERE source_locator = 'atomic-2')"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            """
            SELECT s.checkpoint_position
            FROM capture_source_streams s
            JOIN capture_source_bindings b USING (binding_uuid)
            WHERE s.source_session_id = 'synthetic-session' AND b.stable_name = 'codex-atomic'
            """));
    }

    [Fact]
    public async Task StreamRejectsGapsAndBacktrackingWithoutMovingTheAcceptedPrefix()
    {
        var captureKey = $"capture-prefix-{Guid.NewGuid():N}";
        await EnrollAsync("codex-prefix", captureKey);
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "prefix-0", "zero"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var gap = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(2, "prefix-2", "gap"));
        Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        Assert.Contains("expected sourcePosition 1", await gap.Content.ReadAsStringAsync());

        var next = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(1, "prefix-1", "one"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var nextReceipt = await next.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, nextReceipt.GetProperty("sourcePosition").GetInt64());

        var backtrack = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "different-prefix-0", "other"));
        Assert.Equal(HttpStatusCode.Conflict, backtrack.StatusCode);

        var third = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(2, "prefix-2", "two"));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task StreamKeepsItsFirstEffectiveRouteAfterBindingPolicyChanges()
    {
        var captureKey = $"capture-route-{Guid.NewGuid():N}";
        await EnrollAsync("codex-route-fixed", captureKey);
        using var client = CaptureClient(captureKey);

        var first = await client.PostAsJsonAsync(
            "/capture/v1/observations", Observation(0, "route-0", "zero"));
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
            "/capture/v1/observations", Observation(1, "route-1", "one"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var receipt = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("capture/unscoped", receipt.GetProperty("effectiveNamespace").GetString());
        Assert.Equal("fallback", receipt.GetProperty("routeBasis").GetString());
    }

    [Fact]
    public async Task SafetyGateRedactsSyntheticSecretBeforeAnyCaptureAppend()
    {
        var captureKey = $"capture-safety-{Guid.NewGuid():N}";
        await EnrollAsync("codex-safety", captureKey);
        using var client = CaptureClient(captureKey);
        string seededSyntheticSecret = "AKIA" + "SYNTHETICFIXTURE";

        var request = SafetyObservation(seededSyntheticSecret);
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
        Assert.Contains("scan=redacted", shown);
        Assert.Contains("rules=aws-access-key-id", shown);
        Assert.Contains("categories=secret", shown);
        Assert.Contains("redactions=8", shown);

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

    private static object Observation(long position, string locator, string message) => new
    {
        contractVersion = 1,
        sourceSessionId = "synthetic-session",
        sourcePosition = position,
        sourceLocator = locator,
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
                relationships = new[] { new { type = "result_for", targetNativeId = "call-1", targetKind = "tool_call" } } }
        }
    };

    private static object ObservationWithDuplicatePartOrder(long position, string locator) => new
    {
        contractVersion = 1,
        sourceSessionId = "synthetic-session",
        sourcePosition = position,
        sourceLocator = locator,
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

    private static object SafetyObservation(string secret) => new
    {
        contractVersion = 1,
        sourceSessionId = "synthetic-session",
        sourcePosition = 0,
        sourceLocator = "safety-1",
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
                    new { type = "result_for", targetNativeId = secret, targetKind = secret }
                }
            }
        }
    };
}
