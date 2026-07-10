using Dapper;
using MemSrv.Core;
using Npgsql;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MemSrv.Tests;

[Collection("database")]
public sealed class MemoryServiceTests : IAsyncLifetime
{
    private const string AdminConnection = "Host=127.0.0.1;Port=55432;Database=memory_test;Username=overmind;Password=overmind_dev";
    private const string RuntimeConnection = "Host=127.0.0.1;Port=55432;Database=memory_test;Username=memsrv;Password=memsrv_dev";
    private readonly string _root = FindRepoRoot();
    private readonly List<string> _serverErrorLines = [];

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await connection.ExecuteAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        await connection.ExecuteAsync("GRANT ALL ON SCHEMA public TO overmind;");
        DatabaseMigrator.Migrate(AdminConnection, Path.Combine(_root, "migrations"), logToConsole: false);
        await connection.ExecuteAsync("ALTER ROLE memsrv LOGIN PASSWORD 'memsrv_dev';");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TraceMutationFailsByTrigger()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-grants");
        var trace = await service.LogTraceAsync(context, context.SessionId, "note", new { ok = true });

        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        var triggerError = await Assert.ThrowsAsync<PostgresException>(() =>
            admin.ExecuteAsync("UPDATE traces SET event_type = 'note' WHERE trace_uuid = @TraceUuid", new { trace.Data.TraceUuid }));
        Assert.Contains("traces are append-only", triggerError.MessageText, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("P0001", triggerError.SqlState);
    }

    [Fact]
    public async Task PrivateMemoriesAreOwnerScoped()
    {
        var service = Service();
        var owner = new MemoryContext("agent-a", "memory-system", "session-owner");
        var other = new MemoryContext("agent-b", "memory-system", "session-other");

        var saved = await service.SaveNoteAsync(owner, "memory-system", "note", "private quartz calibration note");

        var ownerResults = await service.SearchMemoryAsync(owner, "quartz");
        var otherResults = await service.SearchMemoryAsync(other, "quartz");

        Assert.Contains(ownerResults.Data, result => result.Uuid == saved.Data.Uuid);
        Assert.DoesNotContain(otherResults.Data, result => result.Uuid == saved.Data.Uuid);
    }

    [Fact]
    public async Task SharedMemoryIsProposedUntilApproved()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-approval");

        var proposed = await service.ProposeMemoryAsync(
            context,
            "memory-system",
            "decision",
            "Use Dapper with handwritten SQL for memory persistence",
            "human",
            "test-source");

        Assert.Equal("proposed", proposed.Data.Status);
        var beforeApproval = await service.SearchMemoryAsync(context, "Dapper handwritten");
        Assert.DoesNotContain(beforeApproval.Data, result => result.Uuid == proposed.Data.Uuid);

        await service.ApproveAsync(proposed.Data.Uuid, "test-operator");
        var afterApproval = await service.SearchMemoryAsync(context, "Dapper handwritten");

