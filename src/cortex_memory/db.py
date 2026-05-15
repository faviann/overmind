from __future__ import annotations

import os
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Literal

import psycopg
from psycopg.rows import dict_row


DEFAULT_DATABASE_URL = "postgresql://cortex_memory:cortex_memory@localhost:55432/cortex_memory"
DEFAULT_NAMESPACE = "repo/memorySubsystem"


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
class ProposalInput:
    namespace: str
    memory_type: str
    content: str
    source_text: str | None = None
    rationale: str | None = None


def create_proposal(proposal: ProposalInput) -> dict:
    validate_required_text("namespace", proposal.namespace)
    validate_required_text("type", proposal.memory_type)
    validate_required_text("content", proposal.content)

    proposal_id = new_id()
    with connect() as conn:
        with conn.cursor() as cur:
            namespace_id = ensure_namespace(cur, proposal.namespace)
            cur.execute(
                """
                INSERT INTO memory_proposals (
                    id, namespace_id, memory_type, content, source_text, rationale
                )
                VALUES (%s, %s, %s, %s, %s, %s)
                RETURNING id, memory_type, content, status, created_at
                """,
                (
                    proposal_id,
                    namespace_id,
                    proposal.memory_type,
                    proposal.content,
                    proposal.source_text,
                    proposal.rationale,
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
                SELECT p.id, n.name AS namespace, p.memory_type, p.content, p.status, p.created_at
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
                SELECT id, namespace_id, memory_type, content, source_text, status
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
                    id, namespace_id, proposal_id, memory_type, content, source_text
                )
                VALUES (%s, %s, %s, %s, %s, %s)
                RETURNING id, proposal_id, memory_type, content, approved_at
                """,
                (
                    knowledge_id,
                    proposal["namespace_id"],
                    proposal["id"],
                    proposal["memory_type"],
                    proposal["content"],
                    proposal["source_text"],
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
                SELECT k.id, n.name AS namespace, k.memory_type, k.title, k.content, k.approved_at
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
