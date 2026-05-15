from __future__ import annotations

import os
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Literal

import psycopg
from psycopg.rows import dict_row
from psycopg.types.json import Json


DEFAULT_DATABASE_URL = "postgresql://cortex_memory:cortex_memory@localhost:55432/cortex_memory"
DEFAULT_NAMESPACE = "repo/memorySubsystem"
TRACE_EVENT_TYPES = {
    "user_message",
    "agent_message",
    "tool_call",
    "tool_result",
    "command_run",
    "file_observed",
    "file_modified",
    "decision",
    "error",
    "memory_proposal_created",
    "memory_approved",
    "memory_rejected",
    "memory_superseded",
}


def database_url() -> str:
    return os.environ.get("MEMORY_DATABASE_URL", DEFAULT_DATABASE_URL)


def default_namespace() -> str:
    return os.environ.get("MEMORY_DEFAULT_NAMESPACE", DEFAULT_NAMESPACE)


def new_id() -> str:
    return str(uuid.uuid4())


def connect() -> psycopg.Connection:
    return psycopg.connect(database_url(), row_factory=dict_row)


def migration_files() -> list[Path]:
    root = Path(__file__).resolve().parents[2]
    return sorted((root / "migrations").glob("*.sql"))


def apply_migrations() -> list[str]:
    applied: list[str] = []
    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version TEXT PRIMARY KEY,
                    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
                )
                """
            )
            for migration in migration_files():
                version = migration.name
                cur.execute(
                    "SELECT 1 FROM schema_migrations WHERE version = %s",
                    (version,),
                )
                if cur.fetchone():
                    continue
                cur.execute(migration.read_text())
                cur.execute(
                    "INSERT INTO schema_migrations (version) VALUES (%s)",
                    (version,),
                )
                applied.append(version)
        conn.commit()
    return applied


def reset_dev_database() -> None:
    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute("DROP TABLE IF EXISTS knowledge_entries")
            cur.execute("DROP TABLE IF EXISTS memory_proposals")
            cur.execute("DROP TABLE IF EXISTS trace_events")
            cur.execute("DROP TABLE IF EXISTS namespaces")
            cur.execute("DROP TABLE IF EXISTS schema_migrations")
        conn.commit()


def ensure_namespace(cur: psycopg.Cursor, name: str) -> str:
    cur.execute("SELECT id FROM namespaces WHERE name = %s", (name,))
    row = cur.fetchone()
    if row:
        return row["id"]

    namespace_id = new_id()
    cur.execute(
        "INSERT INTO namespaces (id, name) VALUES (%s, %s)",
        (namespace_id, name),
    )
    return namespace_id


@dataclass(frozen=True)
class TraceEventInput:
    session_id: str
    project_id: str
    agent_id: str
    event_type: str
    content: str
    metadata: dict | None = None
    timestamp: str | None = None


@dataclass(frozen=True)
class ProposalInput:
    namespace: str
    memory_type: str
    content: str
    source_text: str | None = None
    rationale: str | None = None
    source_event_id: str | None = None


def create_trace_event(event: TraceEventInput) -> dict:
    validate_required_text("session", event.session_id)
    validate_required_text("project", event.project_id)
    validate_required_text("agent", event.agent_id)
    validate_event_type(event.event_type)
    validate_required_text("content", event.content)
    if event.metadata is not None and not isinstance(event.metadata, dict):
        raise ValueError("metadata must be a JSON object")

    event_id = new_id()
    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO trace_events (
                    id, session_id, project_id, agent_id, event_type, occurred_at, content, metadata
                )
                VALUES (%s, %s, %s, %s, %s, COALESCE(%s::timestamptz, now()), %s, %s)
                RETURNING
                    id,
                    session_id,
                    project_id,
                    agent_id,
                    event_type,
                    occurred_at AS timestamp,
                    content,
                    metadata
                """,
                (
                    event_id,
                    event.session_id,
                    event.project_id,
                    event.agent_id,
                    event.event_type,
                    event.timestamp,
                    event.content,
                    Json(event.metadata or {}),
                ),
            )
            row = cur.fetchone()
        conn.commit()
    return row


