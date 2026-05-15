from __future__ import annotations

import argparse
import json
import sys
from datetime import date, datetime
from decimal import Decimal
from typing import Any

from cortex_memory import db, service


def add_output_argument(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--json", action="store_true", help="Emit JSON output.")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="memory")
    subparsers = parser.add_subparsers(dest="command", required=True)

    init = subparsers.add_parser("init", help="Apply local database migrations.")
    add_output_argument(init)

    trace = subparsers.add_parser("trace", help="Append and inspect trace events.")
    trace_subparsers = trace.add_subparsers(dest="trace_command", required=True)

    trace_append = trace_subparsers.add_parser("append", help="Append a trace event.")
    trace_append.add_argument("--session", required=True, dest="session_id")
    trace_append.add_argument("--project", default=db.default_namespace(), dest="project_id")
    trace_append.add_argument("--agent", required=True, dest="agent_id")
    trace_append.add_argument("--type", required=True, choices=sorted(service.TRACE_EVENT_TYPES), dest="event_type")
    trace_append.add_argument("--content", required=True)
    trace_append.add_argument("--metadata-json", default="{}", help="JSON object with event metadata.")
    trace_append.add_argument("--timestamp", help="ISO-8601 event timestamp. Defaults to database time.")
    add_output_argument(trace_append)

    trace_list = trace_subparsers.add_parser("list", help="List trace events for a session.")
    trace_list.add_argument("--session", required=True, dest="session_id")
    trace_list.add_argument("--project", dest="project_id")
    add_output_argument(trace_list)

    propose = subparsers.add_parser("propose", help="Create a pending memory proposal.")
    propose.add_argument("--namespace", default=db.default_namespace())
    propose.add_argument("--type", required=True, dest="memory_type")
    propose.add_argument("--content", required=True)
    propose.add_argument("--source-text")
    propose.add_argument("--rationale")
    propose.add_argument("--from-event", dest="source_event_id")
    add_output_argument(propose)

    proposals = subparsers.add_parser("proposals", help="Review memory proposals.")
    proposal_subparsers = proposals.add_subparsers(dest="proposal_command", required=True)

    proposals_list = proposal_subparsers.add_parser("list", help="List proposals.")
    proposals_list.add_argument("--namespace", default=db.default_namespace())
    proposals_list.add_argument("--status", default="pending", choices=["pending", "approved", "rejected", "all"])
    add_output_argument(proposals_list)

    proposals_show = proposal_subparsers.add_parser("show", help="Show a proposal with provenance.")
    proposals_show.add_argument("proposal_id")
    add_output_argument(proposals_show)

    proposals_approve = proposal_subparsers.add_parser("approve", help="Approve a proposal.")
    proposals_approve.add_argument("proposal_id")
    add_output_argument(proposals_approve)

    proposals_reject = proposal_subparsers.add_parser("reject", help="Reject a proposal.")
    proposals_reject.add_argument("proposal_id")
    add_output_argument(proposals_reject)

    search = subparsers.add_parser("search", help="Search approved knowledge.")
    search.add_argument("--namespace", default=db.default_namespace())
    search.add_argument("--query", required=True)
    search.add_argument("--type", dest="memory_type")
    search.add_argument("--limit", type=int, default=20)
    add_output_argument(search)

    dev = subparsers.add_parser("dev", help="Local development utilities.")
    dev_subparsers = dev.add_subparsers(dest="dev_command", required=True)
    dev_reset = dev_subparsers.add_parser("reset", help="Drop and recreate the local dev schema.")
    dev_reset.add_argument("--yes", action="store_true", help="Confirm destructive reset.")
    add_output_argument(dev_reset)

    return parser


def json_default(value: Any) -> str:
    if isinstance(value, datetime | date):
        return value.isoformat()
    if isinstance(value, Decimal):
        return str(value)
    return str(value)


def emit(payload: Any, as_json: bool, fields: list[str] | None = None, empty_text: str | None = None) -> None:
    if as_json:
        print(json.dumps(payload, default=json_default, indent=2))
        return

    if isinstance(payload, list):
        if not payload:
            print(empty_text or "no results")
            return
        if fields is None:
            raise ValueError("fields are required for text output")
        print(db.format_rows(payload, fields))
        return

    if isinstance(payload, dict):
        if fields is None:
            raise ValueError("fields are required for text output")
        print(db.format_rows([payload], fields))
        return

    print(payload)


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        if args.command == "init":
            payload = service.initialize_database()
            if args.json:
                emit(payload, True)
            elif payload["applied"]:
                print("applied migrations:")
                for version in payload["applied"]:
                    print(f"- {version}")
            else:
                print("database already initialized")
            return 0

        if args.command == "trace":
            if args.trace_command == "append":
                row = service.append_trace_event(
                    service.TraceEvent(
                        session_id=args.session_id,
                        project_id=args.project_id,
                        agent_id=args.agent_id,
                        event_type=args.event_type,
                        content=args.content,
                        metadata=parse_json_object(args.metadata_json, "metadata-json"),
                        timestamp=args.timestamp,
                    )
                )
                emit(row, args.json, ["id", "session_id", "project_id", "agent_id", "event_type", "content"])
                return 0
            if args.trace_command == "list":
                rows = service.list_trace_events(args.session_id, args.project_id)
                emit(
                    rows,
                    args.json,
                    ["id", "session_id", "project_id", "agent_id", "event_type", "content"],
                    "no trace events",
                )
                return 0

        if args.command == "propose":
            row = service.propose_memory(
                service.MemoryProposal(
                    namespace=args.namespace,
                    memory_type=args.memory_type,
                    content=args.content,
                    source_text=args.source_text,
                    rationale=args.rationale,
                    source_event_id=args.source_event_id,
                )
            )
            emit(row, args.json, ["id", "memory_type", "status", "content"])
            return 0

        if args.command == "proposals":
            if args.proposal_command == "list":
                rows = service.list_proposals(args.namespace, args.status)
                emit(rows, args.json, ["id", "namespace", "memory_type", "status", "content"], "no proposals")
                return 0
            if args.proposal_command == "show":
                payload = service.get_proposal_inspection(args.proposal_id)
                if args.json:
                    emit(payload, True)
                else:
                    print(format_proposal_inspection(payload))
                return 0
            if args.proposal_command == "approve":
                row = service.approve_proposal(args.proposal_id)
                emit(row, args.json, ["id", "proposal_id", "memory_type", "content"])
                return 0
            if args.proposal_command == "reject":
                row = service.reject_proposal(args.proposal_id)
                emit(row, args.json, ["id", "status"])
                return 0

        if args.command == "search":
            rows = service.search_knowledge(args.namespace, args.query, args.memory_type, args.limit)
            emit(rows, args.json, ["id", "namespace", "memory_type", "content"], "no knowledge")
            return 0

        if args.command == "dev":
            if args.dev_command == "reset":
                if not args.yes:
                    raise ValueError("dev reset requires --yes")
                payload = service.reset_development_database()
                if args.json:
                    emit(payload, True)
                else:
                    print("development database reset")
                    if payload["applied"]:
                        print("applied migrations:")
                        for version in payload["applied"]:
                            print(f"- {version}")
                return 0
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    parser.error("unhandled command")
    return 2


def parse_json_object(value: str, name: str) -> dict:
    parsed = json.loads(value)
    if not isinstance(parsed, dict):
        raise ValueError(f"{name} must be a JSON object")
    return parsed


def format_proposal_inspection(payload: dict) -> str:
    proposal = payload["proposal"]
    lines = [
        "proposal:",
        f"  id={proposal['id']}",
        f"  namespace={proposal['namespace']}",
        f"  memory_type={proposal['memory_type']}",
        f"  status={proposal['status']}",
        f"  provenance_status={payload['provenance_status']}",
        f"  source_event_id={proposal['source_event_id'] or 'missing'}",
        f"  content={proposal['content']}",
    ]
    if proposal.get("rationale"):
        lines.append(f"  rationale={proposal['rationale']}")
    if proposal.get("source_text"):
        lines.append(f"  source_text={proposal['source_text']}")

    source_event = payload["source_event"]
    lines.append("source_event:")
    if source_event is None:
        lines.append("  missing")
        return "\n".join(lines)

    lines.extend(
        [
            f"  id={source_event['id']}",
            f"  event_type={source_event['event_type']}",
            f"  timestamp={json_default(source_event['timestamp'])}",
            f"  session_id={source_event['session_id']}",
            f"  project_id={source_event['project_id']}",
            f"  agent_id={source_event['agent_id']}",
            f"  content={source_event['content']}",
            f"  metadata={json.dumps(source_event['metadata'], default=json_default, sort_keys=True)}",
        ]
    )
    return "\n".join(lines)
