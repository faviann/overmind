using Dapper;
using Npgsql;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemSrv.Tests;

// Slice 7 (issue #9): the spec §10 acceptance suite, consolidated. One
// end-to-end scenario is seeded through the public surface (MCP tools as keyed
// agents over HTTP, memctl as the operator), then the four provenance
// questions are asserted as queries: memctl where a command exists (consumed,
// why, show, trace), SQL where none does — the source_id listing, which spec
// §10.2 requires to work now, and the FTS-over-consumed-set join, per the
// issue's "assert the four provenance questions as queries". Mechanical checks
// follow docs/testing.md: direct DB access only where the database mechanism IS the
// spec'd behavior (trigger + grants append-only, content_hash validity);
// operator-facing checks run the real memctl CLI as a subprocess.
[Collection("database")]
public sealed class AcceptanceTests : HttpSeamTestBase
{
    // Shared fixture language: the seeder writes these phrases and the
    // provenance and hallucination checks assert on them, so the seeded
    // scenario and the queries cannot drift apart silently.
    private const string SourceTraceText = "Bench measurement read 32768 hertz for the quartz resonator";
    private const string RevisionTraceText = "Recalibration measured the quartz resonator at 32767 hertz";
    private const string OriginalFactText = "The quartz resonator runs at 32768 hertz";
    private const string RevisedFactText = "After recalibration the quartz resonator runs at 32767 hertz";
    private const string UnconsumedFactText = "The helium backup oscillator drifts weekly";
    private const string ConsumedClaim = "quartz resonator recalibration";  // present in the consumed revised fact
    private const string UnconsumedClaim = "helium backup oscillator";      // present only in the approved-but-unconsumed fact
    private const string FabricatedClaim = "antigravity perpetual turbine"; // present nowhere

    // --- The four provenance questions (spec §10.1–.4) ---

