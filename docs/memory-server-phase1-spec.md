# Memory Server — Phase 1 Build Spec
 
This is a build spec for a Claude Code session. It is deliberately narrow. The **Do Not Build** section is as binding as the requirements. The goal is a working v0 spine in 1–2 sessions that the homelab project can consume immediately.
 
Companion doc: `ansible-integration-checklist.md` (first consumer wiring).
Background: the project handoff (`agent-memory-handoff-v2.md`) governs intent; where this spec is silent, the handoff decides.
 
---
 
## 1. What this is
 
A single self-hosted server process exposing a small MCP tool surface (stdio and HTTP) over one PostgreSQL database (existing dedicated LXC). It is the memory/control substrate for multiple agents (Claude Code, Codex, later a personal assistant and dispatcher). It owns:
 
- an **append-only trace store** (immutable; primary for "what happened")
- a **memory store** (typed, provenance-carrying, versioned facts)
- a **proposal → approval** flow for shared durable memory
- **two-step hybrid retrieval** (lexical + recency, RRF-fused, per-lane scores preserved)
- **workstream** check-out/check-in for parallel-session coordination
The server is the **only door** to the database. Exactly one application role (`memsrv`) holds a connection string; no agent, MCP postgres bridge, or consumer ever connects directly. Migrations run under a separate admin role.
 
## 2. Stack
 
- **Language:** implementer's choice; default recommendation is Python 3.12 + the official MCP Python SDK. C# with the official MCP C# SDK is an acceptable alternative if the maintainer prefers .NET. Do not agonize; the schema and tool contracts are the product, the language is a vehicle.
- **Storage:** PostgreSQL 15+ on the existing DB LXC. One database (`memory`), one schema (`public` is fine), one app role (`memsrv`). A second database `memory_test` for the test suite. Add `memory` to the LXC's existing backup rotation **on day one** — the memory system is exactly as durable as its backups. Optionally a `mem_readonly` role for operator psql inspection.
- **Full-text:** built-in `tsvector` + GIN. Optional `pg_trgm` extension for the identifier/exact lane (see Retrieval). **No pgvector yet** (it is the documented seam for the future vector lane — same database, no new datastore).
- **Transports:** MCP over stdio (for local Claude Code/Codex) and MCP over HTTP with static bearer keys (for agents elsewhere on the LAN). One key per agent identity.
- **Operator CLI:** a small `memctl` command (same codebase) for approval, inspection, and admin. Approval is **not** exposed as an agent tool.
## 3. Identities and namespaces
 
- Every request carries `agent_id` (derived from the bearer key or stdio config, never self-asserted in tool arguments) and `namespace`.
- Phase 1 namespaces: `memory-system` (this repo — the zeroth consumer), `homelab`. Namespaces are rows in a table, not an enum; adding one is an insert.
- Namespace isolation is enforced server-side: a search never crosses namespaces unless the tool call explicitly passes `namespaces: [...]` and the agent's key is allowed those namespaces.
## 4. Schema (PostgreSQL DDL)
 
