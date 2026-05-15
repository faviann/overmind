from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from cortex_memory import db


ProposalStatus = Literal["pending", "approved", "rejected", "all"]
TRACE_EVENT_TYPES = db.TRACE_EVENT_TYPES


@dataclass(frozen=True)
class MemoryProposal:
    namespace: str
    memory_type: str
    content: str
    source_text: str | None = None
    rationale: str | None = None
    source_event_id: str | None = None


@dataclass(frozen=True)
class TraceEvent:
    session_id: str
    project_id: str
    agent_id: str
    event_type: str
    content: str
    metadata: dict | None = None
    timestamp: str | None = None


def initialize_database() -> dict:
    return {"applied": db.apply_migrations()}


def reset_development_database() -> dict:
    db.reset_dev_database()
    return {"reset": True, "applied": db.apply_migrations()}


def propose_memory(proposal: MemoryProposal) -> dict:
    return db.create_proposal(
        db.ProposalInput(
            namespace=proposal.namespace,
            memory_type=proposal.memory_type,
            content=proposal.content,
            source_text=proposal.source_text,
            rationale=proposal.rationale,
            source_event_id=proposal.source_event_id,
        )
    )


def append_trace_event(event: TraceEvent) -> dict:
    return db.create_trace_event(
        db.TraceEventInput(
            session_id=event.session_id,
            project_id=event.project_id,
            agent_id=event.agent_id,
            event_type=event.event_type,
            content=event.content,
            metadata=event.metadata,
            timestamp=event.timestamp,
        )
    )


def list_trace_events(session_id: str, project_id: str | None = None) -> list[dict]:
    return db.list_trace_events(session_id, project_id)


def list_proposals(namespace: str, status: ProposalStatus = "pending") -> list[dict]:
    return db.list_proposals(namespace, status)


def get_proposal_inspection(proposal_id: str) -> dict:
    return db.get_proposal_inspection(proposal_id)


def approve_proposal(proposal_id: str) -> dict:
    return db.approve_proposal(proposal_id)


def reject_proposal(proposal_id: str) -> dict:
    return db.reject_proposal(proposal_id)


def search_knowledge(
    namespace: str,
    query: str,
    memory_type: str | None = None,
    limit: int = 20,
) -> list[dict]:
    return db.search_knowledge(namespace, query, memory_type, limit)


def approved_content_exists(namespace: str, content: str) -> bool:
    results = search_knowledge(namespace, content[:40])
    return any(row["content"] == content for row in results)
