using System.Text.Json;
using CaptureAdapters;
using MemSrv.Core;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class CaptureAdapterConformanceTests : HttpSeamTestBase
{
    [Fact]
    public void CodexNominalTextPartsWithoutExplicitStringTextRemainOpaqueEvidence()
    {
        const string record = """
            {
              "type": "response_item",
              "payload": {
                "type": "message",
                "role": "assistant",
                "content": [
                  {"type":"input_text","future":"first"},
                  {"type":"output_text","text":{"future":"second"}},
                  {"type":"text","text":42,"future":"third"}
                ]
              }
            }
            """;

        var terminal = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
            new CodexJsonlAdapter().Adapt(Source(record, isTerminal: true)));

        Assert.Equal(
            ["content/0:opaque", "content/1:opaque", "content/2:opaque"],
            terminal.Observation.Events.Select(item => item.PartKey));
        Assert.Equal(
            [0, 1, 2],
            terminal.Observation.Events.Select(item => item.PartOrder));
        Assert.All(terminal.Observation.Events, item =>
        {
            Assert.Equal("opaque", item.Kind);
            Assert.Equal("unknown", item.Actor);
            Assert.False(item.Payload.TryGetProperty("text", out _));
        });
        Assert.Equal(
            ["input_text", "output_text", "text"],
            terminal.Observation.Events.Select(
                item => item.Payload.GetProperty("contentType").GetString()));
        Assert.Equal(
            "first",
            terminal.Observation.Events[0].Payload.GetProperty("source")
                .GetProperty("future").GetString());
        Assert.Equal(
            "second",
            terminal.Observation.Events[1].Payload.GetProperty("source")
                .GetProperty("text").GetProperty("future").GetString());
        Assert.Equal(
            42,
            terminal.Observation.Events[2].Payload.GetProperty("source")
                .GetProperty("text").GetInt32());
    }

    [Fact]
    public void CodexEmptyMessageContentArrayRemainsOpaqueSourceShape()
    {
        const string record = """
            {
              "type": "response_item",
              "payload": {"type":"message","role":"developer","content":[]}
            }
            """;
        var adapter = new CodexJsonlAdapter();

        var terminal = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
            adapter.Adapt(Source(record, isTerminal: true)));
        CaptureEvent evidence = Assert.Single(terminal.Observation.Events);

        Assert.Equal("content:opaque", evidence.PartKey);
        Assert.Equal(0, evidence.PartOrder);
        Assert.Equal("opaque", evidence.Kind);
        Assert.Equal("unknown", evidence.Actor);
        Assert.Equal("message_content", evidence.Payload.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Array, evidence.Payload.GetProperty("source").ValueKind);
        Assert.Empty(evidence.Payload.GetProperty("source").EnumerateArray());
        Assert.False(evidence.Payload.TryGetProperty("text", out _));

        var retry = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
            adapter.Adapt(Source(record, isTerminal: true)));
        Assert.Equal(
            JsonSerializer.Serialize(terminal.Observation.Events, WebJson),
            JsonSerializer.Serialize(retry.Observation.Events, WebJson));
    }

    [Fact]
    public void CodexUnsupportedNonArrayMessageContentRemainsOpaqueEvidence()
    {
        string[] records =
        [
            """{"type":"response_item","payload":{"type":"message","role":"user"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":{"future":"object"}}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":42}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":true}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":null}}"""
        ];
        var adapter = new CodexJsonlAdapter();

        CaptureEvent[] evidence = records.Select(record =>
        {
            var terminal = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                adapter.Adapt(Source(record, isTerminal: true)));
            return Assert.Single(terminal.Observation.Events);
        }).ToArray();

        Assert.All(evidence, item =>
        {
            Assert.Equal("content:opaque", item.PartKey);
            Assert.Equal(0, item.PartOrder);
            Assert.Equal("opaque", item.Kind);
            Assert.Equal("unknown", item.Actor);
            Assert.Equal(
                "message_content",
                item.Payload.GetProperty("contentType").GetString());
            Assert.False(item.Payload.TryGetProperty("text", out _));
        });
        Assert.Equal(JsonValueKind.Object, evidence[0].Payload.GetProperty("source").ValueKind);
        Assert.False(evidence[0].Payload.GetProperty("source").TryGetProperty("content", out _));
        Assert.Equal(
            "object",
            evidence[1].Payload.GetProperty("source").GetProperty("future").GetString());
        Assert.Equal(42, evidence[2].Payload.GetProperty("source").GetInt32());
        Assert.True(evidence[3].Payload.GetProperty("source").GetBoolean());
        Assert.Equal(JsonValueKind.Null, evidence[4].Payload.GetProperty("source").ValueKind);
    }

    [Fact]
    public async Task CodexModelFacingMessagesFanOutInSourceOrderWhileUiViewsRemainAnnotations()
    {
        var adapter = new CodexJsonlAdapter();
        string fixture = Path.Combine(
            _root, "fixtures/adapter-conformance/codex-cli-0.144.messages.synthetic.jsonl");
        var source = await JsonlSourceReader.ReadAsync(
            fixture, "synthetic-codex-messages", terminalAtEndOfFile: true);
        var terminal = source.Select(adapter.Adapt)
            .Select(Assert.IsType<CaptureSourcePositionOutcome.Terminal>)
            .ToArray();

        Assert.Equal(6, terminal.Length);
        Assert.Equal("2", terminal[0].Observation.Adapter.Version);
        Assert.Equal(
            [2, 1, 1, 1, 2, 1],
            terminal.Select(outcome => outcome.Observation.Events.Count));
        Assert.Equal(
            ["message", "message"],
            terminal[0].Observation.Events.Select(item => item.Kind));
        Assert.Equal(
            ["user", "user"],
            terminal[0].Observation.Events.Select(item => item.Actor));
        Assert.Equal(
            ["content/0:message", "content/1:message"],
            terminal[0].Observation.Events.Select(item => item.PartKey));
        Assert.Equal(
            [0, 1],
            terminal[0].Observation.Events.Select(item => item.PartOrder));
        Assert.Equal(
            ["First user part.", "Second user part."],
            terminal[0].Observation.Events.Select(
                item => item.Payload.GetProperty("text").GetString()));

        Assert.Equal("annotation", terminal[1].Observation.Events[0].Kind);
        Assert.Equal("harness", terminal[1].Observation.Events[0].Actor);
        Assert.Equal("view:user_message", terminal[1].Observation.Events[0].PartKey);
        Assert.Equal(
            "user_message",
            terminal[1].Observation.Events[0].Payload.GetProperty("view").GetString());

        Assert.Equal("developer", terminal[2].Observation.Events[0].Actor);
        Assert.Equal(
            "Developer instruction.",
            terminal[2].Observation.Events[0].Payload.GetProperty("text").GetString());
        Assert.Equal("system", terminal[3].Observation.Events[0].Actor);
        Assert.Equal(
            "System instruction.",
            terminal[3].Observation.Events[0].Payload.GetProperty("text").GetString());

        Assert.Equal(
            ["assistant", "assistant"],
            terminal[4].Observation.Events.Select(item => item.Actor));
        Assert.Equal(
            ["content/0:message", "content/1:message"],
            terminal[4].Observation.Events.Select(item => item.PartKey));
        Assert.Equal(
            [0, 1],
            terminal[4].Observation.Events.Select(item => item.PartOrder));
        Assert.Equal("annotation", terminal[5].Observation.Events[0].Kind);
        Assert.Equal("view:agent_message", terminal[5].Observation.Events[0].PartKey);

        Assert.True(
            terminal[0].Observation.SourcePayload
                .GetProperty("additiveMessageFixtureField").GetProperty("retained").GetBoolean());
        Assert.Equal(
            "AKIA" + "SYNTHETICFIXTURE",
            terminal[0].Observation.SourcePayload.GetProperty("payload")
                .GetProperty("content")[0].GetProperty("futureContentField").GetString());
        Assert.Equal(
            "retained",
            terminal[3].Observation.SourcePayload.GetProperty("payload")
                .GetProperty("futureMessageField").GetString());
        Assert.False(
            terminal[3].Observation.Events[0].Payload.TryGetProperty(
                "futureMessageField", out _));

        string first = JsonSerializer.Serialize(
            terminal.Select(outcome => outcome.Observation.Events), WebJson);
        string retry = JsonSerializer.Serialize(
            source.Select(adapter.Adapt)
                .Cast<CaptureSourcePositionOutcome.Terminal>()
                .Select(outcome => outcome.Observation.Events),
            WebJson);
        Assert.Equal(first, retry);
    }

    [Fact]
    public async Task CodexAndClaudeFixturesSatisfyTheSameAdapterContract()
    {
        var cases = new[]
        {
            (
                Adapter: (ICaptureSourceAdapter)new CodexJsonlAdapter(),
                Fixture: Path.Combine(
                    _root, "fixtures/adapter-conformance/codex-cli-0.144.synthetic.jsonl")),
            (
                Adapter: (ICaptureSourceAdapter)new DisposableClaudeJsonlAdapter(),
                Fixture: Path.Combine(
                    _root, "fixtures/adapter-conformance/claude-code-2.1.201.synthetic.jsonl"))
        };
        string[] expectedKinds =
        [
            "message", "message", "tool_call", "tool_result", "tool_result",
            "error", "compaction", "lifecycle", "opaque"
        ];
        string[] expectedActors =
        [
            "user", "assistant", "assistant", "tool", "tool",
            "harness", "harness", "harness", "unknown"
        ];

        foreach (var (adapter, fixture) in cases)
        {
            var source = await JsonlSourceReader.ReadAsync(
                fixture, $"synthetic-{adapter.Harness}", terminalAtEndOfFile: true);
            var outcomes = source.Select(adapter.Adapt).ToArray();
            var terminal = outcomes
                .Select(outcome => Assert.IsType<CaptureSourcePositionOutcome.Terminal>(outcome))
                .ToArray();
            Assert.Equal(9, terminal.Length);
            Assert.Equal(
                Enumerable.Range(0, 9).Select(position => (long)position),
                terminal.Select(outcome => outcome.SourcePosition));
            Assert.Equal(
                expectedKinds,
                terminal.Select(outcome => Assert.Single(outcome.Observation.Events).Kind));
            Assert.Equal(
                expectedActors,
                terminal.Select(outcome => Assert.Single(outcome.Observation.Events).Actor));

            Assert.Equal(adapter.Harness, terminal[0].Observation.Source.Harness);
            Assert.Equal(
                "persisted_record", terminal[0].Observation.Source.MaterialKind);
            Assert.EndsWith(
                ".synthetic", terminal[0].Observation.Source.HarnessVersion);
            Assert.EndsWith(
                ".synthetic", terminal[1].Observation.Source.HarnessVersion);
            Assert.Null(terminal[0].Observation.Source.Model);
            Assert.Null(terminal[0].Observation.Source.Provider);
            Assert.NotNull(terminal[0].Observation.SourceTimestamp);
            Assert.Null(terminal[1].Observation.SourceTimestamp);
            Assert.Empty(terminal[0].Observation.Events[0].Relationships!);
            Assert.Equal(
                ["result_for"],
                terminal[3].Observation.Events[0].Relationships!.Select(value => value.Type));
            Assert.Equal(
                ["result_for"],
                terminal[4].Observation.Events[0].Relationships!.Select(value => value.Type));
            Assert.Equal(
                ["spawned_by", "parent_session"],
                terminal[7].Observation.Events[0].Relationships!.Select(value => value.Type));
            Assert.Equal(
                "unknown",
                terminal[8].Observation.Events[0].Actor);
            Assert.Equal(
                "retained",
                terminal[0].Observation.SourcePayload
                    .GetProperty("additiveFixtureField").GetString());
            Assert.Equal(
                "Inspect the synthetic workspace.",
                terminal[0].Observation.Events[0].Payload.GetProperty("text").GetString());
            Assert.Equal(
                "The synthetic workspace is ready.",
                terminal[1].Observation.Events[0].Payload.GetProperty("text").GetString());
            Assert.Equal(
                "pwd",
                terminal[2].Observation.Events[0].Payload
                    .GetProperty("arguments").GetProperty("command").GetString());
            Assert.Equal(
                "/synthetic/workspace",
                terminal[3].Observation.Events[0].Payload.GetProperty("output").GetString());
            JsonElement unknownSource = terminal[8].Observation.SourcePayload;
            JsonElement unknownEvidence = unknownSource.TryGetProperty(
                "syntheticValue", out var topLevelUnknown)
                ? topLevelUnknown
                : unknownSource.GetProperty("payload").GetProperty("syntheticValue");
            Assert.Equal("preserve me", unknownEvidence.GetString());
            if (adapter.Harness == "codex")
            {
                Assert.Equal("response_item", terminal[1].Observation.Source.RecordType);
                Assert.Equal(
                    "codex-synthetic-model", terminal[1].Observation.Source.Model);
                Assert.Equal(
                    "synthetic-provider", terminal[1].Observation.Source.Provider);
            }
            else
            {
                Assert.Equal("assistant", terminal[1].Observation.Source.RecordType);
                Assert.Equal(
                    "claude-synthetic-model", terminal[1].Observation.Source.Model);
                Assert.Null(terminal[1].Observation.Source.Provider);
            }

            string first = JsonSerializer.Serialize(
                terminal.Select(value => value.Observation.Events), WebJson);
            string second = JsonSerializer.Serialize(
                source.Select(adapter.Adapt)
                    .Cast<CaptureSourcePositionOutcome.Terminal>()
                    .Select(value => value.Observation.Events),
                WebJson);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void IncompleteAndMissingFactsNeverBecomeInferredTerminalFacts()
    {
        var adapters = new ICaptureSourceAdapter[]
        {
            new CodexJsonlAdapter(),
            new DisposableClaudeJsonlAdapter()
        };

        foreach (var adapter in adapters)
        {
            var incomplete = Source(
                """{"type":"future_record"}""", isTerminal: false);
            var deferred = Assert.IsType<CaptureSourcePositionOutcome.Incomplete>(
                adapter.Adapt(incomplete));
            Assert.Equal(0, deferred.SourcePosition);

            string missingFacts = adapter.Harness == "codex"
                ? """
                  {"type":"response_item","payload":{"type":"function_call_output","output":"synthetic"}}
                  """
                : """
                  {"type":"user","message":{"content":[{"type":"tool_result","content":"synthetic"}]}}
                  """;
            var terminal = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                adapter.Adapt(Source(missingFacts, isTerminal: true)));
            var result = Assert.Single(terminal.Observation.Events);
            Assert.Equal("tool_result", result.Kind);
            Assert.Equal("unknown", result.Payload.GetProperty("outcome").GetString());
            Assert.Empty(result.Relationships!);
            Assert.Null(terminal.Observation.Source.Model);
            Assert.Null(terminal.Observation.Source.Provider);
            Assert.Null(terminal.Observation.SourceTimestamp);

            string missingActor = adapter.Harness == "codex"
                ? """{"type":"response_item","payload":{"type":"message","content":"synthetic"}}"""
                : """{"type":"user","message":{"content":"synthetic"}}""";
            var actorOutcome = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                adapter.Adapt(Source(missingActor, isTerminal: true)));
            Assert.Equal(
                "unknown",
                Assert.Single(actorOutcome.Observation.Events).Actor);

            var hookSource = Source(missingFacts, isTerminal: true) with
            {
                MaterialKind = CaptureSourceMaterialKind.HookFact
            };
            var hookOutcome = Assert.IsType<CaptureSourcePositionOutcome.Terminal>(
                adapter.Adapt(hookSource));
            Assert.Equal("hook_fact", hookOutcome.Observation.Source.MaterialKind);
        }
    }

    [Fact]
    public async Task DisposableClaudeSpikeImportsThroughDisabledRuntimeAndCanonicalApi()
    {
        string credential = $"mcap_{Guid.NewGuid():N}";
        string stableName = $"claude-spike-{Guid.NewGuid():N}";
        string credentialPath = Path.Combine(
            Path.GetTempPath(), $"claude-spike-key-{Guid.NewGuid():N}");
        string stateDirectory = Path.Combine(
            Path.GetTempPath(), $"claude-spike-state-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(credentialPath, credential);
        try
        {
            string enrollment = await RunMemCtlAsync(
                "capture", "enroll", stableName,
                "--harness", "claude-code",
                "--agent-id", $"capture:{stableName}",
                "--credential-file", credentialPath);
            Assert.Contains("no supported capture adapter", enrollment);

            var adapter = new DisposableClaudeJsonlAdapter();
            string fixturePath = Path.Combine(
                _root, "fixtures/adapter-conformance/claude-code-2.1.201.synthetic.jsonl");
            string sourceStream = $"claude-spike-session-{Guid.NewGuid():N}";
            var state = new FileCaptureRuntimeState(stateDirectory);
            await CodexCaptureClaimer.ClaimCompletedAsync(
                adapter,
                fixturePath,
                sourceStream,
                state,
                SafetyGate());
            CaptureRuntimeStreamState stream = Assert.Single((await state.ReadAsync()).Streams);
            var receipts = await DisabledCaptureRuntime.RunClaimedFixtureAsync(
                adapter,
                fixturePath,
                sourceStream,
                stream.Queue,
                new Uri(_baseUrl),
                credential,
                SafetyGate(),
                (_, _, _) => Task.CompletedTask);
            Assert.Equal(9, receipts.Count);

            var canonical = receipts.Select(receipt =>
                    JsonDocument.Parse(receipt).RootElement.Clone())
                .ToArray();
            Assert.All(
                canonical,
                receipt => Assert.Equal("new", receipt.GetProperty("status").GetString()));
            Assert.Equal(
                "claude-code",
                canonical[0].GetProperty("observation").GetProperty("source")
                    .GetProperty("harness").GetString());
            Assert.Equal(
                "persisted_record",
                canonical[0].GetProperty("observation").GetProperty("source")
                    .GetProperty("materialKind").GetString());
            Assert.Equal(
                "retained",
                canonical[0].GetProperty("observation").GetProperty("safeSourcePayload")
                    .GetProperty("additiveFixtureField").GetString());
            Assert.Equal(
                "claude-synthetic-model",
                canonical[1].GetProperty("observation").GetProperty("source")
                    .GetProperty("model").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                canonical[1].GetProperty("observation").GetProperty("source")
                    .GetProperty("provider").ValueKind);
            Assert.Equal(
                "tool_result",
                Assert.Single(canonical[4].GetProperty("events").EnumerateArray())
                    .GetProperty("kind").GetString());
            Assert.Equal(
                "failed",
                Assert.Single(canonical[4].GetProperty("events").EnumerateArray())
                    .GetProperty("payload").GetProperty("outcome").GetString());

            Guid unknownObservation = canonical[8].GetProperty("observationUuid").GetGuid();
            var envelope = JsonDocument.Parse(
                await RunMemCtlAsync("capture", "receipt", unknownObservation.ToString()))
                .RootElement;
            Assert.Equal(
                "opaque",
                envelope.GetProperty("event").GetProperty("kind").GetString());
            Assert.Equal(
                "future_transcript_record",
                envelope.GetProperty("observation").GetProperty("source")
                    .GetProperty("recordType").GetString());
        }
        finally
        {
            File.Delete(credentialPath);
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    private static TrustedSourceObservation Source(string json, bool isTerminal) =>
        new(
            "synthetic-source-session",
            SourcePosition: 0,
            new CaptureSourceLocator.NativeId("synthetic-native-record"),
            CaptureSourceMaterialKind.PersistedRecord,
            JsonDocument.Parse(json).RootElement.Clone(),
            isTerminal);

    private static JsonSerializerOptions WebJson { get; } =
        new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Deliberately disposable Claude Code parser spike. It is test evidence only:
/// this test assembly is not referenced by the tracer or any release project.
/// </summary>
internal sealed class DisposableClaudeJsonlAdapter : ICaptureSourceAdapter
{
    public string Harness => "claude-code";
    public CaptureAdapter Identity { get; } = new("claude-code-jsonl-disposable-spike", "1");

    public CaptureSourcePositionOutcome Adapt(TrustedSourceObservation source)
    {
        if (!source.IsTerminal)
        {
            return new CaptureSourcePositionOutcome.Incomplete(
                source.SourcePosition, "source record may still be extended");
        }

        JsonElement record = source.SourcePayload;
        string? recordType = String(record, "type");
        string? version = StringOrObject(record, "version");
        string? provider = StringOrObject(record, "provider");
        string? model = null;
        JsonElement message = record.TryGetProperty("message", out var messageValue)
            ? messageValue
            : default;
        if (message.ValueKind == JsonValueKind.Object)
        {
            model = StringOrObject(message, "model");
        }

        var request = new CaptureObservationRequest(
            ContractVersion: 1,
            source.SourceSessionId,
            source.SourcePosition,
            WireLocator(source.Locator),
            Timestamp(record),
            new CaptureSource(
                Harness,
                version,
                recordType,
                model,
                provider,
                MaterialKind(source.MaterialKind)),
            Identity,
            record.Clone(),
            Events(record, recordType));
        return new CaptureSourcePositionOutcome.Terminal(source.SourcePosition, request);
    }

    private static IReadOnlyList<CaptureEvent> Events(JsonElement record, string? recordType)
    {
        if (recordType is "user" or "assistant"
            && record.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            return MessageContent(message, content);
        }

        if (string.Equals(recordType, "system", StringComparison.Ordinal))
        {
            return String(record, "subtype") switch
            {
                "api_error" => [Event(
                    "error/0", 0, "error", "harness",
                    new
                    {
                        error = TextProperty(record, "error"),
                        outcome = String(record, "outcome") ?? "unknown"
                    })],
                "compact_boundary" => [Compaction(record)],
                "subagent_start" => [Subagent(record)],
                _ => [Opaque(recordType, record)]
            };
        }

        return [Opaque(recordType, record)];
    }

    private static IReadOnlyList<CaptureEvent> MessageContent(
        JsonElement message, JsonElement content)
    {
        string actor = String(message, "role") ?? "unknown";
        if (content.ValueKind != JsonValueKind.Array)
        {
            return [Event("content/0:message", 0, "message", actor, new { text = Text(content) })];
        }

        var events = new List<CaptureEvent>();
        int index = 0;
        foreach (var block in content.EnumerateArray())
        {
            string? type = block.ValueKind == JsonValueKind.Object ? String(block, "type") : null;
            events.Add(type switch
            {
                "text" => Event(
                    $"content/{index}:message", index, "message", actor,
                    new { text = TextProperty(block, "text") }),
                "tool_use" => ToolCall(block, index),
                "tool_result" => ToolResult(block, index),
                _ => Event(
                    $"content/{index}:opaque", index, "opaque", "unknown",
                    new { blockType = type, source = block.Clone() })
            });
            index++;
        }
        return events;
    }

    private static CaptureEvent ToolCall(JsonElement block, int index)
    {
        JsonElement input = block.TryGetProperty("input", out var value)
            ? ObjectOrParsedString(value)
            : JsonSerializer.SerializeToElement<object?>(null);
        return Event(
            $"content/{index}:tool_call", index, "tool_call", "assistant",
            new
            {
                callId = String(block, "id"),
                tool = String(block, "name"),
                arguments = input
            });
    }

    private static CaptureEvent ToolResult(JsonElement block, int index)
    {
        string? callId = String(block, "tool_use_id");
        string outcome = block.TryGetProperty("is_error", out var isError)
            && isError.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isError.GetBoolean() ? "failed" : "succeeded"
                : "unknown";
        IReadOnlyList<CaptureRelationship> relationships = callId is null
            ? []
            : [Relationship("result_for", callId, "tool_call")];
        return Event(
            $"content/{index}:tool_result", index, "tool_result", "tool",
            new
            {
                callId,
                outcome,
                output = block.TryGetProperty("content", out var content)
                    ? Text(content)
                    : null
            },
            relationships);
    }

    private static CaptureEvent Compaction(JsonElement record)
    {
        JsonElement metadata = record.TryGetProperty("compactMetadata", out var value)
            ? value
            : default;
        return Event(
            "compaction/0", 0, "compaction", "harness",
            new
            {
                trigger = metadata.ValueKind == JsonValueKind.Object
                    ? String(metadata, "trigger")
                    : metadata.ValueKind == JsonValueKind.String ? metadata.GetString() : null,
                outcome = metadata.ValueKind == JsonValueKind.Object
                    ? String(metadata, "outcome") ?? "unknown"
                    : "unknown",
                summary = metadata.ValueKind == JsonValueKind.Object
                    ? TextProperty(metadata, "summary")
                    : null,
                metrics = metadata.ValueKind == JsonValueKind.Object
                    ? metadata.Clone()
                    : (JsonElement?)null
            });
    }

    private static CaptureEvent Subagent(JsonElement record)
    {
        var relationships = new List<CaptureRelationship>();
        if (String(record, "spawnedBy") is { } spawnedBy)
        {
            relationships.Add(Relationship("spawned_by", spawnedBy, "tool_call"));
        }
        if (String(record, "parentSessionId") is { } parent)
        {
            relationships.Add(Relationship("parent_session", parent, "session"));
        }
        return Event(
            "subagent/0", 0, "lifecycle", "harness",
            new
            {
                action = "subagent_start",
                childId = String(record, "agentId"),
                agentType = String(record, "agentType")
            },
            relationships);
    }

    private static CaptureEvent Opaque(string? recordType, JsonElement record) =>
        Event(
            "opaque/0", 0, "opaque", "unknown",
            new { recordType, source = record.Clone() });

    private static CaptureEvent Event(
        string partKey,
        int partOrder,
        string kind,
        string actor,
        object payload,
        IReadOnlyList<CaptureRelationship>? relationships = null) =>
        new(
            partKey,
            partOrder,
            kind,
            actor,
            JsonSerializer.SerializeToElement(payload, WebJson),
            OccurredAt: null,
            relationships ?? []);

    private static CaptureRelationship Relationship(string type, string nativeId, string kind) =>
        new(type, new CaptureRelationshipTarget(null, nativeId, kind));

    private static CaptureSourceTimestamp? Timestamp(JsonElement record)
    {
        if (String(record, "timestamp") is not { } raw)
        {
            return null;
        }
        return new CaptureSourceTimestamp(
            raw,
            DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null);
    }

    private static string? String(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? StringOrObject(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => String(value, "name"),
            _ => null
        };
    }

    private static string? TextProperty(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) ? Text(value) : null;

    private static string Text(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => string.Join(
                "\n",
                value.EnumerateArray().Select(Text).Where(text => text.Length > 0)),
            JsonValueKind.Object when String(value, "text") is { } text => text,
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };

    private static JsonElement ObjectOrParsedString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return value.Clone();
        }
        try
        {
            return JsonDocument.Parse(value.GetString() ?? "").RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value.GetString(), WebJson);
        }
    }

    private static CaptureLocator WireLocator(CaptureSourceLocator locator) =>
        locator switch
        {
            CaptureSourceLocator.NativeId native =>
                new CaptureLocator("native_id", native.Value, null, null, null),
            CaptureSourceLocator.ByteRange range =>
                new CaptureLocator(
                    "byte_range", null, range.Offset, range.Length, range.SourceContentSha256),
            _ => throw new InvalidOperationException("Unknown source locator variant.")
        };

    private static string MaterialKind(CaptureSourceMaterialKind kind) =>
        kind switch
        {
            CaptureSourceMaterialKind.PersistedRecord => "persisted_record",
            CaptureSourceMaterialKind.HookFact => "hook_fact",
            _ => throw new InvalidOperationException("Unknown source material kind.")
        };

    private static JsonSerializerOptions WebJson { get; } =
        new(JsonSerializerDefaults.Web);
}
