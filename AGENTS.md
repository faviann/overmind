# Agent Operating Instructions

**Project**: Cortex Memory — local-first memory substrate for agent work
**Current focus**: V0a memory loop in README.md
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

- `faviann/cortex` — this repo. Memory substrate and future architecture notes
- `faviann/cortex-tasks` — validated task list as GitHub Issues. Labels: `source:discord`, `source:email`, `importance:low/medium/high`, `status:parked`

## Key Constraints

- V0a is CLI-to-Postgres only.
- Do not add REST, MCP, embeddings, graph memory, dashboards, or production deployment until V0a is stable.
- Never expose gateway ports publicly without authentication.
- Do not build OB1 integration until it is explicitly scoped.

## Local Development Notes

- Use `UV_CACHE_DIR=/tmp/uv-cache` when running `uv` in sandboxed agent sessions. The default uv cache under `$HOME` may be read-only.
- Local Postgres smoke tests connect to the Compose-published port on `localhost:55432`; sandboxed agents may need local network escalation for those commands.
- Prefer `docker compose -f docker-compose.yml up -d postgres` for the V0a development database, and stop it after verification unless continued use is needed.
- A persistent local Postgres volume is acceptable for speed, but tests must not depend on accumulated state. Use unique namespaces in tests and `memory dev reset --yes` when a clean schema is needed.

## Versioning

Current target: **V0a — local vertical memory loop**. See README.md and NEXT_STEPS.md. Do not implement future Overmind orchestration features in this slice.