def list_trace_events(session_id: str, project_id: str | None = None) -> list[dict]:
    validate_required_text("session", session_id)
    filters = ["session_id = %s"]
    params: list[object] = [session_id]
    if project_id is not None:
        validate_required_text("project", project_id)
        filters.append("project_id = %s")
        params.append(project_id)

    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT
                    id,
                    session_id,
                    project_id,
                    agent_id,
                    event_type,
                    occurred_at AS timestamp,
                    content,
                    metadata
                FROM trace_events
                WHERE {' AND '.join(filters)}
                ORDER BY occurred_at ASC, created_at ASC
                """,
                params,
            )
            return list(cur.fetchall())


def create_proposal(proposal: ProposalInput) -> dict:
    validate_required_text("namespace", proposal.namespace)
    validate_required_text("type", proposal.memory_type)
    validate_required_text("content", proposal.content)

    proposal_id = new_id()
    with connect() as conn:
        with conn.cursor() as cur:
            namespace_id = ensure_namespace(cur, proposal.namespace)
            if proposal.source_event_id is not None:
                source_event = ensure_trace_event_exists(cur, proposal.source_event_id)
                if source_event["project_id"] != proposal.namespace:
                    raise ValueError(
                        "source event project does not match proposal namespace: "
                        f"{source_event['project_id']} != {proposal.namespace}"
                    )
            cur.execute(
                """
                INSERT INTO memory_proposals (
                    id, namespace_id, memory_type, content, source_text, rationale, source_event_id
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s)
                RETURNING id, memory_type, content, status, source_event_id, created_at
                """,
                (
                    proposal_id,
                    namespace_id,
                    proposal.memory_type,
                    proposal.content,
                    proposal.source_text,
                    proposal.rationale,
                    proposal.source_event_id,
                ),
            )
            row = cur.fetchone()
        conn.commit()
    return row


def list_proposals(namespace: str, status: Literal["pending", "approved", "rejected", "all"] = "pending") -> list[dict]:
    validate_required_text("namespace", namespace)
    filters = ["p.namespace_id = %s"]
    params: list[object] = []

    with connect() as conn:
        with conn.cursor() as cur:
            namespace_id = ensure_namespace(cur, namespace)
            params.append(namespace_id)
            if status != "all":
                filters.append("p.status = %s")
                params.append(status)
            cur.execute(
                f"""
                SELECT p.id, n.name AS namespace, p.memory_type, p.content, p.status, p.source_event_id, p.created_at
                FROM memory_proposals p
                JOIN namespaces n ON n.id = p.namespace_id
                WHERE {' AND '.join(filters)}
                ORDER BY p.created_at ASC
                """,
                params,
            )
            return list(cur.fetchall())


def approve_proposal(proposal_id: str) -> dict:
    knowledge_id = new_id()
    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id, namespace_id, memory_type, content, source_text, source_event_id, status
                FROM memory_proposals
                WHERE id = %s
                FOR UPDATE
                """,
                (proposal_id,),
            )
            proposal = cur.fetchone()
            if proposal is None:
                raise ValueError(f"proposal not found: {proposal_id}")
            if proposal["status"] != "pending":
                raise ValueError(
                    f"proposal {proposal_id} is {proposal['status']}, not pending"
                )

            cur.execute(
                """
                UPDATE memory_proposals
                SET status = 'approved', reviewed_at = now(), updated_at = now()
                WHERE id = %s
                """,
                (proposal_id,),
            )
            cur.execute(
                """
                INSERT INTO knowledge_entries (
                    id, namespace_id, proposal_id, memory_type, content, source_text, source_event_id
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s)
                RETURNING id, proposal_id, source_event_id, memory_type, content, approved_at
                """,
                (
                    knowledge_id,
                    proposal["namespace_id"],
                    proposal["id"],
                    proposal["memory_type"],
                    proposal["content"],
                    proposal["source_text"],
                    proposal["source_event_id"],
                ),
            )
            row = cur.fetchone()
        conn.commit()
    return row


def reject_proposal(proposal_id: str) -> dict:
    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE memory_proposals
                SET status = 'rejected', reviewed_at = now(), updated_at = now()
                WHERE id = %s AND status = 'pending'
                RETURNING id, status, reviewed_at
                """,
                (proposal_id,),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"pending proposal not found: {proposal_id}")
        conn.commit()
    return row


def search_knowledge(
    namespace: str,
    query: str,
    memory_type: str | None = None,
    limit: int = 20,
) -> list[dict]:
    validate_required_text("namespace", namespace)
    validate_required_text("query", query)
    if limit < 1:
        raise ValueError("limit must be greater than 0")
    if memory_type is not None:
        validate_required_text("type", memory_type)

    filters = ["n.name = %s", "(k.content ILIKE %s OR coalesce(k.title, '') ILIKE %s)"]
    params: list[object] = [namespace, f"%{query}%", f"%{query}%"]
    if memory_type:
        filters.append("k.memory_type = %s")
        params.append(memory_type)
    params.append(limit)

    with connect() as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT
                    k.id,
                    n.name AS namespace,
                    k.proposal_id,
                    k.source_event_id,
                    k.memory_type,
                    k.title,
                    k.content,
                    k.approved_at
                FROM knowledge_entries k
                JOIN namespaces n ON n.id = k.namespace_id
                WHERE {' AND '.join(filters)}
                ORDER BY k.approved_at DESC
                LIMIT %s
                """,
                params,
            )
            return list(cur.fetchall())


def format_rows(rows: Iterable[dict], fields: list[str]) -> str:
    lines: list[str] = []
    for row in rows:
        lines.append(" | ".join(f"{field}={row[field]}" for field in fields))
    return "\n".join(lines)


def validate_required_text(name: str, value: str) -> None:
    if not value or not value.strip():
        raise ValueError(f"{name} cannot be empty")


def validate_event_type(event_type: str) -> None:
    validate_required_text("type", event_type)
    if event_type not in TRACE_EVENT_TYPES:
        allowed = ", ".join(sorted(TRACE_EVENT_TYPES))
        raise ValueError(f"unknown trace event type {event_type!r}; expected one of: {allowed}")


def ensure_trace_event_exists(cur: psycopg.Cursor, event_id: str) -> dict:
    validate_required_text("source event", event_id)
    cur.execute("SELECT id, project_id FROM trace_events WHERE id = %s", (event_id,))
    row = cur.fetchone()
    if row is None:
        raise ValueError(f"source event not found: {event_id}")
    return row