```sql
-- Run as admin/migration role. App role is memsrv.
 
CREATE TABLE namespaces (
  name        TEXT PRIMARY KEY,
  description TEXT,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
 
-- Append-only. Enforced at the CREDENTIAL level (grants), with a
-- belt-and-braces trigger underneath.
CREATE TABLE traces (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  trace_uuid  UUID NOT NULL UNIQUE DEFAULT gen_random_uuid(),
  session_id  TEXT NOT NULL,
  agent_id    TEXT NOT NULL,
  namespace   TEXT NOT NULL REFERENCES namespaces(name),
  event_type  TEXT NOT NULL,        -- see event taxonomy below
  content     JSONB NOT NULL,
  refs        UUID[],               -- memory uuids consumed/produced
  ts          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_traces_session ON traces(session_id, ts);
CREATE INDEX idx_traces_ns_ts   ON traces(namespace, ts);
 
CREATE OR REPLACE FUNCTION forbid_mutation() RETURNS trigger AS $$
BEGIN RAISE EXCEPTION 'traces are append-only'; END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER traces_immutable BEFORE UPDATE OR DELETE ON traces
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
 
-- Durable snapshots written by harness compaction BEFORE it deletes messages.
-- (Harness integration is later; the table and tool support exist now.)
CREATE TABLE trace_snapshots (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  session_id  TEXT NOT NULL,
  agent_id    TEXT NOT NULL,
  namespace   TEXT NOT NULL,
  snapshot    JSONB NOT NULL,       -- full pre-compaction message state
  summary     TEXT,                 -- the summary that replaced it
  ts          TIMESTAMPTZ NOT NULL DEFAULT now()
);
 
CREATE TABLE memories (
  id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  uuid          UUID NOT NULL UNIQUE DEFAULT gen_random_uuid(), -- identity provenance
  namespace     TEXT NOT NULL REFERENCES namespaces(name),
  type          TEXT NOT NULL
                CHECK (type IN ('decision','fact','preference','task','adr','runbook','note')),
  visibility    TEXT NOT NULL DEFAULT 'shared'
                CHECK (visibility IN ('private','shared')),      -- see write policy
  status        TEXT NOT NULL DEFAULT 'proposed'
                CHECK (status IN ('proposed','approved','rejected','superseded','retired')),
  tier          TEXT NOT NULL DEFAULT 'warm'
                CHECK (tier IN ('hot','warm','cold')),           -- columns now, mechanism later
  content       TEXT NOT NULL,
  -- source provenance
  source_type   TEXT NOT NULL,      -- trace|document|human|worker
  source_id     TEXT,               -- trace_uuid, file path, etc.
  agent_id      TEXT NOT NULL,
  session_id    TEXT,
  -- versioning (append-and-archive; never destructive overwrite)
  version       INTEGER NOT NULL DEFAULT 1,
  supersedes    UUID,               -- uuid of the memory this version replaces
  -- lifecycle
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  approved_by   TEXT,
  approved_at   TIMESTAMPTZ,
  retired_at    TIMESTAMPTZ,
  -- lexical lane
  search_tsv    tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);
CREATE INDEX idx_mem_ns_status ON memories(namespace, status);
CREATE INDEX idx_mem_source    ON memories(source_id);          -- reciprocal provenance
CREATE INDEX idx_mem_tsv       ON memories USING GIN (search_tsv);
-- Optional (identifier/exact lane): CREATE EXTENSION pg_trgm;
-- CREATE INDEX idx_mem_trgm ON memories USING GIN (content gin_trgm_ops);
 
-- Per-(agent, namespace) retrieval behavior. Differences between future
-- consumers (dispatcher vs assistant vs coder) are ROWS HERE, not code forks.
CREATE TABLE retrieval_config (
  agent_id             TEXT NOT NULL,
  namespace            TEXT NOT NULL,
  lanes                JSONB NOT NULL DEFAULT '["fts","recency"]',
  recency_half_life_h  REAL NOT NULL DEFAULT 720,                -- 30 days
  max_results          INTEGER NOT NULL DEFAULT 10,
  preview_chars        INTEGER NOT NULL DEFAULT 200,
  include_proposed     BOOLEAN NOT NULL DEFAULT false,
  PRIMARY KEY (agent_id, namespace)
);
-- A row ('*','*') holds defaults; lookup falls back agent→namespace→default.
 
-- Parallel-session coordination (inflight-work visibility).
CREATE TABLE workstreams (
  id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  uuid         UUID NOT NULL UNIQUE DEFAULT gen_random_uuid(),
  namespace    TEXT NOT NULL REFERENCES namespaces(name),
  title        TEXT NOT NULL,
  status       TEXT NOT NULL DEFAULT 'open'
               CHECK (status IN ('open','checked_out','done','abandoned')),
  owner_agent  TEXT,
  session_id   TEXT,
  notes        TEXT,                -- freeform state / handoff summary
  refs         UUID[],              -- related memory/trace uuids
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
 
-- Async outbox. EMPTY in Phase 1 — no worker exists. Its presence shapes
-- writes as queue-producing from day one (embedding_outbox pattern).
CREATE TABLE jobs (
  id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  job_type   TEXT NOT NULL,         -- e.g. 'enrich','embed','reconcile' (future)
  payload    JSONB NOT NULL,
  status     TEXT NOT NULL DEFAULT 'pending'
             CHECK (status IN ('pending','running','done','failed')),
  attempts   INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ
);
 
-- Governance at the credential boundary: the app role cannot mutate traces.
GRANT SELECT, INSERT ON traces TO memsrv;
GRANT SELECT, INSERT ON trace_snapshots TO memsrv;
GRANT SELECT, INSERT, UPDATE ON memories, workstreams, jobs, retrieval_config, namespaces TO memsrv;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO memsrv;
-- Note: no DELETE granted on anything. Retirement is a status flip.
```
 
### Trace event taxonomy (event_type)
 
`user_msg`, `assistant_msg`, `tool_call`, `tool_result`, `memory_consumed`, `memory_proposed`, `memory_written`, `approval`, `rejection`, `handoff`, `workstream_checkout`, `workstream_checkin`, `compaction_boundary`, `note`.
 
Normalization rule (Moraine-shaped): `tool_call` content is `{tool, params}`; `tool_result` content is `{tool, ok, summary, bytes}` — enough to recreate conditions when a bug happens. Store parameters verbatim.
 
