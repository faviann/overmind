# AGENTS.md — Memory Server

Self-hosted memory/control substrate: one .NET server exposing a small MCP tool
surface over one PostgreSQL database. Traces are append-only, memories carry
provenance, shared memories follow proposal→approval, and retrieval is two-step.

## Authority

1. `docs/memory-server-phase1-spec.md` — binding build details, schema, and tool contracts
2. `docs/agent-memory-handoff-v4.md` — intent and architecture where the spec is silent
3. This file and `docs/` conventions

## Always-on invariants

- **Never log to stdout.** In stdio transport, stdout belongs to JSON-RPC; send
  application logs to stderr or a file.
- **The server is the only database door.** No consumer ever sees a connection
  string.
- **Traces are append-only.** Enforce this with grants and a trigger; no code
  path updates or deletes a trace, no role receives DELETE, and retirement is a
  status change.
- **Derive `agent_id` from the credential, never from tool arguments.**
- **Shared memories require operator approval.** They are born `proposed`, and
  approval is `memctl`-only; never add an agent-facing approval tool.
- **Do not broaden scope beyond the binding spec.**

## Read before changing

- MCP hosting or tools → run `make sdk-reference`, then inspect the relevant
  centrally pinned, version-matched upstream documentation or sample in
  `reference/csharp-sdk/`. Trust that evidence over memory. Remote transport
  uses Streamable HTTP, never legacy SSE.
- Tests or refactors → `docs/testing.md`
- Schema, retrieval, dependencies, or scope → `docs/design-rules.md`
- Deployment or deployment configuration → `docs/deployment-contract.md`
- Domain terminology or domain documentation → `CONTEXT.md` and
  `docs/agents/domain.md`
- Issue tracking or labels → `docs/agents/issue-tracker.md` and
  `docs/agents/triage-labels.md`

## Common commands

- `make db-up` — start the development database services
- `make test` / `make test-one T=<filter>` — run the suite / one filtered test
- `make test-db-reset` — recreate the test database and apply migrations
- `make migrate-dev` — migrate the development database
- Interactive development uses `memory_dev`, never `memory_test` or production.
