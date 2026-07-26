using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace MemSrv.Tests;

// The never-store gate is a public module surface of MemSrv.Core, and these
// tests call it directly. That is the highest seam at which rule-set
// validation, deterministic overlap resolution, bounded decoding, and the
// numeric scan budgets are observable at all: none of them has an MCP tool or
// a memctl command, and the HTTP capture route is capped at 1 MB by transport
// long before a scanner budget is reachable. End-to-end proof that the same
// gate governs real writes lives in CaptureTests, AcceptanceTests,
// MemoryServiceTests, and WorkstreamToolsTests.
//
// Every credential in this file is synthetic. Nothing here is, or resembles,
// a live credential beyond a provider prefix.
public sealed class SafetyGateTests : IDisposable
{
    private const string FakeAwsKeyId = "AKIA" + "FAKEFAKEFAKEFAKE";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"never-store-{Guid.NewGuid():N}");
    private readonly string _shippedRules =
        Path.Combine(TestProcessRunner.RepoRoot, "config/never_store.yaml");

    public SafetyGateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // --- AC1 / AC6: rule configuration fails closed and is validated --------

    public static TheoryData<string, string?, string> UnusableConfigurations() => new()
    {
        { "missing file", null, "missing" },
        { "empty file", "", "empty" },
        { "whitespace only", "   \n  \n", "empty" },
        { "not yaml", "rules: [ this: is: not: yaml", "not valid YAML" },
        { "no version", "rules:\n  - id: a\n", "version" },
        { "no rules", "version: \"v1\"\nrules: []\n", "no rules" },
        {
            "blank id",
            "version: \"v1\"\nrules:\n  - id: \"\"\n    category: provider_token\n" +
            "    priority: 1\n    prefilter: AKIA\n    matcher: regex\n    pattern: AKIA\n",
            "blank id"
        },
        {
            "duplicate ids",
            "version: \"v1\"\nrules:\n" +
            "  - id: dup\n    category: provider_token\n    priority: 1\n" +
            "    prefilter: AKIA\n    matcher: regex\n    pattern: AKIA\n" +
            "  - id: dup\n    category: provider_token\n    priority: 2\n" +
            "    prefilter: ASIA\n    matcher: regex\n    pattern: ASIA\n",
            "duplicated"
        },
        {
            "unknown category",
            "version: \"v1\"\nrules:\n  - id: a\n    category: vibes\n    priority: 1\n" +
            "    prefilter: AKIA\n    matcher: regex\n    pattern: AKIA\n",
            "unknown category"
        },
        {
            "no priority",
            "version: \"v1\"\nrules:\n  - id: a\n    category: provider_token\n" +
            "    prefilter: AKIA\n    matcher: regex\n    pattern: AKIA\n",
            "no priority"
        },
        {
            "no prefilter",
            "version: \"v1\"\nrules:\n  - id: a\n    category: provider_token\n" +
            "    priority: 1\n    matcher: regex\n    pattern: AKIA\n",
            "no prefilter"
        },
        {
            "unsupported matcher",
            "version: \"v1\"\nrules:\n  - id: a\n    category: provider_token\n" +
            "    priority: 1\n    prefilter: AKIA\n    matcher: entropy\n    pattern: AKIA\n",
            "unsupported matcher"
        },
        {
            "pattern the non-backtracking matcher cannot compile",
            "version: \"v1\"\nrules:\n  - id: a\n    category: provider_token\n" +
            "    priority: 1\n    prefilter: AKIA\n    matcher: regex\n" +
            "    pattern: '(?<=x)AKIA[0-9A-Z]{16}'\n",
            "non-backtracking"
        }
    };

    [Theory]
    [MemberData(nameof(UnusableConfigurations))]
    public void UnusableRuleConfigurationFailsClosedWithASafeReason(
        string scenario, string? contents, string expectedReason)
    {
        string path = Path.Combine(_directory, $"rules-{Guid.NewGuid():N}.yaml");
        if (contents is not null)
        {
            File.WriteAllText(path, contents);
        }

        var gate = new NeverStoreGate(path);

        Assert.False(gate.IsConfigured, scenario);
        Assert.Contains(expectedReason, gate.FailureReason!, StringComparison.OrdinalIgnoreCase);
        // Constructible, but every governed call refuses.
        Assert.Throws<SafetyConfigurationException>(() => gate.Scan("anything"));
        Assert.Throws<SafetyConfigurationException>(() => gate.Redact("anything"));
        Assert.Throws<SafetyConfigurationException>(() => gate.AssertAllowed("anything"));
        Assert.Throws<SafetyConfigurationException>(() => gate.ScanJson("{}"));
        Assert.Throws<SafetyConfigurationException>(() => gate.AssertAllowedObject(new { a = 1 }));
        Assert.Throws<SafetyConfigurationException>(
            () => gate.AssertObservationWithinBudget("{}"));
        Assert.Contains(gate.FailureReason!,
            Assert.Throws<SafetyConfigurationException>(() => gate.Scan("x")).Message);
    }

    [Fact]
    public void ShippedRuleSetLoadsAndCarriesAStableVersion()
    {
        var gate = new NeverStoreGate(_shippedRules);
        Assert.True(gate.IsConfigured, gate.FailureReason);
        Assert.Null(gate.FailureReason);
        Assert.StartsWith("never-store/", gate.RuleSetVersion);
        Assert.Equal(gate.RuleSetVersion, new NeverStoreGate(_shippedRules).RuleSetVersion);
        Assert.Equal(SafetyBudgets.CurrentVersion, gate.Budgets.Version);
    }

    [Fact]
    public void FailedReloadKeepsThePreviouslyLoadedRuleSetInForce()
    {
        string path = Path.Combine(_directory, "reloadable.yaml");
        File.WriteAllText(path, SingleRule("first-rule", "AKIA", @"\bAKIA[0-9A-Z]{16}\b"));
        var gate = new NeverStoreGate(path);
        string firstVersion = gate.RuleSetVersion;
        Assert.Equal($"[REDACTED:first-rule]", gate.Redact(FakeAwsKeyId));

        File.WriteAllText(path, "version: \"broken\"\nrules: []\n");
        Assert.False(gate.TryReload(out string? reason));
        Assert.Contains("no rules", reason!);
        // Atomic: the failed load changed nothing observable.
        Assert.True(gate.IsConfigured);
        Assert.Equal(firstVersion, gate.RuleSetVersion);
        Assert.Equal("[REDACTED:first-rule]", gate.Redact(FakeAwsKeyId));

        File.WriteAllText(path, SingleRule("second-rule", "AKIA", @"\bAKIA[0-9A-Z]{16}\b"));
        Assert.True(gate.TryReload(out string? noReason));
        Assert.Null(noReason);
        Assert.NotEqual(firstVersion, gate.RuleSetVersion);
        Assert.Equal("[REDACTED:second-rule]", gate.Redact(FakeAwsKeyId));
    }

    [Fact]
    public void OverlappingMatchesResolveByPriorityThenLengthDeterministically()
    {
        var gate = new NeverStoreGate(_shippedRules);

        // "Authorization: Bearer <token>" matches authorization-header
        // (priority 96) and bearer-token (priority 90) on overlapping spans.
        var header = gate.Scan("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345");
        Assert.Equal("[REDACTED:authorization-header]", header.Redacted);
        Assert.Equal(["authorization-header"], header.RuleIds);
        Assert.Equal(1, header.RedactionCount);

        // Same input, repeated: identical output, no ordering drift.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                header.Redacted,
                gate.Redact("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345"));
        }

        // A bare bearer header still reports the bearer rule.
        Assert.Equal(
            "[REDACTED:bearer-token]",
            gate.Redact("bearer abcdefghijklmnopqrstuvwxyz012345"));
    }

    // --- AC2: rule families over a synthetic positive corpus ---------------

    public static TheoryData<string, string, string> PositiveCorpus() => new()
    {
        {
            "pem private key",
            "-----BEGIN RSA PRIVATE KEY-----\nc3ludGhldGljZmFrZWtleW1hdGVyaWFs\n" +
            "-----END RSA PRIVATE KEY-----",
            "private-key-block"
        },
        {
            "openssh private key",
            "-----BEGIN OPENSSH PRIVATE KEY-----\nc3ludGhldGljZmFrZQ==\n" +
            "-----END OPENSSH PRIVATE KEY-----",
            "private-key-block"
        },
        {
            "unterminated private key block redacts to the end of the leaf",
            "-----BEGIN PGP PRIVATE KEY BLOCK-----\nc3ludGhldGljZmFrZQ==",
            "private-key-block"
        },
        {
            "crlf private key",
            "-----BEGIN EC PRIVATE KEY-----\r\nc3ludGhldGljZmFrZQ==\r\n" +
            "-----END EC PRIVATE KEY-----",
            "private-key-block"
        },
        { "authorization header", "Authorization: Basic c3ludGhldGljOmZha2U=", "authorization-header" },
        { "bare basic header", "basic c3ludGhldGljZmFrZXBhaXI=", "basic-auth-header" },
        { "bearer header", "bearer synthetic.fake.token.value.0123456789", "bearer-token" },
        {
            "credential-bearing postgres url",
            "postgres://svc_user:synthetic-fake-pw@db.internal:5432/memory",
            "credential-bearing-url"
        },
        {
            "credential-bearing https url",
            "https://ci-bot:synthetic-fake-pw@example.invalid/repo.git",
            "credential-bearing-url"
        },
        { "aws access key id", FakeAwsKeyId, "aws-access-key-id" },
        { "aws sts key id", "ASIA" + "FAKEFAKEFAKEFAKE", "aws-access-key-id" },
        {
            "github classic token",
            "ghp_" + new string('A', 36),
            "github-token"
        },
        {
            "github fine-grained token",
            "github_pat_" + new string('B', 24),
            "github-fine-grained-token"
        },
        { "gitlab token", "glpat-" + new string('C', 20), "gitlab-access-token" },
        { "openai key", "sk-" + new string('D', 32), "openai-api-key" },
        { "anthropic key", "sk-ant-" + new string('E', 32), "anthropic-api-key" },
        { "slack token", "xoxb-" + new string('1', 12), "slack-token" },
        { "npm token", "npm_" + new string('F', 36), "npm-access-token" },
        { "google api key", "AIza" + new string('G', 35), "google-api-key" },
        { "dotenv assignment", "API_KEY=synthetic-fake-value-0001", "sensitive-assignment" },
        {
            "quoted assignment with spaces",
            "client_secret = \"synthetic fake value with spaces\"",
            "sensitive-assignment"
        },
        {
            "single-quoted assignment",
            "PASSWORD: 'synthetic fake pw'",
            "sensitive-assignment"
        },
        {
            "compound name assignment",
            "AWS_SECRET_ACCESS_KEY=syntheticfakevalue0001",
            "sensitive-assignment"
        },
        {
            "cli flag assignment",
            "--api-key=synthetic-fake-value-0002",
            "sensitive-assignment"
        }
    };

    [Theory]
    [MemberData(nameof(PositiveCorpus))]
    public void SyntheticCredentialCorpusIsRedactedByItsRule(
        string scenario, string value, string expectedRuleId)
    {
        var gate = new NeverStoreGate(_shippedRules);
        var result = gate.Scan(value);

        Assert.Contains(expectedRuleId, result.RuleIds);
        Assert.Contains($"[REDACTED:{expectedRuleId}]", result.Redacted);
        Assert.True(result.RedactionCount > 0, scenario);
        // The value itself never survives into the sanitized text.
        Assert.DoesNotContain(SecretCore(value), result.Redacted, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> NegativeCorpus() => new()
    {
        { "sha256 hash", "content_hash 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" },
        { "uuid", "session 550e8400-e29b-41d4-a716-446655440000 consumed" },
        { "git commit sha", "commit 1dbe57f8f2a4c9b0e1d3a5f7c9b1d3e5f7a9c1b3" },
        { "source map", "//# sourceMappingURL=data:application/json;base64,eyJ2ZXJzaW9uIjozfQ==" },
        { "package metadata", "\"integrity\": \"sha512-abcdefghijklmnopqrstuvwxyz0123456789+/==\"" },
        { "generated code", "const tokenizer = new Tokenizer(); // token stream helper" },
        { "ordinary prose about tokens", "The token budget for this secret plan is unclear." },
        { "ordinary prose about secrets", "Keep the secret sauce recipe out of the repo." },
        { "url without credentials", "https://example.invalid/api/v1/tokens?limit=20" },
        { "bearer word without a token", "bearer of bad news" },
        { "assignment with no separator", "password rotation policy is quarterly" }
    };

    [Theory]
    [MemberData(nameof(NegativeCorpus))]
    public void OrdinaryTranscriptShapesAreNotRedacted(string scenario, string value)
    {
        var gate = new NeverStoreGate(_shippedRules);
        var result = gate.Scan(value);

        Assert.Equal(value, result.Redacted);
        Assert.Equal(0, result.RedactionCount);
        Assert.Empty(result.RuleIds);
        Assert.Empty(result.OmissionReasons);
        Assert.True(result.RedactionCount == 0, scenario);
    }

    // --- AC5: content can never authorize its own persistence --------------

    [Theory]
    [InlineData("gitleaks:allow")]
    [InlineData("pragma: allowlist secret")]
    [InlineData("# noqa: detect-secrets")]
    [InlineData("nosecret - reviewed and approved by the transcript author")]
    public void TranscriptControlledAllowlistMarkersHaveNoEffect(string marker)
    {
        var gate = new NeverStoreGate(_shippedRules);

        Assert.Contains(
            "[REDACTED:aws-access-key-id]", gate.Redact($"{FakeAwsKeyId} {marker}"));
        Assert.Contains(
            "[REDACTED:aws-access-key-id]", gate.Redact($"{marker}\n{FakeAwsKeyId}"));
        Assert.DoesNotContain(
            FakeAwsKeyId,
            gate.RedactJson($$"""{"note":"{{marker}}","value":"{{FakeAwsKeyId}}"}"""));
    }

    // --- AC3: decoded structured leaves, exact spans, whole-leaf omission --

    [Fact]
    public void StructuredScanRedactsLeafSpansAndNeverRewritesSerializedJson()
    {
        var gate = new NeverStoreGate(_shippedRules);
        string json = $$"""
            {"outer":{"note":"key {{FakeAwsKeyId}} was rotated","count":3,
             "list":["safe","also {{FakeAwsKeyId}}"]},"safe":"untouched"}
            """;

        var result = gate.ScanJson(json);
        using var document = JsonDocument.Parse(result.Redacted);
        var outer = document.RootElement.GetProperty("outer");

        Assert.Equal(
            "key [REDACTED:aws-access-key-id] was rotated",
            outer.GetProperty("note").GetString());
        Assert.Equal(3, outer.GetProperty("count").GetInt32());
        Assert.Equal("safe", outer.GetProperty("list")[0].GetString());
        Assert.Equal(
            "also [REDACTED:aws-access-key-id]", outer.GetProperty("list")[1].GetString());
        Assert.Equal("untouched", document.RootElement.GetProperty("safe").GetString());
        Assert.Equal(2, result.RedactionCount);
        Assert.Empty(result.OmissionReasons);
    }

    [Fact]
    public void SensitiveFieldWithoutAnExactSpanOmitsThatLeafAndKeepsSafeSiblings()
    {
        var gate = new NeverStoreGate(_shippedRules);
        string json = """
            {"keep":"visible","password":8675309,"credentials":{"user":"u","pass":"p"},
             "token":["synthetic-fake-one","synthetic-fake-two"],"absent":null}
            """;

        var result = gate.ScanJson(json);
        using var document = JsonDocument.Parse(result.Redacted);
        var root = document.RootElement;

        Assert.Equal("visible", root.GetProperty("keep").GetString());
        Assert.Equal("[OMITTED:sensitive_field_scalar]", root.GetProperty("password").GetString());
        Assert.Equal(
            "[OMITTED:sensitive_field_subtree]", root.GetProperty("credentials").GetString());
        Assert.Equal(
            "[OMITTED:sensitive_field_subtree]", root.GetProperty("token").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("absent").ValueKind);
        Assert.Contains("sensitive_field_scalar", result.OmissionReasons);
        Assert.Contains("sensitive_field_subtree", result.OmissionReasons);
    }

    [Fact]
    public void SecretsInPropertyNamesCrossTheSameRulesAsValues()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // An env dump keyed by its value, a credential used as a map key: the
        // secret is the NAME, and nothing scans the value side.
        string json = $$"""
            {"env":{"{{FakeAwsKeyId}}":"harmless","KEEP_ME":"kept"},"safe":"untouched"}
            """;

        var result = gate.ScanJson(json);

        Assert.DoesNotContain(FakeAwsKeyId, result.Redacted, StringComparison.Ordinal);
        Assert.Contains("aws-access-key-id", result.RuleIds);
        // Still parseable JSON, with the surrounding structure intact.
        using var document = JsonDocument.Parse(result.Redacted);
        var env = document.RootElement.GetProperty("env");
        Assert.Equal("harmless", env.GetProperty("[REDACTED:aws-access-key-id]").GetString());
        Assert.Equal("kept", env.GetProperty("KEEP_ME").GetString());
        Assert.Equal("untouched", document.RootElement.GetProperty("safe").GetString());
    }

    [Fact]
    public void SiblingPropertyNamesThatCollapseToTheSameTextOmitTheWholeObject()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // Two distinct secrets, one redacted name: emitting both would write a
        // duplicate key and silently lose a value on re-parse.
        string json = $$"""
            {"env":{"{{FakeAwsKeyId}}":"first","AKIAFAKEFAKEFAKEFAK1":"second"},"safe":"untouched"}
            """;

        var result = gate.ScanJson(json);

        Assert.DoesNotContain(FakeAwsKeyId, result.Redacted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Redacted);
        Assert.Equal(
            "[OMITTED:redacted_name_collision]",
            document.RootElement.GetProperty("env").GetString());
        Assert.Equal("untouched", document.RootElement.GetProperty("safe").GetString());
        Assert.Contains("redacted_name_collision", result.OmissionReasons);
    }

    [Fact]
    public void LiterallyDuplicateSourceKeysWithNoSecretsPassThroughWithoutOmission()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // Duplicate keys are legal JSON that JsonDocument preserves. Nothing was
        // redacted here, so the collision was in the SOURCE, not caused by the
        // gate: dropping the object would be a fidelity loss unrelated to secrets.
        const string json = """
            {"env":{"a":1,"a":2},"safe":"untouched"}
            """;

        var result = gate.ScanJson(json);

        Assert.Empty(result.OmissionReasons);
        Assert.DoesNotContain("[OMITTED:", result.Redacted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Redacted);
        Assert.Equal(
            JsonValueKind.Object, document.RootElement.GetProperty("env").ValueKind);
        Assert.Equal("untouched", document.RootElement.GetProperty("safe").GetString());
    }

    [Fact]
    public void AShortHighPriorityMatchInsideALongerOneStillRedactsTheWholeOuterSpan()
    {
        const string configuredValue = "synthetic-operator-literal-0004";
        string literals = Path.Combine(_directory, "overlap-literals.txt");
        File.WriteAllText(literals, configuredValue + "\n");
        var gate = new NeverStoreGate(_shippedRules, literals);

        // The literal (priority int.MaxValue) sits strictly INSIDE the
        // private-key block (priority 100). Discarding the key-block match
        // would leave every byte of the block except the literal in cleartext.
        string value =
            "-----BEGIN RSA PRIVATE KEY-----\n"
            + $"c3ludGhldGljZmFrZWtleW1hdGVyaWFs{configuredValue}\n"
            + "-----END RSA PRIVATE KEY-----";

        var result = gate.Scan(value);

        Assert.Equal("[REDACTED:operator-literal]", result.Redacted);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", result.Redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("c3ludGhldGlj", result.Redacted, StringComparison.Ordinal);
        Assert.Equal(1, result.RedactionCount);
    }

    [Fact]
    public void OversizedLeafIsWhollyOmittedWhileSafeSiblingsRemain()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxLeafBytes = 32 });
        string json = JsonSerializer.Serialize(new
        {
            small = "kept",
            big = new string('x', 64)
        });

        var result = gate.ScanJson(json);
        using var document = JsonDocument.Parse(result.Redacted);

        Assert.Equal("kept", document.RootElement.GetProperty("small").GetString());
        Assert.Equal(
            "[OMITTED:leaf_exceeds_limit]", document.RootElement.GetProperty("big").GetString());
        Assert.Equal(["leaf_exceeds_limit"], result.OmissionReasons);
    }

    [Fact]
    public void ARequiredValueThatCannotBeInspectedCompletelyFailsClosed()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxLeafBytes = 8 });

        var failure = Assert.Throws<SafetyScanException>(
            () => gate.AssertAllowed(new string('y', 64)));
        Assert.Contains("could not be inspected completely", failure.Message);
        Assert.Contains("leaf_exceeds_limit", failure.Message);
    }

    // --- AC4: at most one bounded decoding pass ----------------------------

    [Fact]
    public void OneLevelPercentHexAndBase64EncodingsAreDecodedAndTheEncodedSpanRedacted()
    {
        var gate = new NeverStoreGate(_shippedRules);
        string percent = string.Concat(FakeAwsKeyId.Select(c => $"%{(int)c:X2}"));
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(FakeAwsKeyId)).ToLowerInvariant();
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(FakeAwsKeyId));

        foreach (string encoded in new[] { percent, hex, base64 })
        {
            var result = gate.Scan($"value {encoded} end");
            Assert.Contains("aws-access-key-id", result.RuleIds);
            Assert.Equal("value [REDACTED:aws-access-key-id] end", result.Redacted);
            Assert.DoesNotContain(encoded, result.Redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Base64UrlEncodedCredentialsAreDecodedToo()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // Chosen so the base64url form genuinely differs from the standard
        // form: the '_' falls where no standard-alphabet run of the encoding
        // decodes back to the credential.
        string encoded = Convert
            .ToBase64String(Encoding.UTF8.GetBytes("ÿ·" + FakeAwsKeyId))
            .Replace('+', '-')
            .Replace('/', '_');
        Assert.Contains('_', encoded);

        var result = gate.Scan($"value {encoded} end");

        Assert.Contains("aws-access-key-id", result.RuleIds);
        Assert.Equal("value [REDACTED:aws-access-key-id] end", result.Redacted);
    }

    [Fact]
    public void AnEncodedCredentialsFileLongerThanTheOldCapIsStillDecoded()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // A base64'd credentials file: well past the previous 4,096-character
        // qualification cap, well inside the published 65,536 one.
        string credentialsFile =
            string.Concat(Enumerable.Range(0, 200).Select(index =>
                $"# synthetic credentials file line {index:0000} of padding text\n"))
            + $"aws_access_key_id = {FakeAwsKeyId}\n";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentialsFile));
        Assert.InRange(encoded.Length, 4_097, SafetyBudgets.Default.MaxDecoderCandidateLength);

        var result = gate.Scan($"blob {encoded} end");

        Assert.Contains("aws-access-key-id", result.RuleIds);
        Assert.Equal("blob [REDACTED:aws-access-key-id] end", result.Redacted);
    }

    [Fact]
    public void AnEncodedRunBeyondTheCandidateLengthCapIsSkippedNotFailedClosed()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // The accepted residual risk, stated as a test so it cannot drift into
        // an unnoticed fail-open OR an unnoticed availability loss: a run this
        // long is not decoded, and the scan still succeeds.
        string oversized = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                new string('p', 60_000) + $" aws_access_key_id = {FakeAwsKeyId}"));
        Assert.True(oversized.Length > SafetyBudgets.Default.MaxDecoderCandidateLength);

        var result = gate.Scan(oversized);

        Assert.Equal(oversized, result.Redacted);
        Assert.Equal(0, result.RedactionCount);
        Assert.Empty(result.OmissionReasons);
    }

    [Fact]
    public void DecoderCandidateExtractionCarriesTheSameMatcherTimeout()
    {
        var gate = new NeverStoreGate(
            _shippedRules,
            null,
            SafetyBudgets.Default with { MaxRuleTime = TimeSpan.FromTicks(1) });
        // No rule prefilter hits this value, so the only matcher that runs is
        // decoder candidate extraction. Without a timeout it would run to
        // completion; with one it must fail the scan closed.
        string pathological = string.Concat(Enumerable.Repeat("0123456789abcdef", 200_000));

        var failure = Assert.Throws<SafetyScanException>(() => gate.Scan(pathological));
        Assert.Contains("decoder candidate scan", failure.Message);
        Assert.Contains("matcher timeout", failure.Message);
    }

    [Fact]
    public void DecodingIsExactlyOneLevelDeep()
    {
        var gate = new NeverStoreGate(_shippedRules);
        string once = Convert.ToBase64String(Encoding.UTF8.GetBytes(FakeAwsKeyId));
        string twice = Convert.ToBase64String(Encoding.UTF8.GetBytes(once));

        Assert.Equal(1, gate.Scan(once).RedactionCount);
        var doubled = gate.Scan(twice);
        Assert.Equal(0, doubled.RedactionCount);
        Assert.Equal(twice, doubled.Redacted);
    }

    [Fact]
    public void MalformedAndNonTextEncodingsAreIgnoredRatherThanGuessedAt()
    {
        var gate = new NeverStoreGate(_shippedRules);
        // Truncated base64, odd-length hex, and a decoded binary blob.
        string binary = Convert.ToBase64String([.. Enumerable.Range(0, 48).Select(i => (byte)i)]);
        foreach (string value in new[]
                 {
                     "%ZZ%ZZ%ZZ%ZZ malformed percent",
                     "abcdefabcdefabcdefa odd length hex",
                     binary
                 })
        {
            var result = gate.Scan(value);
            Assert.Equal(value, result.Redacted);
            Assert.Equal(0, result.RedactionCount);
        }
    }

    // --- AC7: every budget is a versioned constant that fails closed -------

    [Fact]
    public void MatchCountBudgetExhaustionFailsClosed()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxMatches = 2 });

        Assert.Equal(2, gate.Scan($"{FakeAwsKeyId} {FakeAwsKeyId}").RedactionCount);
        var failure = Assert.Throws<SafetyScanException>(
            () => gate.Scan($"{FakeAwsKeyId} {FakeAwsKeyId} {FakeAwsKeyId}"));
        Assert.Contains("match-count budget of 2", failure.Message);
    }

    [Fact]
    public void DecoderCandidateBudgetExhaustionFailsClosed()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxDecoderCandidates = 2 });
        string flood = string.Join(' ', Enumerable.Range(0, 64)
            .Select(index => Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"synthetic-candidate-{index:0000}"))));

        var failure = Assert.Throws<SafetyScanException>(() => gate.Scan(flood));
        Assert.Contains("decoder-candidate budget of 2", failure.Message);
    }

    [Fact]
    public void TotalDecodedByteBudgetExhaustionFailsClosed()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxDecodedBytes = 8 });
        string candidate = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("synthetic-decodable-payload-value"));

        var failure = Assert.Throws<SafetyScanException>(() => gate.Scan(candidate));
        Assert.Contains("total-decoded-byte budget of 8", failure.Message);
    }

    [Fact]
    public void TotalScanTimeBudgetExhaustionFailsClosed()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxScanTime = TimeSpan.Zero });

        var failure = Assert.Throws<SafetyScanException>(() => gate.Scan("anything at all"));
        Assert.Contains("total scan-time budget", failure.Message);
    }

    [Fact]
    public void PerRuleMatcherTimeoutFailsClosed()
    {
        var gate = new NeverStoreGate(
            _shippedRules,
            null,
            SafetyBudgets.Default with { MaxRuleTime = TimeSpan.FromTicks(1) });
        // Long enough that a linear-time matcher still cannot finish inside
        // one tick; the prefilter deliberately hits so a matcher does run.
        string pathological = string.Concat(Enumerable.Repeat("AKIA", 400_000));

        var failure = Assert.Throws<SafetyScanException>(() => gate.Scan(pathological));
        Assert.Contains("matcher timeout", failure.Message);
    }

    [Fact]
    public void ObservationBudgetIsTheVersionedCeilingNotTheTransportCap()
    {
        var gate = new NeverStoreGate(
            _shippedRules, null, SafetyBudgets.Default with { MaxObservationBytes = 16 });

        gate.AssertObservationWithinBudget(new string('a', 16));
        var failure = Assert.Throws<SafetyScanException>(
            () => gate.AssertObservationWithinBudget(new string('a', 17)));
        Assert.Contains("observation budget of 16 bytes", failure.Message);
    }

    // --- operator-provisioned exact credentials ----------------------------

    [Fact]
    public void OperatorLiteralsAreMatchedExactlyAndNeverAppearInDiagnostics()
    {
        // Synthetic, low-entropy on purpose: exact matching is the only
        // mechanism that can catch a value like this.
        const string configuredValue = "synthetic-operator-literal-0001";
        string literals = Path.Combine(_directory, "literals.txt");
        File.WriteAllText(
            literals, $"# operator-owned\n\n{configuredValue}\nsynthetic-second-0002\n");

        var gate = new NeverStoreGate(_shippedRules, literals);
        Assert.True(gate.IsConfigured, gate.FailureReason);
        Assert.Contains("literals:2", gate.RuleSetVersion);
        Assert.DoesNotContain(configuredValue, gate.RuleSetVersion);

        var result = gate.Scan($"the deploy used {configuredValue} last night");
        Assert.Equal("the deploy used [REDACTED:operator-literal] last night", result.Redacted);
        Assert.Equal(["configured_credential"], result.Categories);

        var rejection = Assert.Throws<NeverStoreException>(
            () => gate.AssertAllowed($"remember {configuredValue}"));
        Assert.Equal("operator-literal", rejection.RuleName);
        Assert.DoesNotContain(configuredValue, rejection.Message);
    }

    [Fact]
    public void ARefusalNamesTheHighestPriorityMatchNotTheOrdinalFirstRuleId()
    {
        const string configuredValue = "synthetic-operator-literal-0005";
        string literals = Path.Combine(_directory, "priority-literals.txt");
        File.WriteAllText(literals, configuredValue + "\n");
        var gate = new NeverStoreGate(_shippedRules, literals);

        // Two disjoint matches. "aws-access-key-id" sorts first ordinally, but
        // the operator literal is the highest-priority rule and is the one that
        // actually decided the refusal.
        string value = $"rotate {FakeAwsKeyId} and {configuredValue} tonight";
        var scan = gate.Scan(value);
        Assert.Equal(["aws-access-key-id", "operator-literal"], scan.RuleIds);
        Assert.Equal("operator-literal", scan.PrimaryRuleId);

        Assert.Equal(
            "operator-literal",
            Assert.Throws<NeverStoreException>(() => gate.AssertAllowed(value)).RuleName);
        Assert.Equal(
            "operator-literal",
            Assert.Throws<NeverStoreException>(
                () => gate.AssertAllowedObject(new { note = value })).RuleName);
    }

    [Fact]
    public void AbsentOrEmptyOperatorLiteralFileIsValidAndNotAFailClosedCondition()
    {
        string absent = Path.Combine(_directory, "no-such-literals.txt");
        var absentGate = new NeverStoreGate(_shippedRules, absent);
        Assert.True(absentGate.IsConfigured, absentGate.FailureReason);
        Assert.DoesNotContain("literals:", absentGate.RuleSetVersion);

        string empty = Path.Combine(_directory, "empty-literals.txt");
        File.WriteAllText(empty, "\n# only a comment\n\n");
        var emptyGate = new NeverStoreGate(_shippedRules, empty);
        Assert.True(emptyGate.IsConfigured, emptyGate.FailureReason);
    }

    [Fact]
    public void AnUnusablyShortOperatorLiteralFailsClosedWithoutQuotingIt()
    {
        const string tooShort = "abc";
        string literals = Path.Combine(_directory, "short-literals.txt");
        File.WriteAllText(literals, $"synthetic-operator-literal-0003\n{tooShort}\n");

        var gate = new NeverStoreGate(_shippedRules, literals);

        Assert.False(gate.IsConfigured);
        Assert.Contains("line 2", gate.FailureReason!);
        Assert.DoesNotContain(tooShort, gate.FailureReason!);
    }

    private static string SingleRule(string id, string prefilter, string pattern) =>
        $"""
        version: "test/{Guid.NewGuid():N}"
        rules:
          - id: {id}
            category: provider_token
            priority: 10
            prefilter: "{prefilter}"
            matcher: regex
            pattern: '{pattern}'
        """;

    // The distinctive middle of a synthetic credential, used to prove the
    // value itself does not survive redaction.
    private static string SecretCore(string value) =>
        value.Length <= 12 ? value : value.Substring(value.Length / 2, 6);
}
