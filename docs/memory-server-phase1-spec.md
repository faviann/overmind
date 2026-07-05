# Memory Server — Phase 1 Build Spec (v1.1)

This is a build spec for a Claude Code session. It is deliberately narrow. The **Do Not Build** section is as binding as the requirements. The goal is a working v0 spine in 1–2 sessions that the homelab project can consume immediately.

> **v1.1 changelog (mid-build amendments — small, apply before closing Phase 1):**
> 1. `memories` gains `metadata JSONB` and `content_hash`; type CHECK gains `constraint`, `open_question`, `warning`.
> 2. Never-store gate splits behavior: **reject** for memory writes, **redact-in-place** for trace writes.
> 3. `memctl approve` gains `--edit`; reviewer identity (`--by`) is **required** on approve/reject.
> 4. New §6b: **review event convention** — approval/rejection trace events carry reviewer identity in a synthetic review session, never the proposing agent's identity.
> 5. Trace metadata conventions documented (model/provider on `assistant_msg`; repo/branch/cwd on tool events).
> 6. Forward seams add the `memctl export` projection boundary.
> 7. Acceptance tests extended accordingly.
> 8. Test-DB location reconciled (see §2/§12): dev/test/CI use a disposable local `memory_test`; the LXC hosts production `memory` only. Language bullet aligned to the committed .NET stack in handoff v4.

Companion doc: `ansible-integration-checklist.md` (first consumer wiring).
Background: the project handoff (`agent-memory-handoff-v4.md`) governs intent; where this spec is silent, the handoff decides.

---

## 1. What this is

A single self-hosted server process exposing a small MCP tool surface (stdio and HTTP) over one PostgreSQL database (existing dedicated LXC). It is the memory/control substrate for multiple agents (Claude Code, Codex, later a personal assistant and dispatcher). It owns:

- an **append-only trace store** (immutable; primary for "what happened")
- a **memory store** (typed, provenance-carrying, versioned facts)
- a **proposal → approval** flow for shared durable memory (approve / edit-then-approve / reject)
- **two-step hybrid retrieval** (lexical + recency, RRF-fused, per-lane scores preserved)
- **workstream** check-out/check-in for parallel-session coordination

The server is the **only door** to the database. Exactly one application role (`memsrv`) holds a connection string; no agent, MCP postgres bridge, or consumer ever connects directly. Migrations run under a separate admin role.

## 2. Stack

- **Language:** **.NET with the official MCP C# SDK** (committed — see handoff v4). The schema and tool contracts are the product; the language is the vehicle for them.
- **Storage:** PostgreSQL 15+. The existing DB LXC hosts **production `memory` only** — one database, one schema (`public` is fine), one app role (`memsrv`). Add `memory` to the LXC's existing backup rotation **on day one** — the memory system is exactly as durable as its backups. Optionally a `mem_readonly` role for operator psql inspection. **Dev/test/CI use a disposable, locally-provisioned `memory_test`, never a persistent database on the LXC** — because the trace store is append-only and no-delete by grant and by trigger, contamination of the prod ledger is unrecoverable, so it is made structurally impossible rather than avoided by care. Two safeguards keep the disposable target faithful: (1) pin it to the LXC's exact Postgres **major version** and extension set, and run the same migrations; (2) the production **grant/trigger/role-shape assertions live in the on-LXC disposable verify step** (spin up, prove, drop) owned by provisioning — they do **not** move into the app's local suite. The app's suite may still assert the append-only trigger, which ships in the migrations.
- **Full-text:** built-in `tsvector` + GIN. Optional `pg_trgm` extension for the identifier/exact lane (see Retrieval). **No pgvector yet** (it is the documented seam for the future vector lane — same database, no new datastore).
- **Transports:** MCP over stdio (for local Claude Code/Codex) and MCP over HTTP with static bearer keys (for agents elsewhere on the LAN). One key per agent identity.
- **Operator CLI:** a small `memctl` command (same codebase) for approval, inspection, and admin. Approval is **not** exposed as an agent tool.

## 3. Identities and namespaces