    [Fact]
    public async Task WhyDidYouSayThat_ConsumedEventsResolveToSourceTraces()
    {
        var scenario = await SeedProvenanceScenarioAsync();

        // "What did the agent read in this session?" — every consumed memory,
        // already decorated with its source.
        var consumed = await RunMemCtlAsync("consumed", scenario.ConsumerSessionId);
        Assert.Contains(scenario.RevisedMemoryUuid.ToString(), consumed, StringComparison.Ordinal);
        Assert.Contains(scenario.SiblingMemoryUuid.ToString(), consumed, StringComparison.Ordinal);
        Assert.Contains(scenario.OriginalMemoryUuid.ToString(), consumed, StringComparison.Ordinal);
        Assert.Contains($"source=trace:{scenario.RevisionTraceUuid}", consumed, StringComparison.Ordinal);
        Assert.Contains($"source=trace:{scenario.SourceTraceUuid}", consumed, StringComparison.Ordinal);

        // "Why did you say that?" — the consumed memory resolves to its source
        // trace, across the whole supersession chain, with full trace content.
        var why = await RunMemCtlAsync("why", scenario.RevisedMemoryUuid.ToString());
        Assert.Contains(scenario.RevisedMemoryUuid.ToString(), why, StringComparison.Ordinal);
        Assert.Contains(scenario.RevisionTraceUuid.ToString(), why, StringComparison.Ordinal);
        Assert.Contains(RevisionTraceText, why, StringComparison.Ordinal);
        Assert.Contains(scenario.OriginalMemoryUuid.ToString(), why, StringComparison.Ordinal);
        Assert.Contains(scenario.SourceTraceUuid.ToString(), why, StringComparison.Ordinal);
        Assert.Contains(SourceTraceText, why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceChanged_EveryMemoryDerivedFromSourceIdIsListed()
    {
        var scenario = await SeedProvenanceScenarioAsync();

        // Spec §10.2: the nightly reconciliation worker is Phase 3, but the
        // query must work now — a plain lookup on the indexed source_id.
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        var derived = (await connection.QueryAsync<Guid>(
            "SELECT uuid FROM memories WHERE source_id = @SourceId",
            new { SourceId = scenario.SourceTraceUuid.ToString() })).ToList();

        Assert.Equal(2, derived.Count);
        Assert.Contains(scenario.OriginalMemoryUuid, derived);
        Assert.Contains(scenario.SiblingMemoryUuid, derived);
        Assert.DoesNotContain(scenario.RevisedMemoryUuid, derived);
    }

    [Fact]
    public async Task AdjudicateTwoFacts_ShowsProvenanceSideBySideWithDistinctApprovers()
    {
        var scenario = await SeedProvenanceScenarioAsync();

        // Side by side: capture timestamps, sources, versions, and the
        // supersession chain, all from the operator seam.
        var original = await RunMemCtlAsync("show", scenario.OriginalMemoryUuid.ToString());
        Assert.Contains("v1", original, StringComparison.Ordinal);
        Assert.Contains("shared/superseded", original, StringComparison.Ordinal);
        Assert.Contains($"source=trace:{scenario.SourceTraceUuid}", original, StringComparison.Ordinal);
        Assert.Contains($"superseded_by={scenario.RevisedMemoryUuid}", original, StringComparison.Ordinal);
        Assert.DoesNotContain("approved_at=<none>", original);
        Assert.Contains("approved_by=human:reviewer-alpha", original, StringComparison.Ordinal);
        Assert.Contains("agent=agent-a", original, StringComparison.Ordinal);

        var revised = await RunMemCtlAsync("show", scenario.RevisedMemoryUuid.ToString());
        Assert.Contains("v2", revised, StringComparison.Ordinal);
        Assert.Contains("shared/approved", revised, StringComparison.Ordinal);
        Assert.Contains($"source=trace:{scenario.RevisionTraceUuid}", revised, StringComparison.Ordinal);
        Assert.Contains($"supersedes={scenario.OriginalMemoryUuid}", revised, StringComparison.Ordinal);
        Assert.DoesNotContain("approved_at=<none>", revised);
        Assert.Contains("approved_by=human:reviewer-beta", revised, StringComparison.Ordinal);
        Assert.Contains("agent=agent-a", revised, StringComparison.Ordinal);

        // The capture timestamps parse and order: the original fact was
        // captured no later than its revision.
        var originalCreated = CreatedTimestamp(original);
        var revisedCreated = CreatedTimestamp(revised);
        Assert.True(originalCreated <= revisedCreated,
            $"original created={originalCreated:O} must not be after revised created={revisedCreated:O}");

        // Who approved each — from the review events — distinctly from who
        // proposed each: the review sessions carry reviewer identities, never
        // the proposing agent's.
        var originalReview = await RunMemCtlAsync("trace", $"review:{scenario.OriginalMemoryUuid}");
        Assert.Contains(" approval ", originalReview, StringComparison.Ordinal);
        Assert.Contains("agent=human:reviewer-alpha", originalReview, StringComparison.Ordinal);
        Assert.DoesNotContain("agent=agent-a", originalReview);

        var revisedReview = await RunMemCtlAsync("trace", $"review:{scenario.RevisedMemoryUuid}");
        Assert.Contains(" approval ", revisedReview, StringComparison.Ordinal);
        Assert.Contains("agent=human:reviewer-beta", revisedReview, StringComparison.Ordinal);
        Assert.DoesNotContain("agent=agent-a", revisedReview);
    }

    private static DateTimeOffset CreatedTimestamp(string show)
    {
        var match = Regex.Match(show, @"created=(\S+)");
        Assert.True(match.Success, $"memctl show must print created=<timestamp>; got: {show}");
        return DateTimeOffset.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task WasThisHallucinated_FtsOverConsumedSetAnswersClaims()
    {
        var scenario = await SeedProvenanceScenarioAsync();

        // Spec §10.4: given a session and a claim, FTS over the memories the
        // session actually consumed answers whether the claim was in memory.
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();

        Assert.True(await ClaimInConsumedSetAsync(connection, scenario.ConsumerSessionId, ConsumedClaim),
            "a claim present in a consumed memory must be found");
        Assert.False(await ClaimInConsumedSetAsync(connection, scenario.ConsumerSessionId, FabricatedClaim),
            "a fabricated claim must not be found");

        // The join is over the consumed set, not the whole store: this claim
        // exists — exactly as the approved-but-unconsumed memory the scenario
        // seeded — yet was never consumed in the session.
        var unconsumedMatches = (await connection.QueryAsync<Guid>(
            "SELECT uuid FROM memories WHERE search_tsv @@ websearch_to_tsquery('english', @Claim)",
            new { Claim = UnconsumedClaim })).ToList();
        Assert.Equal(scenario.UnconsumedMemoryUuid, Assert.Single(unconsumedMatches));
        Assert.False(await ClaimInConsumedSetAsync(connection, scenario.ConsumerSessionId, UnconsumedClaim),
            "a claim only present in unconsumed memory must not be attributed to the session");
    }

    private static async Task<bool> ClaimInConsumedSetAsync(NpgsqlConnection connection, string sessionId, string claim)
    {
        var matches = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM memories m
            WHERE m.search_tsv @@ websearch_to_tsquery('english', @Claim)
              AND m.uuid IN (
                  SELECT unnest(refs)
                  FROM traces
                  WHERE session_id = @SessionId AND event_type = 'memory_consumed')
            """,
            new { Claim = claim, SessionId = sessionId });
        return matches > 0;
    }

    // --- Provenance decoration polish (spec §7 rules, issue criterion 6) ---

    [Fact]
    public async Task RetrievedResultsCarrySourceVersionStatusDecorationAndLaneScores()
    {
        var scenario = await SeedProvenanceScenarioAsync();

        // Every search result carries source type, version, and status — plus
        // per-lane scores (spec: never discard lane scores).
        var results = scenario.SearchResults.EnumerateArray().ToList();
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            Assert.False(string.IsNullOrEmpty(result.GetProperty("sourceType").GetString()));
            Assert.False(string.IsNullOrEmpty(result.GetProperty("status").GetString()));
            Assert.True(result.GetProperty("version").GetInt32() >= 1);
            var laneScores = result.GetProperty("laneScores");
            foreach (var lane in new[] { "fts", "recency" })
            {
                Assert.True(laneScores.TryGetProperty(lane, out var score), $"result must carry the {lane} lane score");
                Assert.True(score.GetProperty("rank").GetInt32() >= 1);
            }

            Assert.True(result.GetProperty("fusedScore").GetDouble() > 0);
        }

        var revisedResult = Assert.Single(results, r => r.GetProperty("uuid").GetGuid() == scenario.RevisedMemoryUuid);
        Assert.Equal("trace", revisedResult.GetProperty("sourceType").GetString());
        Assert.Equal(scenario.RevisionTraceUuid.ToString(), revisedResult.GetProperty("sourceId").GetString());
        Assert.Equal(2, revisedResult.GetProperty("version").GetInt32());
        Assert.Equal("approved", revisedResult.GetProperty("status").GetString());

        // get_by_id responses carry the full decoration including the version
        // chain in both directions.
        Assert.Equal("trace", scenario.RevisedRecord.GetProperty("sourceType").GetString());
        Assert.Equal(scenario.RevisionTraceUuid.ToString(), scenario.RevisedRecord.GetProperty("sourceId").GetString());
        Assert.Equal(2, scenario.RevisedRecord.GetProperty("version").GetInt32());
        Assert.Equal("approved", scenario.RevisedRecord.GetProperty("status").GetString());
        Assert.Equal(scenario.OriginalMemoryUuid, scenario.RevisedRecord.GetProperty("supersedes").GetGuid());

        Assert.Equal("superseded", scenario.OriginalRecord.GetProperty("status").GetString());
        Assert.Equal(1, scenario.OriginalRecord.GetProperty("version").GetInt32());
        Assert.Equal(scenario.RevisedMemoryUuid, scenario.OriginalRecord.GetProperty("supersededBy").GetGuid());
    }

    // --- Mechanical checks (spec §10 "plus mechanical tests") ---

    // Sanctioned direct-DB test: the trigger and the grants ARE the spec'd
    // behavior. UPDATE/DELETE on traces must fail through the memsrv role's
    // grants (permission denied) AND through the trigger (which the table
    // owner hits even though grants do not bind it).
    [Fact]
    public async Task TraceMutationFailsThroughGrantsAndTrigger()
    {
        await using var client = await ConnectAsync(AgentAKey);
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = $"acceptance-appendonly-{Guid.NewGuid():N}",
            ["event_type"] = "note",
            ["content"] = new { text = "immutable once written" }
        });
        var traceUuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();

        await using (var runtime = new NpgsqlConnection(RuntimeConnection))
        {
            await runtime.OpenAsync();
            var update = await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.ExecuteAsync("UPDATE traces SET event_type = 'assistant_msg' WHERE trace_uuid = @Uuid", new { Uuid = traceUuid }));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);

            var delete = await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.ExecuteAsync("DELETE FROM traces WHERE trace_uuid = @Uuid", new { Uuid = traceUuid }));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
        }

        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        var triggerUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
            admin.ExecuteAsync("UPDATE traces SET event_type = 'assistant_msg' WHERE trace_uuid = @Uuid", new { Uuid = traceUuid }));
        Assert.Contains("traces are append-only", triggerUpdate.MessageText, StringComparison.OrdinalIgnoreCase);

        var triggerDelete = await Assert.ThrowsAsync<PostgresException>(() =>
            admin.ExecuteAsync("DELETE FROM traces WHERE trace_uuid = @Uuid", new { Uuid = traceUuid }));
        Assert.Contains("traces are append-only", triggerDelete.MessageText, StringComparison.OrdinalIgnoreCase);

        // No role receives DELETE on any table — retirement is a status change.
        var deleteGrants = await admin.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.role_table_grants WHERE grantee = 'memsrv' AND privilege_type = 'DELETE'");
        Assert.Equal(0, deleteGrants);
    }

    [Fact]
    public async Task SharedWritesAreBornProposedAndInvisibleUntilApproved()
    {
        var term = $"bornproposed{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        // The tool surface offers no way to request any other status: born
        // proposed is asserted on the response...
        var uuid = await ProposeAsync(client, "decision", $"Shared writes start proposed {term}", "human", null);

        // ...and the proposal stays invisible to retrieval until an operator
        // approves it.
        var before = await CallToolAsync(client, "search_memory", new Dictionary<string, object?> { ["query"] = term });
        Assert.DoesNotContain(before.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == uuid);

        await RunMemCtlAsync("approve", uuid.ToString(), "--by", "gatekeeper");

        var after = await CallToolAsync(client, "search_memory", new Dictionary<string, object?> { ["query"] = term });
        var result = Assert.Single(after.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == uuid);
        Assert.Equal("approved", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PrivateMemoriesAreInvisibleToOtherAgentCredentials()
    {
        var term = $"privnote{Guid.NewGuid():N}";
        await using var owner = await ConnectAsync(AgentAKey);
        var saved = await CallToolAsync(owner, "save_note", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "note",
            ["content"] = $"Private calibration note {term}"
        });
        var uuid = saved.GetProperty("data").GetProperty("uuid").GetGuid();

        var ownerSearch = await CallToolAsync(owner, "search_memory", new Dictionary<string, object?> { ["query"] = term });
        Assert.Contains(ownerSearch.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == uuid);

        // A different keyed agent in the same namespace sees nothing — neither
        // through search nor through a direct fetch of the uuid.
        await using var other = await ConnectAsync(ScopedKey);
        var otherSearch = await CallToolAsync(other, "search_memory", new Dictionary<string, object?> { ["query"] = term });
        Assert.DoesNotContain(otherSearch.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == uuid);

        var fetch = await other.CallToolAsync("get_by_id", new Dictionary<string, object?> { ["uuid"] = uuid });
        Assert.True(fetch.IsError == true, "another agent's private memory must not be fetchable by uuid");
    }

    [Fact]
    public async Task NamespaceIsolationHoldsAcrossAgentAllowlists()
    {
        var term = $"nsisolation{Guid.NewGuid():N}";
        await using var writer = await ConnectAsync(AgentAKey);
        var uuid = await ProposeAsync(writer, "fact", $"Homelab-only fact {term}", "human", null, @namespace: "homelab");
        await RunMemCtlAsync("approve", uuid.ToString(), "--by", "gatekeeper");

        // The allowlisted writer reads it back, proving the memory is live...
        var allowed = await CallToolAsync(writer, "search_memory", new Dictionary<string, object?>
        {
            ["query"] = term,
            ["namespaces"] = new[] { "homelab" }
        });
        Assert.Contains(allowed.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == uuid);

        // ...but agent-b is confined to memory-system: an explicit homelab
        // search is rejected, an unqualified search does not leak the memory,
        // and a direct fetch of the known uuid fails.
        await using var outsider = await ConnectAsync(ScopedKey);
        var foreignSearch = await outsider.CallToolAsync("search_memory", new Dictionary<string, object?>
        {
            ["query"] = term,
            ["namespaces"] = new[] { "homelab" }
        });
        Assert.True(foreignSearch.IsError == true, "searching a namespace outside the allowlist must be rejected");

        var defaultSearch = await CallToolAsync(outsider, "search_memory", new Dictionary<string, object?> { ["query"] = term });
        Assert.DoesNotContain(defaultSearch.GetProperty("data").EnumerateArray(), r => r.GetProperty("uuid").GetGuid() == uuid);

        var fetch = await outsider.CallToolAsync("get_by_id", new Dictionary<string, object?> { ["uuid"] = uuid });
        Assert.True(fetch.IsError == true, "fetching a memory outside the allowlist must be rejected");
    }

    [Fact]
    public async Task NeverStoreRejectsMemoryWritesAndRedactsTraceWrites()
    {
        // Synthetic secret matching config/never_store.yaml's aws-access-key-id
        // rule — never a real credential shape beyond the prefix.
        const string fakeSecret = "AKIAFAKEFAKEFAKEFAKE";
        await using var client = await ConnectAsync(AgentAKey);

        // Memory writes REJECT: both proposal and private note paths.
        var propose = await client.CallToolAsync("propose_memory", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "fact",
            ["content"] = $"Synthetic credential {fakeSecret} must not persist",
            ["source_type"] = "human"
        });
        Assert.True(propose.IsError == true, "propose_memory containing a never-store match must be rejected");

        var note = await client.CallToolAsync("save_note", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "note",
            ["content"] = $"Remember key {fakeSecret} for later"
        });
        Assert.True(note.IsError == true, "save_note containing a never-store match must be rejected");

        // Trace writes REDACT in place: the event is still recorded.
        await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = client.SessionId,
            ["event_type"] = "tool_result",
            ["content"] = new { tool = "shell", ok = true, summary = $"found {fakeSecret} in config" }
        });

        // The session replay shows the redacted trace event plus the redacted
        // notes recording the blocked writes — and the secret nowhere.
        var trace = await RunMemCtlAsync("trace", client.SessionId!);
        Assert.Contains(" tool_result ", trace, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:aws-access-key-id]", trace, StringComparison.Ordinal);
        Assert.Contains(" note ", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeSecret, trace, StringComparison.Ordinal);

        // Sanctioned DB-level absence check (docs/testing.md never-store gate):
        // the rejected writes must not have persisted the secret in any memory
        // row.
        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        var persisted = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM memories WHERE content LIKE @Pattern",
            new { Pattern = $"%{fakeSecret}%" });
        Assert.Equal(0, persisted);
    }

    // Sanctioned direct-DB test: content_hash validity is a database-level
    // invariant (v1.1: every memory row has a valid content_hash).
    [Fact]
    public async Task EveryMemoryRowCarriesAValidContentHash()
    {
        var term = $"hashcheck{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);
        await CallToolAsync(client, "save_note", new Dictionary<string, object?>
        {
            ["namespace"] = "memory-system",
            ["type"] = "note",
            ["content"] = $"Private hash fixture {term}"
        });
        await ProposeAsync(client, "fact", $"Shared hash fixture {term}", "human", null);

        await using var connection = new NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<(string Content, string ContentHash)>(
            "SELECT content, content_hash AS ContentHash FROM memories")).ToList();

        Assert.True(rows.Count >= 2);
        foreach (var row in rows)
        {
            Assert.Equal(64, row.ContentHash.Length);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Content))).ToLowerInvariant(),
                row.ContentHash);
        }
    }

    [Fact]
    public async Task ApprovalWithoutReviewerIdentityFails()
    {
        await using var client = await ConnectAsync(AgentAKey);
        var uuid = await ProposeAsync(client, "decision", "Anonymous approvals are forbidden", "human", null);

        var approve = await RunMemCtlForResultAsync(null, "approve", uuid.ToString());

        Assert.NotEqual(0, approve.ExitCode);
        Assert.Contains("--by is required", approve.Stderr, StringComparison.Ordinal);

        // The proposal is untouched.
        var show = await RunMemCtlAsync("show", uuid.ToString());
        Assert.Contains("shared/proposed", show, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovalTraceFollowsReviewSessionConvention()
    {
        var sourceSession = $"acceptance-review-src-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = sourceSession,
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = "evidence behind the proposal" }
        });
        var sourceTraceUuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();
        var uuid = await ProposeAsync(client, "decision", "Review events carry their own actor", "trace", sourceTraceUuid.ToString());

        await RunMemCtlAsync("approve", uuid.ToString(), "--by", "casey");

        // The review event lives in the synthetic review:<uuid> session, is
        // authored by the reviewer (never the proposing agent), references the
        // proposal and its source trace, and records amended: false.
        var review = await RunMemCtlAsync("trace", $"review:{uuid}");
        Assert.Contains(" approval ", review, StringComparison.Ordinal);
        Assert.Contains("agent=human:casey", review, StringComparison.Ordinal);
        Assert.DoesNotContain("agent=agent-a", review);
        Assert.Contains(uuid.ToString(), review, StringComparison.Ordinal);
        Assert.Contains(sourceTraceUuid.ToString(), review, StringComparison.Ordinal);
        Assert.Contains("\"reviewer\": \"human:casey\"", review, StringComparison.Ordinal);
        Assert.Contains("\"amended\": false", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditApprovalPreservesOriginalContentAndMarksAmended()
    {
        const string originalContent = "Original proposal wording to preserve";
        const string amendedContent = "Amended wording approved by the operator";
        await using var client = await ConnectAsync(AgentAKey);
        var uuid = await ProposeAsync(client, "decision", originalContent, "human", null);

        // A stub $EDITOR: verifies it received the original proposal text,
        // then replaces it with the amendment.
        var editor = Path.Combine(Path.GetTempPath(), $"memsrv-editor-{Guid.NewGuid():N}.sh");
        try
        {
            await File.WriteAllTextAsync(
                editor,
                $"#!/bin/sh\ngrep -q '{originalContent}' \"$1\" || exit 42\nprintf '{amendedContent}' > \"$1\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(editor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var approve = await RunMemCtlForResultAsync(
                new Dictionary<string, string> { ["EDITOR"] = editor },
                "approve", uuid.ToString(), "--by", "editor-reviewer", "--edit");
            Assert.True(approve.ExitCode == 0, $"approve --edit failed: stdout={approve.Stdout} stderr={approve.Stderr}");
            var approvedUuid = Guid.Parse(approve.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

            // The original content remains queryable on the superseded row...
            var original = await RunMemCtlAsync("show", uuid.ToString());
            Assert.Contains("shared/superseded", original, StringComparison.Ordinal);
            Assert.Contains(originalContent, original, StringComparison.Ordinal);
            Assert.Contains($"superseded_by={approvedUuid}", original, StringComparison.Ordinal);

            // ...the approved row is the amendment, one version ahead...
            var amended = await RunMemCtlAsync("show", approvedUuid.ToString());
            Assert.Contains("shared/approved", amended, StringComparison.Ordinal);
            Assert.Contains("v2", amended, StringComparison.Ordinal);
            Assert.Contains(amendedContent, amended, StringComparison.Ordinal);
            Assert.Contains($"supersedes={uuid}", amended, StringComparison.Ordinal);

            // ...and the approval trace event records amended: true.
            var review = await RunMemCtlAsync("trace", $"review:{uuid}");
            Assert.Contains("\"amended\": true", review, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(editor);
        }
    }

    // --- The seeded end-to-end scenario, built through the public surface ---

    private sealed record ProvenanceScenario(
        string ConsumerSessionId,
        Guid SourceTraceUuid,
        Guid RevisionTraceUuid,
        Guid OriginalMemoryUuid,
        Guid RevisedMemoryUuid,
        Guid SiblingMemoryUuid,
        Guid UnconsumedMemoryUuid,
        JsonElement SearchResults,
        JsonElement RevisedRecord,
        JsonElement OriginalRecord);

    // Agent-a logs source traces, proposes memories citing them, distinct human
    // reviewers approve via memctl (one approval supersedes the original fact),
    // and the agent consumes memories via search + get_by_id so memory_consumed
    // events exist under the transport session.
    private async Task<ProvenanceScenario> SeedProvenanceScenarioAsync()
    {
        var marker = $"resonator{Guid.NewGuid():N}";
        var sourceSession = $"acceptance-src-{marker}";
        await using var client = await ConnectAsync(AgentAKey);

        var sourceTrace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = sourceSession,
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = $"{SourceTraceText} {marker}" }
        });
        var sourceTraceUuid = sourceTrace.GetProperty("data").GetProperty("traceUuid").GetGuid();

        var originalUuid = await ProposeAsync(client, "fact",
            $"{OriginalFactText} ({marker})", "trace", sourceTraceUuid.ToString());
        await RunMemCtlAsync("approve", originalUuid.ToString(), "--by", "reviewer-alpha");

        var revisionTrace = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = sourceSession,
            ["event_type"] = "assistant_msg",
            ["content"] = new { text = $"{RevisionTraceText} {marker}" }
        });
        var revisionTraceUuid = revisionTrace.GetProperty("data").GetProperty("traceUuid").GetGuid();

        var revisedUuid = await ProposeAsync(client, "fact",
            $"{RevisedFactText} ({marker})",
            "trace", revisionTraceUuid.ToString(), originalUuid);
        await RunMemCtlAsync("approve", revisedUuid.ToString(), "--by", "reviewer-beta");

        var siblingUuid = await ProposeAsync(client, "note",
            $"Calibration bench setup notes for the quartz resonator ({marker})",
            "trace", sourceTraceUuid.ToString());
        await RunMemCtlAsync("approve", siblingUuid.ToString(), "--by", "reviewer-alpha");

        // Approved but never consumed: the negative case for "was this
        // hallucinated?".
        var unconsumedUuid = await ProposeAsync(client, "fact",
            $"{UnconsumedFactText} ({marker})", "human", null);
        await RunMemCtlAsync("approve", unconsumedUuid.ToString(), "--by", "reviewer-alpha");

        // Consumption: search, then read full records — the server logs
        // memory_consumed events under the transport session.
        var search = await CallToolAsync(client, "search_memory", new Dictionary<string, object?>
        {
            ["query"] = marker,
            ["limit"] = 10
        });
        var revisedRecord = await CallToolAsync(client, "get_by_id", new Dictionary<string, object?> { ["uuid"] = revisedUuid });
        await CallToolAsync(client, "get_by_id", new Dictionary<string, object?> { ["uuid"] = siblingUuid });
        var originalRecord = await CallToolAsync(client, "get_by_id", new Dictionary<string, object?> { ["uuid"] = originalUuid });

        var consumerSessionId = client.SessionId!;
        Assert.False(string.IsNullOrEmpty(consumerSessionId));

        return new ProvenanceScenario(
            consumerSessionId,
            sourceTraceUuid,
            revisionTraceUuid,
            originalUuid,
            revisedUuid,
            siblingUuid,
            unconsumedUuid,
            search.GetProperty("data").Clone(),
            revisedRecord.GetProperty("data").Clone(),
            originalRecord.GetProperty("data").Clone());
    }

    private static async Task<Guid> ProposeAsync(
        ModelContextProtocol.Client.McpClient client,
        string type,
        string content,
        string sourceType,
        string? sourceId,
        Guid? supersedes = null,
        string @namespace = "memory-system")
    {
        var arguments = new Dictionary<string, object?>
        {
            ["namespace"] = @namespace,
            ["type"] = type,
            ["content"] = content,
            ["source_type"] = sourceType,
            ["source_id"] = sourceId
        };
        if (supersedes.HasValue)
        {
            arguments["supersedes"] = supersedes.Value;
        }

        var proposed = await CallToolAsync(client, "propose_memory", arguments);
        Assert.Equal("proposed", proposed.GetProperty("data").GetProperty("status").GetString());
        return proposed.GetProperty("data").GetProperty("uuid").GetGuid();
    }
}
