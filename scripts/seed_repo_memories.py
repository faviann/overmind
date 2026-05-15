from __future__ import annotations

from cortex_memory import service


NAMESPACE = "repo/memorySubsystem"

SEED_MEMORIES = [
    (
        "decision",
        "V0a is intentionally CLI-to-Postgres only; REST, MCP, and full harness runtime are deferred until the trace/provenance seam is proven.",
    ),
    (
        "decision",
        "V0a began with manual proposals, but the next substrate slice is event-backed proposals with source-event provenance.",
    ),
    (
        "convention",
        "The repo namespace for the memory subsystem is repo/memorySubsystem.",
    ),
]


def approved_content_exists(content: str) -> bool:
    return service.approved_content_exists(NAMESPACE, content)


def main() -> int:
    service.initialize_database()
    created = 0
    skipped = 0

    for memory_type, content in SEED_MEMORIES:
        if approved_content_exists(content):
            print(f"skip existing: {content}")
            skipped += 1
            continue

        proposal = service.propose_memory(
            service.MemoryProposal(
                namespace=NAMESPACE,
                memory_type=memory_type,
                content=content,
                source_text="scripts/seed_repo_memories.py",
                rationale="Seed V0a repo-local memory after the first vertical loop.",
            )
        )
        knowledge = service.approve_proposal(proposal["id"])
        print(f"created {knowledge['id']}: {content}")
        created += 1

    print(f"seed complete: created={created} skipped={skipped}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
