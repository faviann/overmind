from cortex_memory.cli import build_parser


def test_search_defaults_to_repo_namespace():
    parser = build_parser()

    args = parser.parse_args(["search", "--query", "postgres"])

    assert args.namespace == "repo/memorySubsystem"
    assert args.query == "postgres"


def test_trace_append_shape():
    parser = build_parser()

    args = parser.parse_args(
        [
            "trace",
            "append",
            "--session",
            "session-1",
            "--agent",
            "codex",
            "--type",
            "tool_call",
            "--content",
            "Ran pytest.",
            "--metadata-json",
            '{"command":"pytest"}',
            "--json",
        ]
    )

    assert args.command == "trace"
    assert args.trace_command == "append"
    assert args.session_id == "session-1"
    assert args.project_id == "repo/memorySubsystem"
    assert args.agent_id == "codex"
    assert args.event_type == "tool_call"
    assert args.json is True


def test_propose_supports_source_event_link():
    parser = build_parser()

    args = parser.parse_args(
        [
            "propose",
            "--type",
            "decision",
            "--content",
            "Use trace-backed proposals.",
            "--from-event",
            "event-1",
        ]
    )

    assert args.source_event_id == "event-1"


def test_proposals_approve_shape():
    parser = build_parser()

    args = parser.parse_args(["proposals", "approve", "proposal-1"])

    assert args.command == "proposals"
    assert args.proposal_command == "approve"
    assert args.proposal_id == "proposal-1"


def test_proposals_list_supports_all_status_and_json():
    parser = build_parser()

    args = parser.parse_args(["proposals", "list", "--status", "all", "--json"])

    assert args.status == "all"
    assert args.json is True


def test_dev_reset_requires_explicit_subcommand():
    parser = build_parser()

    args = parser.parse_args(["dev", "reset", "--yes", "--json"])

    assert args.command == "dev"
    assert args.dev_command == "reset"
    assert args.yes is True
