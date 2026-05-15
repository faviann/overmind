# Agent Operating Instructions

**Project**: Cortex Memory — local-first memory substrate for agent work
**Current focus**: V0a memory loop in README.md
**Direction**: PROJECT_DIRECTION.md
**Detailed doctrine**: docs/architecture/memory-ledger-principles.md
**Future orchestration context**: docs/overmind-future-plan.md

## Stack

| Component | Role | Deployed on |
|-----------|------|-------------|
| CLI | Local V0a memory workflow | local dev |
| Postgres | Memory proposals and approved knowledge | local dev |
| n8n (future) | Integration, ingestion, event handling | `workstation` LXC |
| Hermes/MCP (future) | Agent access to approved memory tools | `workstation` LXC |

No local model is required for V0a.

## Companion Repos

- `faviann/overmind` — this repo. Memory substrate and future architecture notes
- `faviann/overmind-tasks` — validated task list as GitHub Issues. `faviann/cortex-tasks` is deprecated and should not be used.

## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues for `faviann/overmind-tasks`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the default mattpocock/skills triage label vocabulary. See `docs/agents/triage-labels.md`.

### Domain docs

This repo uses a single-context domain-doc layout. See `docs/agents/domain.md`.

## Key Constraints

- V0a is CLI-to-Postgres only.
- Preserve the distinction between raw trace, memory proposals, approved knowledge, and derived indexes.
- Treat trace plus approved memory ledger as the source of truth.
- Read `docs/architecture/memory-ledger-principles.md` before changing schema, provenance, retrieval, projections, or integrations.
- Do not add REST, MCP, embeddings, graph memory, dashboards, or production deployment until V0a is stable.
- Never expose gateway ports publicly without authentication.
- Do not build OB1 integration until it is explicitly scoped.

## Local Development Notes

- Use `UV_CACHE_DIR=/tmp/uv-cache` when running `uv` in sandboxed agent sessions. The default uv cache under `$HOME` may be read-only.
- Local Postgres smoke tests connect to the Compose-published port on `localhost:55432`; sandboxed agents may need local network escalation for those commands.
- Prefer `docker compose -f docker-compose.yml up -d postgres` for the V0a development database, and stop it after verification unless continued use is needed.
- A persistent local Postgres volume is acceptable for speed, but tests must not depend on accumulated state. Use unique namespaces in tests and `memory dev reset --yes` when a clean schema is needed.
- When clarifying architecture decisions, prefer concrete lifecycle examples and explicitly contrast the target slice with nearby non-goals. This helped clarify that V0 approval means source-event-backed review, not full replay.

## Versioning

Current target: **V0a — local vertical memory loop**. See README.md and PROJECT_DIRECTION.md. Do not implement future Overmind orchestration features in this slice.