- Every request carries `agent_id` (derived from the bearer key or stdio config, never self-asserted in tool arguments) and `namespace`.
- Phase 1 namespaces: `memory-system` (this repo — the zeroth consumer), `homelab`. Namespaces are rows in a table, not an enum; adding one is an insert.
- Namespace isolation is enforced server-side: a search never crosses namespaces unless the tool call explicitly passes `namespaces: [...]` and the agent's key is allowed those namespaces.
- Naming convention: path-style names (`homelab`, `homelab/traefik`, `repo/<owner>/<name>`) give hierarchy for free without schema change. Flat rows, hierarchical names.

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
                CHECK (type IN ('decision','fact','preference','task','adr','runbook','note',
                                'constraint','open_question','warning')),   -- v1.1: +3 types
  visibility    TEXT NOT NULL DEFAULT 'shared'
                CHECK (visibility IN ('private','shared')),      -- see write policy
  status        TEXT NOT NULL DEFAULT 'proposed'
                CHECK (status IN ('proposed','approved','rejected','superseded','retired')),
  tier          TEXT NOT NULL DEFAULT 'warm'
                CHECK (tier IN ('hot','warm','cold')),           -- columns now, mechanism later
  content       TEXT NOT NULL,
  content_hash  TEXT NOT NULL,      -- v1.1: sha256 of content, computed server-side at every
                                    -- write/version. Substrate for future dedup; also lets
                                    -- "was this exact content already proposed?" be a query.
  metadata      JSONB NOT NULL DEFAULT '{}',
                                    -- v1.1: escape hatch. Future workers put extraction_model,
                                    -- extraction_prompt_version, event_date, dedup scores etc.
                                    -- here without migrations. Persist any write-time signal.
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
CREATE INDEX idx_mem_hash      ON memories(content_hash);       -- v1.1: dedup substrate
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
-- writes as queue-shaped from day one (embedding_outbox pattern).
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

`event_type` is open text, not an enum — consumers may add types as they earn them. Pre-blessed additions (from the predecessor project's event contract): `error`, `command_run`, `file_observed`, `file_modified`.

Normalization rule (Moraine-shaped): `tool_call` content is `{tool, params}`; `tool_result` content is `{tool, ok, summary, bytes}` — enough to recreate conditions when a bug happens. Store parameters verbatim (subject to the redaction rule in §5).

**Metadata conventions (v1.1).** Content is JSONB, so these are conventions, not columns — but they are *documented* conventions the consumers should follow:

- `assistant_msg` events SHOULD carry `model` and `provider` in content — which model made a claim is provenance you will want the first time a hallucination is attributed across agents.
- Tool events in repo-shaped namespaces SHOULD carry `repo`, `branch`, `cwd` where applicable.
- `tool_result` MAY carry `duration_ms` and `exit_code` alongside `{tool, ok, summary, bytes}`.

## 5. Write policy (governance in code)

- **Private memories** (`visibility='private'`): the owning agent writes them directly via `save_note`; they are created with `status='approved'` automatically, but only ever retrieved for that same `agent_id` (server-enforced). Still fully traced and provenance-carrying.
- **Shared memories** (`visibility='shared'`): agents can only create them with `status='proposed'` via `propose_memory`. There is **no agent-facing approval tool.** Approval happens through `memctl approve <uuid>` (operator only). Approval/rejection writes an `approval`/`rejection` trace event **following the review event convention in §6b.**
- **Edit-then-approve (v1.1):** `memctl approve <uuid> --edit` opens `$EDITOR` on the proposal content; the amended text becomes the approved memory. The proposal's original content is preserved (append-and-archive: the approved row is a new version superseding the proposal content, or the original is retained in `metadata.original_content` — implementer's choice, but the original must remain queryable). The approval trace event records `amended: true`. Rationale: the common real case is a proposal that is 90% right with wrong wording; reject-and-lose-it wastes the fact and the calibration signal.
- **Supersession:** approving a memory whose `supersedes` is set flips the old row to `status='superseded'` (it is never deleted) and logs it.
- **Never-store gate (v1.1 — split behavior):** every write path runs a denylist scan before insert — private keys, obvious password/token patterns, `.env`-style secrets (configurable regex list in `config/never_store.yaml`). Behavior differs by store:
  - **Memory writes (`propose_memory`, `save_note`): REJECT.** Return an error naming the rule; log a redacted `note` trace event that a write was blocked.
  - **Trace writes (`log_trace` and server-side auto-logging): REDACT in place.** Replace each matched span with `[REDACTED:<rule-name>]` and record the event. A tool result that happens to contain a token still describes something that happened; dropping the event silently would trade a secrets risk for an audit hole, and the acceptance tests are trace joins. Redaction happens *before* insert — it is write-time sanitization, not mutation of an append-only row.
  - Both paths run in code, not in prompts.