## 5. Write policy (governance in code)
 
- **Private memories** (`visibility='private'`): the owning agent writes them directly via `save_note`; they are created with `status='approved'` automatically, but only ever retrieved for that same `agent_id` (server-enforced). Still fully traced and provenance-carrying.
- **Shared memories** (`visibility='shared'`): agents can only create them with `status='proposed'` via `propose_memory`. There is **no agent-facing approval tool.** Approval happens through `memctl approve <uuid>` (operator only). Approval/rejection writes an `approval`/`rejection` trace event.
- **Supersession:** approving a memory whose `supersedes` is set flips the old row to `status='superseded'` (it is never deleted) and logs it.
- **Never-store gate:** every write path (traces included) runs a denylist scan before insert — private keys, obvious password/token patterns, `.env`-style secrets (configurable regex list in `config/never_store.yaml`). On match: reject the write, return an error naming the rule, log a redacted `note` trace event that a write was blocked. This runs in code, not in prompts.
## 6. Causal provenance is captured server-side
 
Do not rely on agents to report what they used. The server logs it:
 
- Every `search_memory` call → trace event `tool_call` with the query.
- Every `get_by_id` call → trace event `memory_consumed` with `refs=[uuid]`, session, agent.
- Every `propose_memory`/`save_note` → `memory_proposed`/`memory_written` with the new uuid in `refs`.
This makes acceptance tests 1 and 4 (below) pure queries, with zero agent cooperation required.
 
## 7. Retrieval
 
Two lanes in Phase 1 (plus one optional), fused with **RRF at k=60**:
 
