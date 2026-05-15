from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from cortex_memory import db


ProposalStatus = Literal["pending", "approved", "rejected", "all"]


@dataclass(frozen=True)
class MemoryProposal:
    namespace: str
    memory_type: str
    content: str
    source_text: str | None = None
    rationale: str | None = None


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
        )
    )


def list_proposals(namespace: str, status: ProposalStatus = "pending") -> list[dict]:
    return db.list_proposals(namespace, status)


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