## 6. Causal provenance is captured server-side

Do not rely on agents to report what they used. The server logs it:

- Every `search_memory` call → trace event `tool_call` with the query.
- Every `get_by_id` call → trace event `memory_consumed` with `refs=[uuid]`, session, agent.
- Every `propose_memory`/`save_note` → `memory_proposed`/`memory_written` with the new uuid in `refs`.

This makes acceptance tests 1 and 4 (below) pure queries, with zero agent cooperation required.

## 6b. Review event convention (v1.1)

The approval is itself a provenance-carrying event with its **own actor**. The source event says what produced the candidate memory; the review event says who accepted or rejected it. Never let the review event borrow the proposing session or agent identity — that makes the review look like stronger provenance than it is.

`memctl approve` / `memctl reject` write their trace events as:

- `session_id`: synthetic — `review:<proposal_uuid>`
- `agent_id`: the reviewer identity, e.g. `human:faviann` — **required** (`--by` is mandatory, not optional; no anonymous approvals)
- `namespace`: the proposal's namespace
- `event_type`: `approval` | `rejection`
- `refs`: `[proposal_uuid, source_trace_uuid]`, plus the approved memory's uuid on approval
- `content`: minimal — `{reviewer, amended: bool, reason?}` (reason required on reject)

This is a trace convention, not a review workflow product. A future review system can evolve it into richer authenticated review sessions while preserving the recorded reviewer, proposal, source, and result identifiers.

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

**Explicitly not tools:** approve/edit/reject (operator CLI only), raw SQL (later, read-only, if ever), anything that mutates traces.

## 9. Operator CLI (`memctl`)

`memctl pending [ns]` · `memctl show <uuid>` · `memctl approve <uuid> --by <name> [--edit]` · `memctl reject <uuid> --by <name> --reason "..."` · `memctl retire <uuid>` · `memctl trace <session_id>` (chronological session replay) · `memctl why <uuid>` (memory → source trace chain) · `memctl consumed <session_id>` (what the agent read). Plain text output is fine.

v1.1 notes: `--by` is **required** on approve/reject (see §6b); `--edit` opens `$EDITOR` and preserves the original content (see §5).

## 10. Acceptance tests

Ship these as an executable test script against a seeded, disposable local `memory_test` database (see §2):

1. **"Why did you say that?"** Given a session_id, list all `memory_consumed` events and resolve each uuid to its source trace/document. (`memctl consumed` + `memctl why`.)
2. **"This source changed — what depends on it?"** Given a source_id, list every memory derived from it (index on `source_id`). *(The nightly reconciliation worker that automates this is Phase 3 — the query must work now.)*
3. **"Adjudicate these two facts."** Given two uuids, show capture timestamps, sources, versions, and supersession chain side by side — including who approved each (from the review events), distinctly from who proposed each.
4. **"Was this hallucinated?"** Given a session_id and a claim, show whether any consumed memory in that session contains it (FTS over the consumed set).

Plus mechanical tests: UPDATE/DELETE on traces fails **both** via trigger and via the `memsrv` role's grants; private memories invisible to other agents; shared writes cannot be born approved; never-store gate **blocks** a seeded fake secret on the memory path and **redacts** it on the trace path (event recorded, secret absent); RRF returns per-lane scores; namespace isolation holds; **(v1.1)** every memory row has a valid `content_hash`; approval without `--by` fails; approval trace event carries `review:<uuid>` session and a reviewer agent_id distinct from the proposer; `--edit` approval preserves original content and marks `amended: true`.

## 11. Do Not Build (binding)

