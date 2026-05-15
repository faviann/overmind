# Cortex Memory

Local-first memory substrate for agent work: proposal capture, human approval, durable knowledge, and retrieval.

This repository is currently focused on the memory layer only. Broader Overmind orchestration plans are kept as future context in [docs/overmind-future-plan.md](docs/overmind-future-plan.md), but they are not the short-term implementation target.

## Current Target

V0a proves the smallest useful memory loop:

```text
manual proposal -> approve/reject -> approved knowledge -> plain text retrieval
```

V0a is intentionally direct CLI-to-Postgres. No REST API, MCP server, async worker, embeddings, graph memory, dashboard, or production deployment yet.

## Local Development

Start Postgres:

```bash
docker compose -f docker-compose.yml up -d postgres
```

Apply migrations:

```bash
UV_CACHE_DIR=/tmp/uv-cache uv run memory init
```

Create and review a memory:

```bash
UV_CACHE_DIR=/tmp/uv-cache uv run memory propose --namespace repo/memorySubsystem --type decision --content "V0a uses plain text search before embeddings."
UV_CACHE_DIR=/tmp/uv-cache uv run memory proposals list --namespace repo/memorySubsystem
UV_CACHE_DIR=/tmp/uv-cache uv run memory proposals approve <proposal-id>
UV_CACHE_DIR=/tmp/uv-cache uv run memory search --namespace repo/memorySubsystem --query "embeddings"
```

Default database URL:

```text
postgresql://cortex_memory:cortex_memory@localhost:55432/cortex_memory
```

Override with `MEMORY_DATABASE_URL`. The default namespace is `repo/memorySubsystem`; override with `MEMORY_DEFAULT_NAMESPACE`.

## Tests

Fast tests:

```bash
UV_CACHE_DIR=/tmp/uv-cache uv run pytest
```

Integration loop against local Postgres:

```bash
docker compose -f docker-compose.yml up -d postgres
MEMORY_REQUIRE_DB=1 UV_CACHE_DIR=/tmp/uv-cache uv run pytest -m integration tests/integration
```

Smoke wrapper:

```bash
UV_CACHE_DIR=/tmp/uv-cache uv run python scripts/smoke_test.py
```

Tests use unique namespaces so they do not depend on accumulated local database state. A persistent local Postgres container is acceptable for speed. If state looks suspicious, reset the local development schema explicitly:

```bash
UV_CACHE_DIR=/tmp/uv-cache uv run memory dev reset --yes
```

Seed the first repo-local approved memories:

```bash
UV_CACHE_DIR=/tmp/uv-cache uv run python scripts/seed_repo_memories.py
```

## Sandbox Notes

Sandboxed agents may need local network escalation for commands that connect to the Compose-published Postgres port on `localhost:55432`.

Use `UV_CACHE_DIR=/tmp/uv-cache` in sandboxed sessions because the default uv cache under `$HOME` may be read-only.

## Next Slice

The likely V0b candidate is MCP tools around the same approved-memory operations: `propose_memory`, `list_proposals`, `approve_proposal`, `reject_proposal`, and `search_knowledge`.
