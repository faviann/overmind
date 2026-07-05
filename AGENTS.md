# AGENTS.md — Memory Server (Phase 1)
 
Self-hosted memory/control substrate for agents. A single .NET server process exposing
a small MCP tool surface (stdio now, HTTP later) over one PostgreSQL database.
It owns an append-only trace store, a provenance-carrying memory store, a
proposal→approval flow, and two-step hybrid retrieval.
 
## Document hierarchy (who wins)
 
1. `docs/memory-server-phase1-spec.md` — **binding** on build details, schema, tool contracts, and the Do Not Build list.
2. `docs/agent-memory-handoff-v2.md` — governs intent and architecture where the spec is silent.
3. This file — governs day-to-day coding conventions in this repo.
If the spec and this file conflict, the spec wins. If an idea appears mid-session that
is not in the spec: `save_note` it into namespace `memory-system` as type `task`
(or append to `decisions.md` if the server isn't running yet) and **move on**. Do not
expand scope inside a session.
 
## Do Not Build (binding — from the Phase 1 spec)
 
- ❌ Embeddings, pgvector, or any embedding model integration
- ❌ Graph storage or graph lanes
- ❌ Any LLM-calling worker (no extraction, consolidation, reconciliation) — the `jobs` table stays **empty**
- ❌ Tiering mechanics (promotion/demotion/TTL) — the `tier` column exists, nothing moves rows
- ❌ Web UI or dashboard — `memctl` only
- ❌ Additional datastores (ClickHouse, Timescale, Redis, vector DBs, queues) — one Postgres database, one server process
- ❌ Auth beyond static bearer keys mapped to agent_ids
- ❌ Dispatcher/orchestrator logic
- ❌ Harness extensions (compaction hooks) — `trace_snapshots` + `log_trace(compaction_boundary)` are the landing pads only
## Non-negotiable invariants
 
- **The server is the only door.** No consumer ever gets a connection string. One app
  role (`memsrv`) holds DB credentials; agents get bearer keys / stdio identities.
- **Traces are append-only.** Enforced at the grant level AND by trigger. No code path
  may UPDATE or DELETE a trace. No DELETE is granted on any table; retirement is a status flip.
- **`agent_id` is derived from the credential, never from tool arguments.**
- **Shared memories are born `proposed`.** There is no agent-facing approve tool —
  approval is `memctl` only. Private notes (`visibility='private'`) are direct-write,
  auto-approved, owner-scoped, server-enforced.
- **Never-store gate runs in code on every write path** (traces included), driven by
  `config/never_store.yaml`. Reject, name the rule, log a redacted `note` trace event.
- **Causal provenance is captured server-side**: `get_by_id` logs `memory_consumed`,
  `search_memory` logs the query, writes log `memory_proposed`/`memory_written`.
  Never rely on agent cooperation for the audit trail.
- **If a signal is computed at write time, persist it.** Never compute a lane score or
  confidence and discard it before the row is written.
- **Per-lane scores stay on every search result.** One fused score for ordering
  (RRF, k=60), one score per lane for debugging. Never return only the fused score.
- **Two-step retrieval always**: `search_memory` returns uuid + preview + provenance
  decoration + lane scores; full content only via `get_by_id`.
- **Every tool response ends with a `next` field** — a one-line hint for the most
  likely follow-up call.
## Stack (decided — do not re-litigate)
 
- **.NET 10** (LTS floor: SDK assemblies target netstandard2.0; project uses net10.0)
- **MCP:** `ModelContextProtocol` **1.4.0** (pinned in `Directory.Packages.props`);
  add `ModelContextProtocol.AspNetCore` only in Session 2 (HTTP transport).
  The SDK went stable v1.0 in March 2026. **Your training data likely predates the
  stable API.** Before writing MCP hosting/tool code, read the vendored samples in
  `reference/csharp-sdk/` (git-ignored clone of `modelcontextprotocol/csharp-sdk`).
  Trust the samples over memory. Streamable HTTP is the remote transport; do not use
  legacy SSE.
- **Data access:** `Npgsql` + `Dapper` with hand-written SQL. **No EF Core** — the
  schema uses generated tsvector columns, triggers, and grant-level governance that
  EF migrations handle badly. SQL is the product; keep it visible.
- **Migrations:** numbered plain `.sql` files in `migrations/`, applied by **DbUp**
  under the admin/migration role (separate from `memsrv`). Grants and triggers live
  in the migration files, not in C#.
- **Config:** `appsettings.json` + environment variables; `config/never_store.yaml`
  parsed with YamlDotNet.
- **Tests:** xUnit integration suite against the `memory_test` database.
  `dotnet test` is the feedback loop — run it after every meaningful change.
- **Logging:** Serilog (or `ILogger`) → **stderr or file only. Never stdout.**
  In stdio transport, stdout belongs to the JSON-RPC protocol; a single stray
  `Console.WriteLine` breaks the client with cryptic parse errors.
## Solution layout
 
```
memsrv.sln
  src/MemSrv.Core/        # domain, SQL, tool implementations, never-store gate, RRF
  src/MemSrv.Server/      # MCP host entry point (stdio; HTTP added Session 2)
  src/MemCtl/             # operator CLI (approve/reject/pending/show/trace/why/consumed)
  tests/MemSrv.Tests/     # acceptance tests + mechanical tests, against memory_test
  migrations/             # 0001_init.sql, ... (DDL from the spec, verbatim where possible)
  config/never_store.yaml
  reference/csharp-sdk/   # git-ignored; vendored SDK samples for API reference
  docs/                   # phase1 spec, handoff, this repo's decisions.md
```
 
`MemSrv.Server` and `MemCtl` are thin executables over `MemSrv.Core`. Tool contracts
and CLI must not drift apart — both call the same core services.
 
## Database conventions
 
- Databases: `memory` (live) and `memory_test` (tests) on the existing DB LXC.
- Two roles: admin/migration role (runs DbUp) and `memsrv` (runtime; SELECT/INSERT on
  traces, no DELETE anywhere). Tests must verify the grants, not just the trigger.
- Use the DDL from the spec as written — including CHECK constraints, the
  `forbid_mutation` trigger, `retrieval_config` fallback rows, and indexes.
- Namespaces are rows, not enums. Phase 1 seeds: `memory-system`, `homelab`.
## Retrieval rules
 
- Lanes in Phase 1: **FTS** (`websearch_to_tsquery`, `ts_rank_cd`) + **recency**
  (`exp(-λ·age_hours)`, half-life from `retrieval_config`). Optional `pg_trgm`
  identifier lane — ship if trivial, else stub the lane function with a TODO.
- Lanes are a registered list the fusion loop iterates; adding a lane later must not
  touch fusion. RRF: `score = Σ 1/(60 + rank_lane)`.
- Search scope: approved memories in the namespace + caller's private memories
  (+ proposed only if `retrieval_config.include_proposed`). Namespace isolation is
  server-enforced; crossing namespaces requires an explicit `namespaces: [...]`
  argument AND key authorization.
## Definition of done (Session 1)
 
From a real Claude Code session: log a trace → propose a memory → approve via
`memctl` → find it via `search_memory` → fetch via `get_by_id` → the
`memory_consumed` event appears in the trace. Plus mechanical tests pass:
trace mutation fails via trigger AND via grants; private memories invisible to other
agents; shared writes cannot be born approved; never-store gate blocks a seeded fake
secret; RRF returns per-lane scores; namespace isolation holds.
 
## Working style
 
- Boring, maintainable, inspectable. Prefer plain SQL and small services over
  abstractions. No repository-pattern ceremony over Dapper.
- Small commits per tool/feature; commit messages reference the spec section.
- When the server runs, log build decisions into namespace `memory-system`
  (the repo is its own zeroth consumer). Until then, append to `docs/decisions.md`.
- Never put secrets in code, config committed to git, or test fixtures — use real
  fake patterns for never-store tests (e.g., a synthetic `AKIA...` string).
