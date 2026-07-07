# CLAUDE.md — Memory Server (Phase 1)

Self-hosted memory/control substrate: one .NET server exposing a small MCP tool
surface over ONE PostgreSQL database. Append-only traces, provenance-carrying
memories, proposal→approval flow, two-step hybrid retrieval.

## Document hierarchy (who wins)
1. `docs/memory-server-phase1-spec.md` — binding on build details, schema, tool contracts
2. `docs/agent-memory-handoff-v4.md` — intent/architecture where the spec is silent
3. This file + `docs/` conventions

## Scope discipline
The spec's Do Not Build list is BINDING (full list: `docs/design-rules.md` §1).
Headline: no embeddings, no graphs, no LLM workers, no tiering mechanics, no UI,
no second datastore, no dispatcher, no harness extensions.
Mid-session ideas not in the spec: one line in `docs/decisions.md`
(post-server: `save_note`, namespace `memory-system`) — then move on.
Closed decisions are closed; new evidence goes to the human, not into code.

## Hard rules (catastrophic if missed)
- **Never log to stdout.** In stdio transport stdout belongs to JSON-RPC;
  one stray `Console.WriteLine` breaks the client. Serilog → stderr or file.
- **MCP SDK `ModelContextProtocol` 1.4.0 (pinned).** Your training data likely
  predates the stable API. Before writing ANY MCP hosting/tool code, read the
  vendored samples in `reference/csharp-sdk/`. Trust samples over memory.
  Streamable HTTP for remote (Session 2); never legacy SSE.
- **The server is the only door.** No consumer ever sees a connection string.
- **Traces are append-only** — enforced by grants AND trigger; no code path
  updates or deletes a trace. No DELETE granted anywhere; retirement = status flip.
- **`agent_id` comes from the credential, never from tool arguments.**
- **Shared memories are born `proposed`; approval is `memctl`-only** —
  never add an agent-facing approve tool.
- Tests exercise the public surface (MCP tools + `memctl`) only —
  exceptions and details in `docs/testing.md`.
- Tests never change during refactor. Commit per green cycle.

## Stack (decided — do not re-litigate; rationale in docs/design-rules.md §3)
.NET 10 · Npgsql + Dapper, hand-written SQL (NO EF Core) · DbUp plain-SQL
migrations · xUnit against `memory_test` · YamlDotNet for `config/never_store.yaml`.

## Commands
- `make db-up` — Postgres **18** dev container via compose (creates
  `memory_dev`/`memory_test` and the `memsrv` login role, mirroring prod)
- `make test` / `make test-one T=<filter>` — suite / single test (`memory_test`)
- `make test-db-reset` — recreate `memory_test` + migrations
- `make migrate-dev` — one-shot containerized `memctl migrate` → `memory_dev`
- `make accept` — Session 1 DoD end-to-end path (NOT IMPLEMENTED — Session 2)
- Interactive dev runs against `memory_dev`, never `memory_test`, never prod.
- Deployment contract for homelab: `docs/deployment-contract.md`.

## Read before specific tasks (progressive disclosure)
- Starting any slice → `docs/workflow.md` (checklist + human gates)
- Writing/changing tests → `docs/testing.md`
- Schema, invariants, retrieval, layout, DoD → `docs/design-rules.md`
- Any MCP hosting/tool code → `reference/csharp-sdk/` samples
- Domain vocabulary → `CONTEXT.md`

## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues for `faviann/overmind`; external PRs are not a triage surface. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the default triage label vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, and `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout: root `CONTEXT.md` plus root `docs/adr/` when they exist. See `docs/agents/domain.md`.
