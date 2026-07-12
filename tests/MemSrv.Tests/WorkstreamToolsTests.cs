namespace MemSrv.Tests;

// Slice 4: workstream and handoff tools (issue #8), asserted at the HTTP seam
// as keyed agents. Trace-event assertions go through the memctl operator seam
// (trace <session_id>), never raw SQL. Harness in HttpSeamTestBase.
[Collection("database")]
public sealed class WorkstreamToolsTests : HttpSeamTestBase
{
    [Fact]
    public async Task CheckoutByUnknownTitleCreatesAndChecksOutInOneCall()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        var result = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });

        AssertNext(result);
        var data = result.GetProperty("data");
        Assert.True(data.GetProperty("created").GetBoolean(), "checkout by unknown title must report it created the stream");
        var workstream = data.GetProperty("workstream");
        Assert.Equal(title, workstream.GetProperty("title").GetString());
        Assert.Equal("checked_out", workstream.GetProperty("status").GetString());
        Assert.Equal("agent-a", workstream.GetProperty("ownerAgent").GetString());
        Assert.Equal(client.SessionId, workstream.GetProperty("sessionId").GetString());
        Assert.Equal("memory-system", workstream.GetProperty("namespace").GetString());
    }

    [Fact]
    public async Task CheckoutOfCheckedOutWorkstreamFailsNamingOwnerAgentAndSession()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        await using var ownerClient = await ConnectAsync(AgentAKey);
        var checkout = await CallToolAsync(ownerClient, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();
        var ownerSession = ownerClient.SessionId!;
        Assert.False(string.IsNullOrEmpty(ownerSession));

        // A second keyed agent tries to take it — by uuid and by title. Both must
        // fail with an error naming the owning agent and its session; no force-steal.
        await using var rival = await ConnectAsync(ScopedKey);
        foreach (var arguments in new[]
        {
            new Dictionary<string, object?> { ["uuid"] = uuid },
            new Dictionary<string, object?> { ["title"] = title }
        })
        {
            var result = await rival.CallToolAsync("checkout_workstream", arguments);
            Assert.True(result.IsError == true, "checkout of a checked-out workstream must fail");
            var message = string.Join("\n", result.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(block => block.Text));
            Assert.Contains("agent-a", message);
            Assert.Contains(ownerSession, message);
        }
    }

    [Fact]
    public async Task ListWorkstreamsShowsStatusAndOwnerAndHonorsStatusFilter()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);
        var checkout = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();

        var list = await CallToolAsync(client, "list_workstreams", new Dictionary<string, object?>
        {
            ["status"] = "checked_out"
        });
        AssertNext(list);
        var entry = list.GetProperty("data").EnumerateArray()
            .Single(row => row.GetProperty("uuid").GetGuid() == uuid);
        Assert.Equal("checked_out", entry.GetProperty("status").GetString());
        Assert.Equal("agent-a", entry.GetProperty("ownerAgent").GetString());
        Assert.Equal(title, entry.GetProperty("title").GetString());

        // The filter excludes it under any other status.
        var openOnly = await CallToolAsync(client, "list_workstreams", new Dictionary<string, object?>
        {
            ["status"] = "open"
        });
        Assert.DoesNotContain(openOnly.GetProperty("data").EnumerateArray(),
            row => row.GetProperty("uuid").GetGuid() == uuid);
    }

    [Fact]
    public async Task ListWorkstreamsDefaultsToInflightStreamsAndRejectsTerminalStatusFilters()
    {
        await using var client = await ConnectAsync(AgentAKey);
        var terminalUuids = new List<Guid>();
        foreach (var status in new[] { "done", "abandoned" })
        {
            var checkout = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
            {
                ["title"] = $"ws-{status}-{Guid.NewGuid():N}"
            });
            var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();
            terminalUuids.Add(uuid);
            await CallToolAsync(client, "checkin_workstream", new Dictionary<string, object?>
            {
                ["uuid"] = uuid,
                ["status"] = status,
                ["notes"] = $"terminal {status}"
            });
        }

        var listed = await CallToolAsync(client, "list_workstreams", new Dictionary<string, object?>());
        Assert.DoesNotContain(listed.GetProperty("data").EnumerateArray(),
            row => terminalUuids.Contains(row.GetProperty("uuid").GetGuid()));

        foreach (var status in new[] { "done", "abandoned" })
        {
            var result = await client.CallToolAsync("list_workstreams", new Dictionary<string, object?>
            {
                ["status"] = status
            });
            Assert.True(result.IsError == true, $"terminal status filter '{status}' must be rejected");
        }
    }

    [Fact]
    public async Task CheckinByNonOwnerFails()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        await using var owner = await ConnectAsync(AgentAKey);
        var checkout = await CallToolAsync(owner, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();

        await using var rival = await ConnectAsync(ScopedKey);
        var result = await rival.CallToolAsync("checkin_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid,
            ["status"] = "done",
            ["notes"] = "not mine to finish"
        });

        Assert.True(result.IsError == true, "checkin by a non-owner must fail");
        var message = string.Join("\n", result.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Select(block => block.Text));
        Assert.Contains("agent-a", message);

        // The stream is untouched: still checked out to its owner.
        var list = await CallToolAsync(owner, "list_workstreams", new Dictionary<string, object?>());
        var entry = list.GetProperty("data").EnumerateArray()
            .Single(row => row.GetProperty("uuid").GetGuid() == uuid);
        Assert.Equal("checked_out", entry.GetProperty("status").GetString());
        Assert.Equal("agent-a", entry.GetProperty("ownerAgent").GetString());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("done")]
    [InlineData("abandoned")]
    public async Task OwnerCheckinLandsEachStatus(string status)
    {
        var title = $"ws-{Guid.NewGuid():N}";
        var notes = $"state at stop {Guid.NewGuid():N}";
        Guid uuid;
        await using (var owner = await ConnectAsync(AgentAKey))
        {
            var checkout = await CallToolAsync(owner, "checkout_workstream", new Dictionary<string, object?>
            {
                ["title"] = title
            });
            uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();
        }

        // Owner means the same agent identity, not the same transport session: a
        // restarted session of the owning agent must be able to check in.
        await using var restarted = await ConnectAsync(AgentAKey);
        var checkin = await CallToolAsync(restarted, "checkin_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid,
            ["status"] = status,
            ["notes"] = notes
        });
        AssertNext(checkin);
        Assert.Equal(status, checkin.GetProperty("data").GetProperty("status").GetString());

        if (status == "open")
        {
            var list = await CallToolAsync(restarted, "list_workstreams", new Dictionary<string, object?>
            {
                ["status"] = status
            });
            var entry = list.GetProperty("data").EnumerateArray()
                .Single(row => row.GetProperty("uuid").GetGuid() == uuid);
            Assert.Equal(notes, entry.GetProperty("notes").GetString());
            // An open checkin is a handoff: the stream is released for the next
            // agent (null owner serializes as an absent property).
            Assert.False(
                entry.TryGetProperty("ownerAgent", out var ownerProperty)
                    && ownerProperty.ValueKind != System.Text.Json.JsonValueKind.Null,
                "an open checkin must release the owner");
        }
        else
        {
            var checkout = await restarted.CallToolAsync("checkout_workstream", new Dictionary<string, object?>
            {
                ["uuid"] = uuid
            });
            Assert.True(checkout.IsError == true, $"a {status} workstream must reject checkout");
            var message = string.Join("\n", checkout.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(block => block.Text));
            Assert.Contains($"terminal status '{status}'", message);
        }
    }

    [Fact]
    public async Task OpenCheckinNotesSurfaceAsHandoffSummaryOnNextListAndCheckout()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        var handoffNotes = $"handoff summary {Guid.NewGuid():N}";
        await using var first = await ConnectAsync(AgentAKey);
        var checkout = await CallToolAsync(first, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();
        await CallToolAsync(first, "checkin_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid,
            ["status"] = "open",
            ["notes"] = handoffNotes
        });

        // The next agent sees the summary on list...
        await using var next = await ConnectAsync(ScopedKey);
        var list = await CallToolAsync(next, "list_workstreams", new Dictionary<string, object?>
        {
            ["status"] = "open"
        });
        var entry = list.GetProperty("data").EnumerateArray()
            .Single(row => row.GetProperty("uuid").GetGuid() == uuid);
        Assert.Equal(handoffNotes, entry.GetProperty("notes").GetString());

        // ...and on checkout by the same title: it claims the handed-off stream
        // (created=false) and carries the summary to start from.
        var reclaimed = await CallToolAsync(next, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        Assert.False(reclaimed.GetProperty("data").GetProperty("created").GetBoolean());
        var workstream = reclaimed.GetProperty("data").GetProperty("workstream");
        Assert.Equal(uuid, workstream.GetProperty("uuid").GetGuid());
        Assert.Equal(handoffNotes, workstream.GetProperty("notes").GetString());
        Assert.Equal("agent-b", workstream.GetProperty("ownerAgent").GetString());
    }

    [Fact]
    public async Task CreateHandoffProducesOpenWorkstreamCarryingSummaryAndRefs()
    {
        var summary = $"pick up from here {Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        // A real trace uuid as the reference: the full record stays retrievable
        // by reference, never inlined.
        var logged = await CallToolAsync(client, "log_trace", new Dictionary<string, object?>
        {
            ["session_id"] = $"handoff-src-{Guid.NewGuid():N}",
            ["event_type"] = "note",
            ["content"] = new { text = "work happened here" }
        });
        var refUuid = logged.GetProperty("data").GetProperty("traceUuid").GetGuid();

        var handoff = await CallToolAsync(client, "create_handoff", new Dictionary<string, object?>
        {
            ["summary"] = summary,
            ["refs"] = new[] { refUuid }
        });
        AssertNext(handoff);
        var data = handoff.GetProperty("data");
        Assert.Equal("open", data.GetProperty("status").GetString());
        Assert.Equal(summary, data.GetProperty("notes").GetString());
        Assert.Contains(data.GetProperty("refs").EnumerateArray(), r => r.GetGuid() == refUuid);
        var uuid = data.GetProperty("uuid").GetGuid();

        // The receiving agent finds it as an open workstream with the summary.
        await using var receiver = await ConnectAsync(ScopedKey);
        var list = await CallToolAsync(receiver, "list_workstreams", new Dictionary<string, object?>
        {
            ["status"] = "open"
        });
        var entry = list.GetProperty("data").EnumerateArray()
            .Single(row => row.GetProperty("uuid").GetGuid() == uuid);
        Assert.Equal(summary, entry.GetProperty("notes").GetString());
        Assert.Contains(entry.GetProperty("refs").EnumerateArray(), r => r.GetGuid() == refUuid);
    }

    [Fact]
    public async Task WorkstreamToolsLogTaxonomyTraceEventsAndAllCarryNextHints()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);

        var checkout = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        AssertNext(checkout);
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();

        var checkin = await CallToolAsync(client, "checkin_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid,
            ["status"] = "done",
            ["notes"] = "finished"
        });
        AssertNext(checkin);

        var handoff = await CallToolAsync(client, "create_handoff", new Dictionary<string, object?>
        {
            ["summary"] = $"handoff {Guid.NewGuid():N}",
            ["refs"] = new[] { uuid }
        });
        AssertNext(handoff);

        var list = await CallToolAsync(client, "list_workstreams", new Dictionary<string, object?>
        {
            ["namespace"] = "homelab",
            ["status"] = "open"
        });
        AssertNext(list);

        // All server-side workstream events share the transport session; the
        // operator trace seam replays them. checkout records whether it created
        // the stream, checkin records the target status.
        var trace = await RunMemCtlAsync("trace", client.SessionId!);
        Assert.Contains("workstream_checkout", trace);
        Assert.Contains("\"created\": true", trace);
        Assert.Contains("workstream_checkin", trace);
        Assert.Contains("\"status\": \"done\"", trace);
        Assert.Contains("handoff", trace);

        // Listing is a read audit event, normalized as {tool, params}, in the
        // resolved namespace and with identity/session derived from transport.
        Assert.Contains(" tool_call ", trace);
        Assert.Contains("\"tool\": \"list_workstreams\"", trace);
        Assert.Contains("\"namespace\": \"homelab\"", trace);
        Assert.Contains("\"status\": \"open\"", trace);
        var traceLines = trace.Split('\n');
        var listContentIndex = Array.FindIndex(traceLines, line => line.Contains("\"tool\": \"list_workstreams\""));
        Assert.True(listContentIndex > 0, "list_workstreams content must follow its trace header");
        Assert.Contains("agent=agent-a", traceLines[listContentIndex - 1]);
        Assert.Contains("ns=homelab", traceLines[listContentIndex - 1]);
        var eventTypes = new[] { "workstream_checkout", "workstream_checkin", "handoff", "tool_call" };
        var eventLines = trace.Split('\n')
            .Count(line => eventTypes.Any(eventType => line.Contains($" {eventType} ")));
        Assert.Equal(4, eventLines);
    }

    [Fact]
    public async Task SeededFakeSecretInNotesAndSummaryIsRedactedInPlaceAndEventsStillRecorded()
    {
        // Synthetic secret matching config/never_store.yaml's aws-access-key-id
        // rule — never a real credential shape beyond the prefix.
        const string fakeSecret = "AKIAFAKEFAKEFAKEFAKE";
        var marker = $"redact-{Guid.NewGuid():N}";
        var title = $"ws-{marker}";
        await using var client = await ConnectAsync(AgentAKey);

        var checkout = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();

        // Workstream notes and handoff summaries follow the TRACE rule:
        // redact-in-place, the operation still succeeds.
        var checkin = await CallToolAsync(client, "checkin_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid,
            ["status"] = "open",
            ["notes"] = $"paused {marker}: found key {fakeSecret} in config"
        });
        var notes = checkin.GetProperty("data").GetProperty("notes").GetString()!;
        Assert.DoesNotContain(fakeSecret, notes);
        Assert.Contains("[REDACTED:aws-access-key-id]", notes);
        Assert.Contains($"paused {marker}", notes);

        var handoff = await CallToolAsync(client, "create_handoff", new Dictionary<string, object?>
        {
            ["summary"] = $"handoff {marker}: rotate {fakeSecret} first",
            ["refs"] = new[] { uuid }
        });
        var summary = handoff.GetProperty("data").GetProperty("notes").GetString()!;
        Assert.DoesNotContain(fakeSecret, summary);
        Assert.Contains("[REDACTED:aws-access-key-id]", summary);

        // Row content seen by any later agent is the redacted text.
        var list = await CallToolAsync(client, "list_workstreams", new Dictionary<string, object?>
        {
            ["status"] = "open"
        });
        foreach (var entry in list.GetProperty("data").EnumerateArray()
            .Where(row => (row.GetProperty("notes").GetString() ?? "").Contains(marker)))
        {
            Assert.DoesNotContain(fakeSecret, entry.GetProperty("notes").GetString());
        }

        // The trace seam: events were still recorded, and the secret is absent
        // from the whole session replay.
        var trace = await RunMemCtlAsync("trace", client.SessionId!);
        Assert.Contains("workstream_checkin", trace);
        Assert.Contains("handoff", trace);
        Assert.DoesNotContain(fakeSecret, trace);
    }

    [Fact]
    public async Task MemctlReleaseFlipsStaleCheckoutBackToOpen()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        var notes = $"stale state {Guid.NewGuid():N}";
        Guid uuid;

        // A session checks out work and dies without checking in — the stale
        // checkout that has no auto-expiry and no force-steal.
        await using (var stale = await ConnectAsync(AgentAKey))
        {
            var checkout = await CallToolAsync(stale, "checkout_workstream", new Dictionary<string, object?>
            {
                ["title"] = title
            });
            uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();
        }

        // The operator escape hatch: memctl release flips it back to open.
        var released = await RunMemCtlAsync("release", uuid.ToString());
        Assert.Contains(uuid.ToString(), released);

        // Another agent can now check it out; ownership moved cleanly.
        await using var next = await ConnectAsync(ScopedKey);
        var reclaimed = await CallToolAsync(next, "checkout_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid
        });
        var workstream = reclaimed.GetProperty("data").GetProperty("workstream");
        Assert.Equal("checked_out", workstream.GetProperty("status").GetString());
        Assert.Equal("agent-b", workstream.GetProperty("ownerAgent").GetString());
    }

    [Fact]
    public async Task TerminalWorkstreamRejectsCheckoutByUuidButTitleCreatesAFreshStream()
    {
        var title = $"ws-{Guid.NewGuid():N}";
        await using var client = await ConnectAsync(AgentAKey);
        var checkout = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        var uuid = checkout.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid();
        await CallToolAsync(client, "checkin_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid,
            ["status"] = "done",
            ["notes"] = "finished"
        });

        // By uuid, a terminal stream is an explicit error naming its status.
        var byUuid = await client.CallToolAsync("checkout_workstream", new Dictionary<string, object?>
        {
            ["uuid"] = uuid
        });
        Assert.True(byUuid.IsError == true, "checkout by uuid of a terminal workstream must fail");
        var message = string.Join("\n", byUuid.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Select(block => block.Text));
        Assert.Contains("done", message);

        // By title, terminal rows do not count (titles are not unique): the same
        // title starts a fresh workstream.
        var byTitle = await CallToolAsync(client, "checkout_workstream", new Dictionary<string, object?>
        {
            ["title"] = title
        });
        Assert.True(byTitle.GetProperty("data").GetProperty("created").GetBoolean());
        Assert.NotEqual(uuid, byTitle.GetProperty("data").GetProperty("workstream").GetProperty("uuid").GetGuid());
    }
}
