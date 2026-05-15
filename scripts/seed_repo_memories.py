from __future__ import annotations

from cortex_memory import db


NAMESPACE = "repo/memorySubsystem"

SEED_MEMORIES = [
    (
        "decision",
        "V0a is intentionally CLI-to-Postgres only; REST and MCP are deferred until the vertical loop is proven.",
    ),
    (
        "decision",
        "V0a stores manual proposals first and only creates durable knowledge after explicit approval.",
    ),
    (
        "convention",
        "The repo namespace for the memory subsystem is repo/memorySubsystem.",
    ),
]


def approved_content_exists(content: str) -> bool:
    results = db.search_knowledge(NAMESPACE, content[:40])
    return any(row["content"] == content for row in results)


def main() -> int:
    db.apply_migrations()
    created = 0
    skipped = 0

    for memory_type, content in SEED_MEMORIES:
        if approved_content_exists(content):
            print(f"skip existing: {content}")
            skipped += 1
            continue

        proposal = db.create_proposal(
            db.ProposalInput(
                namespace=NAMESPACE,
                memory_type=memory_type,
                content=content,
                source_text="scripts/seed_repo_memories.py",
                rationale="Seed V0a repo-local memory after the first vertical loop.",
            )
        )
        knowledge = db.approve_proposal(proposal["id"])
        print(f"created {knowledge['id']}: {content}")
        created += 1

    print(f"seed complete: created={created} skipped={skipped}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