- **FTS lane:** `ts_rank_cd(search_tsv, websearch_to_tsquery('english', :q))` over approved memories in the namespace (plus the caller's private memories; plus proposed ones only if config says so).
- **Recency lane:** rank by `exp(-λ · age_hours)`, λ from `recency_half_life_h` in retrieval_config.
- **Optional exact/identifier lane (`trgm`):** `pg_trgm` similarity / ILIKE substring match. Postgres tsvector tokenizes identifiers like `idx_memory_units_text_search` poorly; this lane covers the exact-token failure mode. Register it in the lane list; ship it if trivial, otherwise leave the lane function stubbed with a TODO.
Rules:
 
- **Per-lane ranks and scores are preserved on every result** and returned in the response. One fused score for ordering, one score per lane for debugging. Never discard lane scores.
- **Two-step always:** `search_memory` returns uuid + type + tier + status + preview (`preview_chars`) + provenance decoration + lane scores. Full content only via `get_by_id`.
- Lanes are a registered list the fusion loop iterates over (the config's `lanes` array selects them). Adding the vector or trigram lane later means registering a lane function, not touching fusion.
## 8. Tool surface (MCP)
 
Every tool response is JSON and **ends with a `next` field**: a one-line hint about the most likely follow-up call (self-guiding responses). Examples given per tool.
 
**Core (must ship):**
 
1. **`log_trace`** `{session_id, event_type, content, refs?}` → `{trace_uuid}`
   `next`: "If this event contains a durable decision or fact, call propose_memory referencing this trace_uuid as source_id."
2. **`search_memory`** `{query, namespaces?, types?, limit?}` → `{results: [{uuid, type, tier, status, preview, source_type, source_id, version, lane_scores, fused_score}], next}`
   `next`: "Call get_by_id with a uuid to read full content. Nothing relevant? Consider propose_memory to fill the gap."
3. **`get_by_id`** `{uuid}` → full record incl. all provenance columns + version chain (`supersedes` / superseded-by).
   `next`: "This memory derives from source_id=<...>; retrieve_trace on it for full context." (Server logs `memory_consumed`.)
4. **`propose_memory`** `{namespace, type, content, source_type, source_id, supersedes?}` → `{uuid, status:'proposed'}`
   `next`: "Proposal recorded; an operator must approve before it becomes shared knowledge. Continue your task."
5. **`save_note`** `{namespace, type, content, source_type?, source_id?}` → private, auto-approved, owner-scoped.
   `next`: "Private note saved. If other agents need this, propose_memory instead."
**Coordination (ship if time in session 1, else session 2):**
 
6. **`list_workstreams`** `{namespace, status?}` → open/checked-out streams with owners — "check inflight work to avoid conflicts."
7. **`checkout_workstream`** `{uuid | title}` / **`checkin_workstream`** `{uuid, notes, refs?}` — check-in notes are the handoff summary; both log trace events.
8. **`create_handoff`** `{namespace, summary, refs}` → creates a workstream in `open` with the summary + reference uuids. The receiving agent gets the summary; the full record stays retrievable by reference.
**Explicitly not tools:** approve/reject (operator CLI only), raw SQL (later, read-only, if ever), anything that mutates traces.
 
## 9. Operator CLI (`memctl`)
 
`memctl pending [ns]` · `memctl show <uuid>` · `memctl approve <uuid> [--by name]` · `memctl reject <uuid> --reason "..."` · `memctl retire <uuid>` · `memctl trace <session_id>` (chronological session replay) · `memctl why <uuid>` (memory → source trace chain) · `memctl consumed <session_id>` (what the agent read). Plain text output is fine.
 
## 10. Acceptance tests
 
Ship these as an executable test script against a seeded `memory_test` database:
 
1. **"Why did you say that?"** Given a session_id, list all `memory_consumed` events and resolve each uuid to its source trace/document. (`memctl consumed` + `memctl why`.)
2. **"This source changed — what depends on it?"** Given a source_id, list every memory derived from it (index on `source_id`). *(The nightly reconciliation worker that automates this is Phase 3 — the query must work now.)*
3. **"Adjudicate these two facts."** Given two uuids, show capture timestamps, sources, versions, and supersession chain side by side.
4. **"Was this hallucinated?"** Given a session_id and a claim, show whether any consumed memory in that session contains it (FTS over the consumed set).
Plus mechanical tests: UPDATE/DELETE on traces fails **both** via trigger and via the `memsrv` role's grants; private memories invisible to other agents; shared writes cannot be born approved; never-store gate blocks a seeded fake secret; RRF returns per-lane scores; namespace isolation holds.
 
## 11. Do Not Build (binding)
 
- ❌ Embeddings, pgvector, or any embedding model integration (the `jobs` table and lane registry are the future seams; that's all)
- ❌ Graph storage or graph lanes
- ❌ Any LLM-calling worker (no extraction, no consolidation, no reconciliation) — the `jobs` table stays empty
- ❌ Tiering *mechanics* (promotion/demotion/TTL) — the `tier` column exists, nothing moves rows between tiers yet
- ❌ Web UI or dashboard — `memctl` only
- ❌ Additional datastores (**ClickHouse, TimescaleDB**, Redis, vector DBs, queues) or multi-node anything — one server process, one Postgres database, one systemd unit. Ease of deployment is not the cost of a second datastore; the second schema, the backup story, and the broken trace↔memory join are. Acceptance tests 1 and 4 are joins.
- ❌ Auth beyond static bearer keys mapped to agent_ids
- ❌ Dispatcher/orchestrator logic of any kind
- ❌ Harness extensions (Pi/Claude Code compaction hooks) — `trace_snapshots` + `log_trace(compaction_boundary)` are the landing pads; integration is a later, separate task
If mid-session an idea appears that isn't in this spec: `save_note` it into namespace `memory-system` as type `task` and move on.
 
## 12. Milestones
 
- **Session 1:** repo scaffold; provision `memory` + `memory_test` databases and the `memsrv` role on the DB LXC; DDL + migrations incl. grants and append-only trigger; never-store gate; tools 1–5 over stdio MCP; FTS+recency RRF with lane scores; `memctl approve/pending/show`. *Definition of done: from a Claude Code session, log a trace, propose a memory, approve it via CLI, find it via search, fetch it by id — and the consumed event appears in the trace.*
- **Session 2:** HTTP transport + bearer keys, tools 6–8, remaining `memctl` commands, acceptance-test script, provenance decoration polish, README with the wiring instructions for consumers.
- **First act after Session 1:** the server logs its own build decisions into namespace `memory-system` (zeroth consumer, recursive loop seeded).
## 13. Forward seams (documented, not built)
 
- **Vector lane:** `CREATE EXTENSION pgvector` in the same database + a `jobs` consumer that populates an embeddings table via the outbox; prefer a **local embedding model** when it arrives (cost profile stays flat). No new datastore, ever.
- **First worker:** the **nightly reconciliation job** — diff git sources + read the day's traces/decisions → emit **proposals** (never direct writes), on a cheap model. The `jobs` outbox gains `LISTEN/NOTIFY` wake-ups then; no polling loops.
- **Trigram lane:** if stubbed in Phase 1, finish it the first time an identifier-shaped search misses.
- **Tiering mechanics:** write-time classification first (supermemory-style), demotion via recency states.
- **Harness integration:** thin Pi/Claude Code extensions (~100 lines) calling this surface; trace-first compaction writes `trace_snapshots` before deleting.
- **Scale-out:** if trace volume ever hurts, the first move is **in-place** — native partitioning or a Timescale hypertable on `traces`, same database, causal joins intact (this is the friend's exact setup). ClickHouse enters only if Moraine-scale trace *analytics* ever becomes a real workload, and then strictly as a **downstream replica fed from `traces`** — never as the system of record. Immutability makes either move the easiest migration in the system.
 
