using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

// The deterministic safety boundary, asserted through the public seams only:
// the capture HTTP route, `memctl capture enroll` / `memctl capture receipt`,
// and the disabled Codex tracer. Direct database access here is the sanctioned
// never-store persistence-absence check from docs/testing.md, plus the narrow
// capture-ledger checkpoint mechanical check.
//
// Every credential in this file is synthetic.
[Collection("database")]
public sealed class CaptureSafetyTests : HttpSeamTestBase
{
    private const string SeededFakeSecret = "AKIA" + "SAFETYSLICEFAKE0";

    // Three representative unusable rule sets: absent, present-but-empty, and
    // present-but-invalid. Full per-case validation lives in SafetyGateTests.
    public static TheoryData<string, string?, string> UnusableRuleFiles() => new()
    {
        { "missing", null, "missing" },
        { "empty", "", "empty" },
        {
            "duplicate ids",
            "version: \"v1\"\nrules:\n" +
            "  - id: dup\n    category: provider_token\n    priority: 1\n" +
            "    prefilter: AKIA\n    matcher: regex\n    pattern: AKIA\n" +
            "  - id: dup\n    category: provider_token\n    priority: 2\n" +
            "    prefilter: ASIA\n    matcher: regex\n    pattern: ASIA\n",
            "duplicated"
        }
    };

    [Theory]
    [MemberData(nameof(UnusableRuleFiles))]
    public async Task UnusableRuleConfigurationMakesCaptureUnhealthyEverywhere(
        string scenario, string? contents, string expectedReason)
    {
        string rulesPath = Path.Combine(
            Path.GetTempPath(), $"never-store-{scenario.Replace(' ', '-')}-{Guid.NewGuid():N}.yaml");
        if (contents is not null)
        {
            await File.WriteAllTextAsync(rulesPath, contents);
        }
        string captureKey = CaptureCredential();
        string bindingName = $"codex-unhealthy-{Guid.NewGuid():N}";
        await EnrollAsync(bindingName, captureKey);

        try
        {
            // 1. The operator enrollment command refuses and names the reason.
            var enrollment = await RunMemCtlForResultAsync(
                new Dictionary<string, string> { ["MemSrv__NeverStorePath"] = rulesPath },
                "capture", "enroll", $"codex-refused-{Guid.NewGuid():N}",
                "--harness", "codex",
                "--agent-id", $"capture:refused-{Guid.NewGuid():N}",
                "--credential-file", await CredentialFileAsync(CaptureCredential()));
            Assert.NotEqual(0, enrollment.ExitCode);
            Assert.Contains("Capture safety rules are not usable", enrollment.Stderr);
            Assert.Contains(expectedReason, enrollment.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(enrollment.Stdout);

            // 2. Ingestion refuses on an authenticated binding, after the
            //    credential check, with the reason in the error body.
            var options = RuntimeOptions();
            options.NeverStorePath = rulesPath;
            await using var app = HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath));
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                string url = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.First();
                using var unknown = Client(url, $"unknown-capture-{Guid.NewGuid():N}");
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    (await unknown.PostAsJsonAsync(
                        "/capture/v1/observations",
                        Observation(UniqueSession(), 0, "unhealthy", "x"))).StatusCode);

                using var client = Client(url, captureKey);
                var refused = await client.PostAsJsonAsync(
                    "/capture/v1/observations",
                    Observation(UniqueSession(), 0, $"unhealthy-{Guid.NewGuid():N}", "x"));
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                string body = await refused.Content.ReadAsStringAsync();
                Assert.Contains("Capture safety rules are not usable", body);
                Assert.Contains(expectedReason, body, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await app.StopAsync();
            }