        var result = Assert.Single(afterApproval.Data, result => result.Uuid == proposed.Data.Uuid);
        Assert.Equal("approved", result.Status);
        Assert.Contains("fts", result.LaneScores.Keys);
        Assert.Contains("recency", result.LaneScores.Keys);
    }

    [Fact]
    public async Task GetByIdLogsMemoryConsumed()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-consumed");
        var proposed = await service.ProposeMemoryAsync(context, "memory-system", "fact", "Consumed records are traced", "human", "test-source");
        await service.ApproveAsync(proposed.Data.Uuid, "test-operator");

        var record = await service.GetByIdAsync(context, proposed.Data.Uuid);
        var trace = await service.TraceAsync(context.SessionId);

        Assert.Equal(proposed.Data.Uuid, record.Data.Uuid);
        Assert.Contains(trace, row => row.EventType == "memory_consumed" && row.Refs?.Contains(proposed.Data.Uuid) == true);
    }

    [Fact]
    public async Task NeverStoreBlocksWriteAndLogsRedactedNote()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-never-store");

        var ex = await Assert.ThrowsAsync<NeverStoreException>(() =>
            service.ProposeMemoryAsync(
                context,
                "memory-system",
                "fact",
                "Synthetic credential AKIA1234567890ABCDEF must not persist",
                "human",
                "test-source"));

        Assert.Equal("aws-access-key-id", ex.RuleName);
        var trace = await service.TraceAsync(context.SessionId);
        var note = Assert.Single(trace, row => row.EventType == "note");
        Assert.Contains("aws-access-key-id", note.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA1234567890ABCDEF", note.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraceWriteRedactsNeverStoreMatches()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-trace-redact");

        var trace = await service.LogTraceAsync(
            context,
            context.SessionId,
            "tool_result",
            new { token = "bearer abcdefghijklmnopqrstuvwxyz123456" });

        var rows = await service.TraceAsync(context.SessionId);
        var row = Assert.Single(rows, item => item.TraceUuid == trace.Data.TraceUuid);
        Assert.Contains("[REDACTED:bearer-token]", row.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", row.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewEventsUseSyntheticSessionAndReviewerIdentity()
    {
        var service = Service();
        var proposer = new MemoryContext("agent-a", "memory-system", "session-review-source");
        var source = await service.LogTraceAsync(proposer, proposer.SessionId, "assistant_msg", new { text = "review provenance source" });
        var proposed = await service.ProposeMemoryAsync(
            proposer,
            "memory-system",
            "warning",
            "Review events must use reviewer identity",
            "trace",
            source.Data.TraceUuid.ToString());

        await service.ApproveAsync(proposed.Data.Uuid, "reviewer-one");

        var reviewTrace = await service.TraceAsync($"review:{proposed.Data.Uuid}");
        var approval = Assert.Single(reviewTrace, row => row.EventType == "approval");
        Assert.Equal("human:reviewer-one", approval.AgentId);
        Assert.NotEqual(proposer.AgentId, approval.AgentId);
        Assert.Contains(proposed.Data.Uuid, approval.Refs ?? []);
        Assert.Contains(source.Data.TraceUuid, approval.Refs ?? []);
        Assert.Contains("\"reviewer\": \"human:reviewer-one\"", approval.Content, StringComparison.Ordinal);
        Assert.Contains("\"amended\": false", approval.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemCtlApproveWithoutReviewerFails()
    {
        var service = Service();
        var proposer = new MemoryContext("agent-a", "memory-system", "session-anonymous-approval");
        var proposed = await service.ProposeMemoryAsync(proposer, "memory-system", "decision", "Anonymous approval must fail", "human", "test-source");

        var result = await RunMemCtlForResultAsync("approve", proposed.Data.Uuid.ToString());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--by is required", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectionUsesSyntheticSessionAndReviewerIdentity()
    {
        var service = Service();
        var proposer = new MemoryContext("agent-a", "memory-system", "session-reject-source");
        var source = await service.LogTraceAsync(proposer, proposer.SessionId, "assistant_msg", new { text = "reject provenance source" });
        var proposed = await service.ProposeMemoryAsync(
            proposer,
            "memory-system",
            "open_question",
            "Should rejected memories keep review provenance?",
            "trace",
            source.Data.TraceUuid.ToString());

        await service.RejectAsync(proposed.Data.Uuid, "reviewer-two", "not durable enough");

        var reviewTrace = await service.TraceAsync($"review:{proposed.Data.Uuid}");
        var rejection = Assert.Single(reviewTrace, row => row.EventType == "rejection");
        Assert.Equal("human:reviewer-two", rejection.AgentId);
        Assert.NotEqual(proposer.AgentId, rejection.AgentId);
        Assert.Contains(proposed.Data.Uuid, rejection.Refs ?? []);
        Assert.Contains(source.Data.TraceUuid, rejection.Refs ?? []);
        Assert.Contains("not durable enough", rejection.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryRowsCarryContentHashMetadataAndV11Types()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-schema-v11");

        foreach (var type in new[] { "constraint", "open_question", "warning" })
        {
            var proposed = await service.ProposeMemoryAsync(context, "memory-system", type, $"schema v11 {type}", "human", "test-source");
            var record = await service.ShowAsync(proposed.Data.Uuid);

            Assert.Equal(64, record.ContentHash.Length);
            Assert.True(record.ContentHash.All(Uri.IsHexDigit));
            Assert.Equal(JsonValueKind.Object, record.Metadata.ValueKind);
        }
    }

    [Fact]
    public async Task LogTraceToExplicitAllowedNamespaceLandsThere()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", new[] { "memory-system", "homelab" }, "session-ns-explicit");

        var trace = await service.LogTraceAsync(context, context.SessionId, "note", new { ok = true }, refs: null, @namespace: "homelab");

        var rows = await service.TraceAsync(context.SessionId);
        var row = Assert.Single(rows, r => r.TraceUuid == trace.Data.TraceUuid);
        Assert.Equal("homelab", row.Namespace);
    }

    [Fact]
    public async Task LogTraceToForbiddenNamespaceIsRejectedNamingIt()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", new[] { "memory-system" }, "session-ns-forbidden");

        var ex = await Assert.ThrowsAsync<NamespaceForbiddenException>(() =>
            service.LogTraceAsync(context, context.SessionId, "note", new { ok = true }, refs: null, @namespace: "secret-ops"));

        Assert.Equal("secret-ops", ex.Namespace);
        Assert.Contains("secret-ops", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await service.TraceAsync(context.SessionId));
    }

    [Fact]
    public async Task UnqualifiedLogTraceLandsInDefaultNamespace()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", new[] { "memory-system", "homelab" }, "session-ns-default");

        var trace = await service.LogTraceAsync(context, context.SessionId, "note", new { ok = true });

        var row = Assert.Single(await service.TraceAsync(context.SessionId), r => r.TraceUuid == trace.Data.TraceUuid);
        Assert.Equal("memory-system", row.Namespace);
    }

    [Fact]
    public async Task SearchWithForbiddenNamespaceIsRejectedNamingIt()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", new[] { "memory-system" }, "session-ns-search-forbidden");

        var ex = await Assert.ThrowsAsync<NamespaceForbiddenException>(() =>
            service.SearchMemoryAsync(context, "anything", namespaces: ["homelab"]));

        Assert.Equal("homelab", ex.Namespace);
        Assert.Contains("homelab", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAcrossAllowedNamespacesReturnsBoth()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", new[] { "memory-system", "homelab" }, "session-ns-search-allowed");

        var here = await service.ProposeMemoryAsync(context, "memory-system", "fact", "Crossns alpha marker", "human", "test-source");
        var there = await service.ProposeMemoryAsync(context, "homelab", "fact", "Crossns beta marker", "human", "test-source");
        await service.ApproveAsync(here.Data.Uuid, "test-operator");
        await service.ApproveAsync(there.Data.Uuid, "test-operator");

        var results = await service.SearchMemoryAsync(context, "Crossns marker", namespaces: ["memory-system", "homelab"]);

        Assert.Contains(results.Data, r => r.Uuid == here.Data.Uuid);
        Assert.Contains(results.Data, r => r.Uuid == there.Data.Uuid);
    }

    [Fact]
    public async Task ProposeToForbiddenNamespaceIsRejectedNamingIt()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", new[] { "memory-system" }, "session-ns-propose-forbidden");

        var ex = await Assert.ThrowsAsync<NamespaceForbiddenException>(() =>
            service.ProposeMemoryAsync(context, "homelab", "fact", "Should never land", "human", "test-source"));

        Assert.Equal("homelab", ex.Namespace);
    }

    [Fact]
    public async Task GetByIdRejectsMemoryOutsideAllowlist()
    {
        var service = Service();
        var writer = new MemoryContext("agent-a", "homelab", new[] { "homelab" }, "session-ns-getbyid-writer");
        var proposed = await service.ProposeMemoryAsync(writer, "homelab", "fact", "Foreign namespace secret", "human", "test-source");
        await service.ApproveAsync(proposed.Data.Uuid, "test-operator");

        var outsider = new MemoryContext("agent-a", "memory-system", new[] { "memory-system" }, "session-ns-getbyid-outsider");

        var ex = await Assert.ThrowsAsync<NamespaceForbiddenException>(() => service.GetByIdAsync(outsider, proposed.Data.Uuid));
        Assert.Equal("homelab", ex.Namespace);
    }

    [Fact]
    public async Task SearchDefaultsToCurrentNamespace()
    {
        var service = Service();
        var memorySystem = new MemoryContext("agent-a", "memory-system", "session-ns-a");
        var homelab = new MemoryContext("agent-a", "homelab", "session-ns-b");

        var memorySystemMemory = await service.ProposeMemoryAsync(memorySystem, "memory-system", "fact", "Namespace alpha marker", "human", "test-source");
        var homelabMemory = await service.ProposeMemoryAsync(homelab, "homelab", "fact", "Namespace beta marker", "human", "test-source");
        await service.ApproveAsync(memorySystemMemory.Data.Uuid, "test-operator");
        await service.ApproveAsync(homelabMemory.Data.Uuid, "test-operator");

        var results = await service.SearchMemoryAsync(memorySystem, "Namespace marker");

        Assert.Contains(results.Data, result => result.Uuid == memorySystemMemory.Data.Uuid);
        Assert.DoesNotContain(results.Data, result => result.Uuid == homelabMemory.Data.Uuid);
    }

    [Fact]
    public async Task StdioServerStartupDoesNotWriteNonProtocolStdout()
    {
        using var process = StartServerProcess();
        try
        {
            var lineTask = process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(2)));

            if (completed == lineTask)
            {
                var line = await lineTask;
                Assert.True(IsJsonRpcLine(line), $"Unexpected non-protocol stdout during startup: {line}");
            }
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    [Fact]
    public async Task McpStdioSmokeTestRunsSession1DefinitionOfDone()
    {
        var sessionId = $"session-smoke-{Guid.NewGuid():N}";
        var reviewer = $"reviewer-{Guid.NewGuid():N}";
        await using var client = await CreateMcpClientAsync(sessionId);

        var trace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = "Session 1 MCP smoke trace" }
        });
        var traceUuid = trace.GetProperty("data").GetProperty("traceUuid").GetGuid();
        AssertNext(trace);

        var uniqueTerm = $"mcp-smoke-{Guid.NewGuid():N}";
        var proposed = await CallToolAsync(client, "propose_memory", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "decision",
            ["content"] = $"Session 1 DoD smoke memory {uniqueTerm}",
            ["source_type"] = "trace",
            ["source_id"] = traceUuid.ToString()
        });
        var memoryUuid = proposed.GetProperty("data").GetProperty("uuid").GetGuid();
        Assert.Equal("proposed", proposed.GetProperty("data").GetProperty("status").GetString());
        AssertNext(proposed);

        await RunMemCtlAsync("approve", memoryUuid.ToString(), "--by", reviewer);

        var search = await CallToolAsync(client, "search_memory", new Dictionary<string, object?>
        {
            ["query"] = uniqueTerm,
            ["limit"] = 5
        });
        AssertNext(search);
        var found = search.GetProperty("data").EnumerateArray().Single(result => result.GetProperty("uuid").GetGuid() == memoryUuid);
        Assert.True(found.GetProperty("laneScores").TryGetProperty("fts", out _));
        Assert.True(found.GetProperty("laneScores").TryGetProperty("recency", out _));

        var fetched = await CallToolAsync(client, "get_by_id", new Dictionary<string, object?>
        {
            ["uuid"] = memoryUuid
        });
        AssertNext(fetched);
        Assert.Equal(memoryUuid, fetched.GetProperty("data").GetProperty("uuid").GetGuid());

        var traceRows = await Service().TraceAsync(sessionId);
        Assert.Contains(traceRows, row => row.EventType == "memory_consumed" && row.Refs?.Contains(memoryUuid) == true);

        var reviewRows = await Service().TraceAsync($"review:{memoryUuid}");
        var approval = Assert.Single(reviewRows, row => row.EventType == "approval");
        Assert.Equal($"human:{reviewer}", approval.AgentId);
        Assert.NotEqual("agent-a", approval.AgentId);
        Assert.Contains(traceUuid, approval.Refs ?? []);
    }

    [Fact]
    public async Task MemctlMigrateAcceptsPostgresUrlAdminConnectionString()
    {
        var env = new Dictionary<string, string>
        {
            ["MEMSRV_ADMIN_CONNECTION_STRING"] = "postgres://overmind:overmind_dev@127.0.0.1:55432/memory_test"
        };

        var result = await RunMemCtlForResultAsync(env, "migrate");

        Assert.True(result.ExitCode == 0, $"memctl migrate failed with exit {result.ExitCode}. stdout={result.Stdout} stderr={result.Stderr}");
        Assert.Contains("migrations applied", result.Stdout);
    }

    [Fact]
    public async Task MemCtlRetireFlipsStatusKeepsRowAndLeavesRetrieval()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-retire");
        var term = $"retire-marker-{Guid.NewGuid():N}";
        var proposed = await service.ProposeMemoryAsync(context, "memory-system", "fact", $"Stale fact {term}", "human", "test-source");
        await service.ApproveAsync(proposed.Data.Uuid, "test-operator");

        var retire = await RunMemCtlForResultAsync("retire", proposed.Data.Uuid.ToString());
        Assert.True(retire.ExitCode == 0, $"retire failed: {retire.Stderr}");
        Assert.Contains($"retired {proposed.Data.Uuid}", retire.Stdout, StringComparison.Ordinal);

        var show = await RunMemCtlForResultAsync("show", proposed.Data.Uuid.ToString());
        Assert.Equal(0, show.ExitCode);
        Assert.Contains("shared/retired", show.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("retired=<none>", show.Stdout, StringComparison.Ordinal);

        var afterRetire = await service.SearchMemoryAsync(context, term);
        Assert.DoesNotContain(afterRetire.Data, result => result.Uuid == proposed.Data.Uuid);
    }

    [Fact]
    public async Task MemCtlWhyWalksSourceTraceChainAcrossSupersession()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", "session-why-source");
        var firstMarker = $"why-v1-{Guid.NewGuid():N}";
        var secondMarker = $"why-v2-{Guid.NewGuid():N}";

        var firstTrace = await service.LogTraceAsync(context, context.SessionId, "assistant_msg", new { text = firstMarker });
        var firstMemory = await service.ProposeMemoryAsync(context, "memory-system", "decision", "Original belief", "trace", firstTrace.Data.TraceUuid.ToString());
        await service.ApproveAsync(firstMemory.Data.Uuid, "test-operator");

        var secondTrace = await service.LogTraceAsync(context, context.SessionId, "assistant_msg", new { text = secondMarker });
        var secondMemory = await service.ProposeMemoryAsync(context, "memory-system", "decision", "Revised belief", "trace", secondTrace.Data.TraceUuid.ToString(), firstMemory.Data.Uuid);
        await service.ApproveAsync(secondMemory.Data.Uuid, "test-operator");

        var why = await RunMemCtlForResultAsync("why", secondMemory.Data.Uuid.ToString());

        Assert.True(why.ExitCode == 0, $"why failed: {why.Stderr}");
        Assert.Contains(secondMemory.Data.Uuid.ToString(), why.Stdout, StringComparison.Ordinal);
        Assert.Contains(secondTrace.Data.TraceUuid.ToString(), why.Stdout, StringComparison.Ordinal);
        Assert.Contains(secondMarker, why.Stdout, StringComparison.Ordinal);
        Assert.Contains(firstMemory.Data.Uuid.ToString(), why.Stdout, StringComparison.Ordinal);
        Assert.Contains(firstTrace.Data.TraceUuid.ToString(), why.Stdout, StringComparison.Ordinal);
        Assert.Contains(firstMarker, why.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemCtlConsumedListsReadMemoriesWithResolvableSource()
    {
        var service = Service();
        var context = new MemoryContext("agent-a", "memory-system", $"session-consumed-{Guid.NewGuid():N}");
        var sourceTrace = await service.LogTraceAsync(context, context.SessionId, "assistant_msg", new { text = "consumed source" });
        var proposed = await service.ProposeMemoryAsync(context, "memory-system", "fact", "A read fact", "trace", sourceTrace.Data.TraceUuid.ToString());
        await service.ApproveAsync(proposed.Data.Uuid, "test-operator");
        await service.GetByIdAsync(context, proposed.Data.Uuid);

        var consumed = await RunMemCtlForResultAsync("consumed", context.SessionId);

        Assert.True(consumed.ExitCode == 0, $"consumed failed: {consumed.Stderr}");
        Assert.Contains(proposed.Data.Uuid.ToString(), consumed.Stdout, StringComparison.Ordinal);
        Assert.Contains(sourceTrace.Data.TraceUuid.ToString(), consumed.Stdout, StringComparison.Ordinal);
    }

    private MemoryService Service() =>
        new(RuntimeConnection, new NeverStoreGate(Path.Combine(_root, "config/never_store.yaml")));

    private async Task<McpClient> CreateMcpClientAsync(string sessionId)
    {
        var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        env["MEMSRV_CONNECTION_STRING"] = RuntimeConnection;
        env["MEMSRV_AGENT_ID"] = "agent-a";
        env["MEMSRV_NAMESPACE"] = "memory-system";
        env["MEMSRV_SESSION_ID"] = sessionId;

        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = ["run", "--project", Path.Combine(_root, "src/MemSrv.Server/MemSrv.Server.csproj"), "--no-build"],
            WorkingDirectory = _root,
            Name = "MemSrv.Server",
            InheritEnvironmentVariables = false,
            EnvironmentVariables = env,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line => _serverErrorLines.Add(line)
        }));
    }

    private async Task<JsonElement> CallToolAsync(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(toolName, arguments);
        var content = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, $"{toolName} returned MCP error: {content}{Environment.NewLine}{string.Join(Environment.NewLine, _serverErrorLines)}");

        var json = result.StructuredContent ?? JsonDocument.Parse(((TextContentBlock)Assert.Single(result.Content)).Text).RootElement;
        return json.Clone();
    }

    private Process StartServerProcess()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "src/MemSrv.Server/MemSrv.Server.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.Environment["MEMSRV_CONNECTION_STRING"] = RuntimeConnection;
        startInfo.Environment["MEMSRV_AGENT_ID"] = "agent-a";
        startInfo.Environment["MEMSRV_NAMESPACE"] = "memory-system";
        startInfo.Environment["MEMSRV_SESSION_ID"] = "session-stdout";

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start MemSrv.Server.");
    }

    private async Task RunMemCtlAsync(params string[] args)
    {
        var result = await RunMemCtlForResultAsync(args);
        Assert.True(result.ExitCode == 0, $"memctl failed with exit {result.ExitCode}. stdout={result.Stdout} stderr={result.Stderr}");
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunMemCtlForResultAsync(params string[] args) =>
        RunMemCtlForResultAsync(extraEnvironment: null, args);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunMemCtlForResultAsync(
        IReadOnlyDictionary<string, string>? extraEnvironment, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "src/MemCtl/MemCtl.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["MEMSRV_CONNECTION_STRING"] = RuntimeConnection;
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start memctl.");
        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static void AssertNext(JsonElement envelope)
    {
        Assert.True(envelope.TryGetProperty("next", out var next), "Tool response did not include next.");
        Assert.False(string.IsNullOrWhiteSpace(next.GetString()));
    }

    private static bool IsJsonRpcLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("jsonrpc", out var jsonRpc) && jsonRpc.GetString() == "2.0";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
        process.Dispose();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repo root.");
    }
}
