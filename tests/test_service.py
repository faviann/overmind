from cortex_memory import service


def test_memory_proposal_is_application_level_input():
    proposal = service.MemoryProposal(
        namespace="repo/memorySubsystem",
        memory_type="decision",
        content="Use service functions as the CLI/MCP boundary.",
        source_event_id="event-1",
    )

    assert proposal.namespace == "repo/memorySubsystem"
    assert proposal.memory_type == "decision"
    assert proposal.source_text is None
    assert proposal.source_event_id == "event-1"


def test_trace_event_is_application_level_input():
    event = service.TraceEvent(
        session_id="session-1",
        project_id="repo/memorySubsystem",
        agent_id="codex",
        event_type="tool_call",
        content="Ran pytest.",
        metadata={"command": "pytest"},
    )

    assert event.session_id == "session-1"
    assert event.project_id == "repo/memorySubsystem"
    assert event.agent_id == "codex"
    assert event.metadata == {"command": "pytest"}