            // 3. The disabled tracer refuses to run at all.
            var tracer = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] =
                        Path.Combine(_root, "fixtures/codex-synthetic.jsonl"),
                    ["MEMSRV_NEVER_STORE_PATH"] = rulesPath
                });
            Assert.NotEqual(0, tracer.ExitCode);
            Assert.Empty(tracer.Stdout);
            Assert.Contains("refuses to run", tracer.Stderr);
            Assert.Contains(expectedReason, tracer.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(rulesPath);
        }
    }

    [Fact]
    public async Task BudgetExhaustionPersistsNothingAndLeavesThePositionForTheNextRecord()
    {
        string captureKey = CaptureCredential();
        string bindingName = $"codex-budget-{Guid.NewGuid():N}";
        string sourceSessionId = UniqueSession();
        await EnrollAsync(bindingName, captureKey);
        using var client = CaptureClient(captureKey);

        var accepted = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation(sourceSessionId, 0, $"budget-0-{Guid.NewGuid():N}", "accepted"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The match-count budget is the one scanner budget reachable through
        // the HTTP route: the deliberate 1 MB transport cap sits far below the
        // observation, decoder-candidate, and scan-time budgets. The rest are
        // exercised at the CaptureIngestion module seam below, with injected
        // budgets, because the mechanism and not the number is under test.
        string locator = $"budget-match-flood-{Guid.NewGuid():N}";
        var refused = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation(
                sourceSessionId, 1, locator,
                string.Join(' ', Enumerable.Range(0, 10_001).Select(index => $"AKIA{index:D16}"))));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        string body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("failed closed", body);
        Assert.Contains("match-count budget of 10000", body);

        var rejectedLocators = new List<string> { locator };
        var binding = await new CaptureAuthority(RuntimeConnection).ResolveAsync(captureKey);
        Assert.NotNull(binding);
        foreach (var (name, budgets, payload) in FailClosedModes())
        {
            string moduleLocator = $"budget-{name}-{Guid.NewGuid():N}";
            rejectedLocators.Add(moduleLocator);
            var ingestion = new CaptureIngestion(
                RuntimeConnection,
                new NeverStoreGate(
                    Path.Combine(_root, "config/never_store.yaml"), null, budgets));
            var failure = await Assert.ThrowsAsync<SafetyScanException>(
                () => ingestion.ImportAsync(
                    binding!,
                    CaptureObservationCommand.FromRequest(
                        JsonSerializer.Deserialize<CaptureObservationRequest>(
                            JsonSerializer.Serialize(
                                Observation(sourceSessionId, 1, moduleLocator, payload),
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)));
            Assert.Contains(name switch
            {
                "observation-size" => "observation budget",
                "decoder-candidates" => "decoder-candidate budget",
                "scan-time" => "total scan-time budget",
                _ => "matcher timeout"
            }, failure.Message);
        }

        await using (var connection = new NpgsqlConnection(AdminConnection))
        {
            await connection.OpenAsync();
            // Sanctioned capture-ledger mechanical checks: no unscanned tail
            // was appended and the checkpoint did not advance.
            Assert.False(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM capture_observations WHERE locator_native_id = ANY(@rejectedLocators))",
                new { rejectedLocators = rejectedLocators.ToArray() }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
                """
                SELECT s.checkpoint_position
                FROM capture_source_streams s
                JOIN capture_source_bindings b USING (binding_uuid)
                WHERE s.source_session_id = @sourceSessionId AND b.stable_name = @bindingName
                """,
                new { sourceSessionId, bindingName }));
        }

        // The stream is undamaged: the next legitimate record takes position 1.
        var next = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            Observation(sourceSessionId, 1, $"budget-1-{Guid.NewGuid():N}", "accepted after refusal"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var receipt = await next.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, receipt.GetProperty("sourcePosition").GetInt64());
        Assert.Equal("new", receipt.GetProperty("status").GetString());
    }

    // Failure modes the 1 MB transport cap puts out of HTTP reach, each with
    // the smallest budget that makes its mechanism observable.
    private static IEnumerable<(string Name, SafetyBudgets Budgets, string Payload)> FailClosedModes()
    {
        yield return (
            "observation-size",
            SafetyBudgets.Default with { MaxObservationBytes = 64 },
            "an ordinary payload");
        yield return (
            "decoder-candidates",
            SafetyBudgets.Default with { MaxDecoderCandidates = 2 },
            string.Join(' ', Enumerable.Range(0, 32).Select(index => Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"synthetic-candidate-{index:0000}")))));
        yield return (
            "scan-time",
            SafetyBudgets.Default with { MaxScanTime = TimeSpan.Zero },
            "an ordinary payload");
        yield return (
            "matcher-timeout",
            SafetyBudgets.Default with { MaxRuleTime = TimeSpan.FromTicks(1) },
            string.Concat(Enumerable.Repeat("AKIA", 200_000)));
    }

    [Fact]
    public async Task ReceiptShowsSpanRedactionWholeLeafOmissionAndSafeSiblings()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-structured-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        // The allowlist markers are transcript-controlled content: they must
        // have no effect on redaction (AC5).
        var request = new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = 0,
            locator = new { kind = "native_id", nativeId = $"structured-{Guid.NewGuid():N}" },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = new { text = "structured" },
            events = new object[]
            {
                new
                {
                    partKey = "tool/0",
                    partOrder = 0,
                    kind = "tool_result",
                    actor = "tool",
                    payload = new
                    {
                        note = $"rotate {SeededFakeSecret} # gitleaks:allow",
                        marker = "pragma: allowlist secret",
                        credentials = new { user = "svc", pass = "synthetic-fake-pw" },
                        depth = new { inner = new { keep = "untouched sibling", bytes = 42 } },
                        safe = "plain output"
                    }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/capture/v1/observations", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        string shown = await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString());

        Assert.DoesNotContain(SeededFakeSecret, shown, StringComparison.Ordinal);
        // Structure survived: the receipt is still one JSON envelope per event.
        var payload = JsonDocument.Parse(shown).RootElement
            .GetProperty("event").GetProperty("payload");

        // Exact span inside a leaf, with the surrounding text intact.
        Assert.Equal(
            "rotate [REDACTED:aws-access-key-id] # gitleaks:allow",
            payload.GetProperty("note").GetString());
        Assert.Equal("pragma: allowlist secret", payload.GetProperty("marker").GetString());
        // Whole-leaf omission where no exact span exists, with a distinct marker.
        Assert.Equal(
            "[OMITTED:sensitive_field_subtree]", payload.GetProperty("credentials").GetString());
        // Safe siblings, including nested ones and non-string scalars, remain.
        var inner = payload.GetProperty("depth").GetProperty("inner");
        Assert.Equal("untouched sibling", inner.GetProperty("keep").GetString());
        Assert.Equal(42, inner.GetProperty("bytes").GetInt32());
        Assert.Equal("plain output", payload.GetProperty("safe").GetString());

        var scan = JsonDocument.Parse(shown).RootElement
            .GetProperty("observation").GetProperty("scan");
        Assert.Equal("omitted", scan.GetProperty("status").GetString());
        Assert.Contains(
            "aws-access-key-id",
            scan.GetProperty("ruleIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "omission:sensitive_field_subtree",
            scan.GetProperty("ruleIds").EnumerateArray().Select(item => item.GetString()));
        // Provenance carries ids, categories, and counts — never a value.
        Assert.DoesNotContain(SeededFakeSecret, scan.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeededFakeSecretIsAbsentFromEveryDurableAndDiagnosticSurface()
    {
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-absence-{Guid.NewGuid():N}", captureKey);

        // An out-of-process server, so real application logging is observable.
        using var server = TestProcessRunner.StartServer(new Dictionary<string, string>
        {
            ["MEMSRV_TRANSPORT"] = "http",
            ["MEMSRV_HTTP_URL"] = "http://127.0.0.1:0",
            ["MEMSRV_AGENT_KEYS_PATH"] = _keysPath,
            ["MEMSRV_CONNECTION_STRING"] = RuntimeConnection
        });
        var serverStdout = new StringBuilder();
        var serverStderr = new StringBuilder();
        var outPump = PumpAsync(server.StandardOutput, serverStdout);
        var errPump = PumpAsync(server.StandardError, serverStderr);
        string responseBody;
        string keyedResponseBody;
        Guid observationUuid;
        try
        {
            string url = await WaitForListeningUrlAsync(serverStderr);
            using var client = Client(url, captureKey);

            var response = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                Observation(
                    sourceSessionId, 0, $"absence-{Guid.NewGuid():N}", SeededFakeSecret));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            responseBody = await response.Content.ReadAsStringAsync();
            observationUuid = JsonDocument.Parse(responseBody).RootElement
                .GetProperty("observationUuid").GetGuid();

            // The same seeded value as a property NAME, not a value: a
            // credential used as a map key, or an environment dump keyed by its
            // value, must not survive into durable state either.
            var keyed = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                ObservationWithPayload(
                    sourceSessionId, 1, $"absence-key-{Guid.NewGuid():N}",
                    new Dictionary<string, object>
                    {
                        [SeededFakeSecret] = "harmless",
                        ["keep"] = "kept"
                    }));
            Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
            keyedResponseBody = await keyed.Content.ReadAsStringAsync();

            // A refusal path too: its error text must not quote the candidate.
            var refused = await client.PostAsJsonAsync(
                "/capture/v1/observations",
                Observation(sourceSessionId, 9, $"absence-gap-{Guid.NewGuid():N}", SeededFakeSecret));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.DoesNotContain(
                SeededFakeSecret, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
            await server.WaitForExitAsync();
            await Task.WhenAll(outPump, errPump);
        }

        // 1. The API response, for the seed as a value and as a property name.
        Assert.DoesNotContain(SeededFakeSecret, responseBody, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:aws-access-key-id]", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SeededFakeSecret, keyedResponseBody, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:aws-access-key-id]", keyedResponseBody, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"kept\"", keyedResponseBody, StringComparison.Ordinal);

        // 2. Server logs: nothing on stdout at all (AGENTS.md), and no
        //    candidate value, captured content, credential, or complete import
        //    request on stderr.
        Assert.Equal("", Snapshot(serverStdout));
        string stderr = Snapshot(serverStderr);
        Assert.Contains("Now listening", stderr);
        Assert.DoesNotContain(SeededFakeSecret, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(captureKey, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("sourcePayload", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceSessionId, stderr, StringComparison.Ordinal);

        // 3. The operator read (both streams).
        var shown = await RunMemCtlForResultAsync(null, "capture", "receipt", observationUuid.ToString());
        Assert.Equal(0, shown.ExitCode);
        Assert.DoesNotContain(SeededFakeSecret, shown.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(SeededFakeSecret, shown.Stderr, StringComparison.Ordinal);

        // 4. A thrown exception message.
        var gate = SafetyGate();
        var rejection = Assert.Throws<NeverStoreException>(
            () => gate.AssertAllowed($"remember {SeededFakeSecret}"));
        Assert.DoesNotContain(SeededFakeSecret, rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SeededFakeSecret, rejection.ToString(), StringComparison.Ordinal);

        // 5. PostgreSQL, across every table that could have retained it.
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        string pattern = $"%{SeededFakeSecret}%";
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM capture_observations
                WHERE safe_source_payload::text LIKE @pattern
                   OR source::text LIKE @pattern OR adapter::text LIKE @pattern
                   OR locator_native_id LIKE @pattern
              UNION ALL
              SELECT 1 FROM captured_events WHERE payload::text LIKE @pattern
              UNION ALL
              SELECT 1 FROM captured_event_relationships
                WHERE target_native_id LIKE @pattern OR target_kind LIKE @pattern
              UNION ALL
              SELECT 1 FROM capture_source_streams WHERE source_session_id LIKE @pattern
              UNION ALL
              SELECT 1 FROM capture_source_bindings WHERE stable_name LIKE @pattern
              UNION ALL
              SELECT 1 FROM traces WHERE content::text LIKE @pattern
              UNION ALL
              SELECT 1 FROM memories WHERE content LIKE @pattern
            )
            """,
            new { pattern }));
    }

    [Fact]
    public async Task DisabledRuntimeScansBeforeItEmitsAndKeepsItsDiagnosticsClean()
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"codex-runtime-{Guid.NewGuid():N}", captureKey);
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-runtime-safety-{Guid.NewGuid():N}.jsonl");
        string fixture = (await File.ReadAllTextAsync(
                Path.Combine(_root, "fixtures/codex-synthetic.jsonl")))
            .Replace("call_fixture_1", $"call_{Guid.NewGuid():N}", StringComparison.Ordinal)
            .Replace(
                "Show the working directory.",
                $"Rotate {SeededFakeSecret} then show the working directory.",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixturePath, fixture, new UTF8Encoding(false));

        try
        {
            var tracer = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath
                });
            Assert.Equal(0, tracer.ExitCode);

            // The runtime crossed the gate before the observation left the
            // process, but it did not rewrite what it sent: the receipt it
            // prints is the SERVER's, redacted by the server's own independent
            // scan, which is what makes the persisted scan provenance real.
            Assert.DoesNotContain(SeededFakeSecret, tracer.Stdout, StringComparison.Ordinal);
            Assert.Contains("[REDACTED:aws-access-key-id]", tracer.Stdout, StringComparison.Ordinal);
            var scan = JsonDocument.Parse(tracer.Stdout.Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]).RootElement
                .GetProperty("observation").GetProperty("scan");
            Assert.Equal("redacted", scan.GetProperty("status").GetString());
            Assert.Contains(
                "aws-access-key-id",
                scan.GetProperty("ruleIds").EnumerateArray().Select(item => item.GetString()));
            Assert.True(scan.GetProperty("redactionCount").GetInt32() > 0);

            // Runtime diagnostics stay on stderr and carry no candidate value,
            // no captured content, no credential, and no import request.
            Assert.Contains("LIMITATION:", tracer.Stderr);
            Assert.DoesNotContain(SeededFakeSecret, tracer.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(captureKey, tracer.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("working directory", tracer.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("sourcePayload", tracer.Stderr, StringComparison.Ordinal);

            var first = JsonDocument.Parse(tracer.Stdout.Split(
                Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]).RootElement;
            var shown = await RunMemCtlForResultAsync(
                null, "capture", "receipt",
                first.GetProperty("observationUuid").GetGuid().ToString());
            Assert.Equal(0, shown.ExitCode);
            Assert.DoesNotContain(SeededFakeSecret, shown.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(SeededFakeSecret, shown.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public async Task DisabledRuntimeRefusesToSendWhenItsOwnScanFailsClosed()
    {
        string captureKey = CaptureCredential();
        await EnrollAsync($"codex-failclosed-{Guid.NewGuid():N}", captureKey);
        string fixturePath = Path.Combine(
            Path.GetTempPath(), $"codex-failclosed-{Guid.NewGuid():N}.jsonl");
        // Past the 10,000-match budget: the runtime's own scan fails closed
        // before anything is transmitted.
        string flood = string.Join(
            ' ', Enumerable.Range(0, 10_001).Select(index => $"AKIA{index:D16}"));
        string fixture = (await File.ReadAllTextAsync(
                Path.Combine(_root, "fixtures/codex-synthetic.jsonl")))
            .Replace("call_fixture_1", $"call_{Guid.NewGuid():N}", StringComparison.Ordinal)
            .Replace("Show the working directory.", flood, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixturePath, fixture, new UTF8Encoding(false));

        try
        {
            var tracer = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = _baseUrl,
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = captureKey,
                    ["OVERMIND_CODEX_FIXTURE"] = fixturePath
                });

            Assert.NotEqual(0, tracer.ExitCode);
            // Nothing was emitted: no receipt, so nothing was ever sent.
            Assert.Empty(tracer.Stdout);
            Assert.Contains("failed closed", tracer.Stderr);
            Assert.Contains("match-count budget of 10000", tracer.Stderr);
            // AC10 still holds on the refusal path.
            Assert.DoesNotContain("AKIA0000", tracer.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(captureKey, tracer.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("sourcePayload", tracer.Stderr, StringComparison.Ordinal);

            await using var connection = new NpgsqlConnection(AdminConnection);
            await connection.OpenAsync();
            Assert.False(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM capture_observations WHERE safe_source_payload::text LIKE '%AKIA0000000000000001%')"));
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    // AC2 asks for an end-to-end HTTP redaction proof per rule family. The
    // provider_token and structured_field families are proven by the receipt
    // and absence tests above; these cover the rest.
    [Fact]
    public async Task EveryRemainingRuleFamilyIsRedactedThroughTheHttpSeam()
    {
        const string fakePem =
            "-----BEGIN RSA PRIVATE KEY-----\nc3ludGhldGljZmFrZWtleW1hdGVyaWFs\n"
            + "-----END RSA PRIVATE KEY-----";
        const string fakeHeader = "Authorization: Bearer synthetic.fake.header.value.0123456789";
        const string fakeUrl = "postgres://svc_user:synthetic-fake-pw@db.internal:5432/memory";

        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-families-{Guid.NewGuid():N}", captureKey);
        using var client = CaptureClient(captureKey);

        var response = await client.PostAsJsonAsync(
            "/capture/v1/observations",
            ObservationWithPayload(
                sourceSessionId, 0, $"families-{Guid.NewGuid():N}",
                new { pem = fakePem, header = fakeHeader, dsn = fakeUrl }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        string shown = await RunMemCtlAsync(
            "capture", "receipt", receipt.GetProperty("observationUuid").GetGuid().ToString());

        var payload = JsonDocument.Parse(shown).RootElement
            .GetProperty("event").GetProperty("payload");
        Assert.Equal("[REDACTED:private-key-block]", payload.GetProperty("pem").GetString());
        Assert.Equal(
            "[REDACTED:authorization-header]", payload.GetProperty("header").GetString());
        // The credential-bearing authority is the span; the trailing path is
        // not a credential and is deliberately left readable.
        Assert.Equal(
            "[REDACTED:credential-bearing-url]/memory", payload.GetProperty("dsn").GetString());
        foreach (string secret in new[] { fakePem, fakeHeader, fakeUrl, "synthetic-fake-pw" })
        {
            Assert.DoesNotContain(secret, shown, StringComparison.Ordinal);
        }
        var ruleIds = JsonDocument.Parse(shown).RootElement
            .GetProperty("observation").GetProperty("scan").GetProperty("ruleIds")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("private-key-block", ruleIds);
        Assert.Contains("authorization-header", ruleIds);
        Assert.Contains("credential-bearing-url", ruleIds);
    }

    // NeverStoreLiteralsPath is new deployment configuration; this is the only
    // test that proves the SERVER honors it, through the HTTP seam.
    [Fact]
    public async Task ServerConfiguredWithAnOperatorLiteralsFileRedactsThatLiteral()
    {
        const string configuredValue = "synthetic-operator-literal-0006";
        string literalsPath = Path.Combine(
            Path.GetTempPath(), $"never-store-literals-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(literalsPath, $"# operator-owned\n{configuredValue}\n");
        string captureKey = CaptureCredential();
        string sourceSessionId = UniqueSession();
        await EnrollAsync($"codex-literals-{Guid.NewGuid():N}", captureKey);

        try
        {
            var options = RuntimeOptions();
            options.NeverStoreLiteralsPath = literalsPath;
            await using var app = HttpServerHost.Build(options, AgentKeyStore.Load(_keysPath));
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            string observationUuid;
            try
            {
                string url = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.First();
                using var client = Client(url, captureKey);
                var response = await client.PostAsJsonAsync(
                    "/capture/v1/observations",
                    Observation(
                        sourceSessionId, 0, $"literal-{Guid.NewGuid():N}",
                        $"the deploy used {configuredValue} last night"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain(configuredValue, body, StringComparison.Ordinal);
                Assert.Contains("[REDACTED:operator-literal]", body, StringComparison.Ordinal);
                observationUuid = JsonDocument.Parse(body).RootElement
                    .GetProperty("observationUuid").GetGuid().ToString();
            }
            finally
            {
                await app.StopAsync();
            }

            // Durable state, read back through the operator seam.
            string shown = await RunMemCtlAsync("capture", "receipt", observationUuid);
            Assert.DoesNotContain(configuredValue, shown, StringComparison.Ordinal);
            Assert.Contains("[REDACTED:operator-literal]", shown, StringComparison.Ordinal);

            await using var connection = new NpgsqlConnection(AdminConnection);
            await connection.OpenAsync();
            Assert.False(await connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                  SELECT 1 FROM capture_observations WHERE safe_source_payload::text LIKE @pattern
                  UNION ALL
                  SELECT 1 FROM captured_events WHERE payload::text LIKE @pattern
                )
                """,
                new { pattern = $"%{configuredValue}%" }));
        }
        finally
        {
            File.Delete(literalsPath);
        }
    }

    // --- helpers ----------------------------------------------------------

    private static string CaptureCredential() => $"mcap_{Guid.NewGuid():N}";
    private static string UniqueSession() => $"safety-session-{Guid.NewGuid():N}";

    private HttpClient CaptureClient(string key) => Client(_baseUrl, key);

    private static HttpClient Client(string baseUrl, string key)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<string> CredentialFileAsync(string credential)
    {
        string path = Path.Combine(Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, credential);
        return path;
    }

    private async Task EnrollAsync(string name, string captureKey)
    {
        string path = await CredentialFileAsync(captureKey);
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

    private static async Task<string> WaitForListeningUrlAsync(StringBuilder stderr)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            foreach (string line in Snapshot(stderr).Split('\n'))
            {
                int index = line.IndexOf("Now listening on: ", StringComparison.Ordinal);
                if (index >= 0)
                {
                    return line[(index + "Now listening on: ".Length)..].Trim();
                }
            }
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException(
            $"Server never reported a listening address. stderr:{Environment.NewLine}{Snapshot(stderr)}");
    }

    private static Task PumpAsync(StreamReader reader, StringBuilder sink) => Task.Run(async () =>
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (sink) { sink.AppendLine(line); }
        }
    });

    private static string Snapshot(StringBuilder buffer)
    {
        lock (buffer) { return buffer.ToString(); }
    }

    private static object Observation(
        string sourceSessionId, long position, string nativeId, string message) =>
        ObservationWithPayload(sourceSessionId, position, nativeId, new { text = message });

    private static object ObservationWithPayload(
        string sourceSessionId, long position, string nativeId, object payload) => new
        {
            contractVersion = 1,
            sourceSessionId,
            sourcePosition = position,
            locator = new { kind = "native_id", nativeId },
            source = new { harness = "codex", harnessVersion = "synthetic", recordType = "turn" },
            adapter = new { name = "codex-synthetic", version = "1" },
            sourcePayload = payload,
            events = new object[]
            {
                new
                {
                    partKey = "message/0",
                    partOrder = 0,
                    kind = "message",
                    actor = "user",
                    payload
                }
            }
        };
}