- ❌ Embeddings, pgvector, or any embedding model integration (the `jobs` table and lane registry are the future seams; that's all)
- ❌ Graph storage or graph lanes
- ❌ Any LLM-calling worker (no extraction, no consolidation, no reconciliation) — the `jobs` table stays empty
- ❌ Tiering *mechanics* (promotion/demotion/TTL) — the `tier` column exists, nothing moves rows between tiers yet
- ❌ Web UI or dashboard — `memctl` only
- ❌ Additional datastores (**ClickHouse, TimescaleDB**, Redis, vector DBs, queues) or multi-node anything — one server process, one Postgres database, one systemd unit. Ease of deployment is not the cost of a second datastore; the second schema, the backup story, and the broken trace↔memory join are. Acceptance tests 1 and 4 are joins.
- ❌ Auth beyond static bearer keys mapped to agent_ids
- ❌ Dispatcher/orchestrator logic of any kind (its design corpus is `deferred-knowledge-and-dispatcher-notes.md`; it stays a document)
- ❌ Harness extensions (Pi/Claude Code compaction hooks) — `trace_snapshots` + `log_trace(compaction_boundary)` are the landing pads; integration is a later, separate task
- ❌ Dedup logic — `content_hash` is written and indexed, nothing *acts* on it yet
- ❌ The export/projection commands — documented seam only (see §13)

If mid-session an idea appears that isn't in this spec: `save_note` it into namespace `memory-system` as type `task` and move on.

## 12. Milestones

- **Session 1:** repo scaffold; provision production `memory` and the `memsrv` role on the DB LXC (dev/test/CI run against a disposable local `memory_test` pinned to the LXC major version — see §2); DDL + migrations incl. grants and append-only trigger; never-store gate (reject/redact split); tools 1–5 over stdio MCP; FTS+recency RRF with lane scores; `memctl approve/pending/show` with required reviewer identity and review-event convention. *Definition of done: from a Claude Code session, log a trace, propose a memory, approve it via CLI (with `--by`), find it via search, fetch it by id — and the consumed event appears in the trace, and the approval event carries the reviewer in a `review:` session.*
- **Session 2:** HTTP transport + bearer keys, tools 6–8, remaining `memctl` commands incl. `--edit`, acceptance-test script, provenance decoration polish, README with the wiring instructions for consumers.
- **First act after Session 1:** the server logs its own build decisions into namespace `memory-system` (zeroth consumer, recursive loop seeded).

## 13. Forward seams (documented, not built)

- **Vector lane:** `CREATE EXTENSION pgvector` in the same database + a `jobs` consumer that populates an embeddings table via the outbox; prefer a **local embedding model** when it arrives (cost profile stays flat). No new datastore, ever. The embeddings table is a **derived projection**: it must be rebuildable from `memories` at any time and is never the only place truth exists.
- **Projection/export boundary (v1.1):** `memctl export approved --format jsonl|markdown [--namespace ns]` and `memctl export trace --session <id> --format jsonl`. This is the documented seam through which wikis, external memory tools (Hindsight, Graphify, LLM-Wiki, …), and future indexes consume the canonical ledger — they are evaluated as *projections/query engines over it*, never as candidate owners of it. Phase 2, cheap, and it operationalizes "derived indexes are disposable."
- **First worker:** the **nightly reconciliation job** — diff git sources + read the day's traces/decisions → emit **proposals** (never direct writes), on a cheap model. The `jobs` outbox gains `LISTEN/NOTIFY` wake-ups then; no polling loops. When it arrives, it records `extraction_model` and `extraction_prompt_version` in `metadata` on every proposal it emits — calibration data for future auto-approval policy.
- **Event date vs capture date:** recency of the write is not recency of the evidence. When the first stale-memory incident hits the dogfooding watch list, the fix is an `event_date` key in `metadata` feeding the recency lane — pre-decided, not built.
- **Trigram lane:** if stubbed in Phase 1, finish it the first time an identifier-shaped search misses.
- **Tiering mechanics:** write-time classification first (supermemory-style), demotion via recency states.
- **Dedup:** the first consumer of `content_hash` is a cheap exact-duplicate check at propose time (warn, don't block); LLM-assisted near-dup synthesis comes with the workers.
- **Harness integration:** thin Pi/Claude Code extensions (~100 lines) calling this surface; trace-first compaction writes `trace_snapshots` before deleting.
- **Scale-out:** if trace volume ever hurts, the first move is **in-place** — native partitioning or a Timescale hypertable on `traces`, same database, causal joins intact. ClickHouse enters only if Moraine-scale trace *analytics* ever becomes a real workload, and then strictly as a **downstream replica fed from `traces`** — never as the system of record. Immutability makes either move the easiest migration in the system.
