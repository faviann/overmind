from cortex_memory import service


def test_memory_proposal_is_application_level_input():
    proposal = service.MemoryProposal(
        namespace="repo/memorySubsystem",
        memory_type="decision",
        content="Use service functions as the CLI/MCP boundary.",
    )

    assert proposal.namespace == "repo/memorySubsystem"
    assert proposal.memory_type == "decision"
    assert proposal.source_text is None
