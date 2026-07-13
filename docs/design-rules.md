# Design rules — established cross-cutting constraints

This guide is a progressive-disclosure summary of decisions that are already
made. It introduces **no independent architectural decisions**: every rule
below inherits its authority from the document it cites, and that document
wins over this summary wherever they differ. Choices the sources genuinely
leave open are listed under "Open boundaries" for human resolution, not
settled here.

Authority order (same as `AGENTS.md`):

1. `docs/memory-server-phase1-spec.md` ("the spec", cited by §) — binding
   build details, schema, and tool contracts
2. `docs/agent-memory-handoff-v4.md` ("the handoff") — intent and
   architecture where the spec is silent
3. `docs/decisions.md` (dated entries) and `docs/adr/` — decisions that amend
   or refine the above; the spec's changelogs fold the binding ones in

## Scope boundaries

- **The spec's Do Not Build list is binding** (spec §11). Headline: no
  embeddings/pgvector, no graph storage, no LLM-calling workers, no tiering
  mechanics, no web UI or dashboard, no additional datastores, no auth beyond
  static bearer keys, no dispatcher/orchestrator, no harness extensions, no
  dedup logic, no export commands yet. Read the full list before adding
  anything.
- **Forward seams are documented, not built** (spec §13): vector lane,
  `memctl export` projection boundary, nightly reconciliation worker,
  event-date recency, trigram-lane completion, tiering mechanics, dedup,
  harness integration, in-place scale-out.
- **Ideas outside the spec do not grow the current slice**: `save_note` them
  as type `task` in namespace `memory-system` and move on (spec §11), or
  raise them with the human via `docs/decisions.md`.
- **Closed decisions stay closed** until the maintainer reopens them; several
  entries in `docs/decisions.md` name explicit evidence doors for reopening
  (e.g. the session-override door in the 2026-07-10 #17 entry).

## Schema invariants

- **Traces are append-only, enforced twice**: the `memsrv` grants exclude
  UPDATE/DELETE on `traces`, and the `forbid_mutation` trigger backstops
  them. No DELETE is granted on any table; retirement is a status flip
  (spec §4).
- **Memories version by append-and-archive**: a new row `supersedes` the old
  uuid; superseded and retired rows are never deleted and remain queryable
  (spec §4–5; handoff "Update semantics").
- **Provenance columns are v1 schema, not a retrofit**: identity (uuid,
  version, `content_hash`), source (`source_type`, `source_id`, agent,
  session), causal consumption logging, and versioning are required now
  (handoff "Provenance: the concrete spec, not a slogan").
- **Causal provenance is captured server-side**: `get_by_id` logs
  `memory_consumed`, writes log `memory_proposed`/`memory_written`; never
  rely on agents to report what they used (spec §6).
- **Write-time signals are persisted**: `content_hash` is computed
  server-side on every write/version, and `metadata JSONB` is the
  migration-free landing pad for future write-time signals (spec §4; handoff:
  "if a signal is computed at write time, persist it").
- **Status and visibility state machines** (spec §5): shared memories are
  born `proposed`; private notes are auto-`approved` and owner-scoped;
  approving a superseding memory flips the old row to `superseded`; only
  `approved` memories can be retired, atomically with their `retirement`
  trace event (spec §6c; decisions 2026-07-10 #18;
  `docs/adr/0001-provenance-carrying-memory-retirement.md`).
- **`agent_id`, `session_id`, and namespace are server-derived**, never
  trusted from tool arguments, and namespace isolation is enforced
  server-side (spec §3; spec v1.3 changelog and decisions 2026-07-10 #17).
- **Review and retirement events carry their own actor** in synthetic
  sessions (`review:<proposal_uuid>`, `retirement:<memory_uuid>`) — never the
  proposing agent's identity, never anonymous (spec §6b–6c).
- **`event_type` is open text, not an enum**; consumers add types as they
  earn them (spec §4, trace event taxonomy).
- **Columns before mechanics**: the `tier` column exists but nothing moves
  rows between tiers; the `jobs` outbox exists but stays empty (spec §4,
  §11).

## Retrieval constraints

- **Two-step always**: `search_memory` returns uuid, type, tier, status,
  preview, provenance decoration, and lane scores; full content only via
  `get_by_id` (spec §7).
- **Hybrid lanes fused with RRF at k=60**: FTS + recency in Phase 1, plus the
  optional trigram/identifier lane. Lanes are a registered list selected by
  `retrieval_config`; adding a lane means registering a lane function, never
  touching fusion (spec §7).
- **Per-lane ranks and scores are preserved on every result** — one fused
  score for ordering, one score per lane for debugging; never discard lane
  scores (spec §7; handoff "Retrieval: the agent decides what it sees").
- **No vector lane yet**: pgvector is a documented seam in the same database,
  added when dogfooding shows semantic misses hurting — not before (spec §2,
  §13).
- **Search is namespace-scoped**: approved memories in the caller's
  namespace, plus the caller's own private memories, plus proposed ones only
  if config allows; a search never crosses namespaces unless the call names
  them and the credential is allowed them (spec §3, §7).
- **Per-consumer retrieval differences are `retrieval_config` rows, not code
  forks** (spec §4).
- **Recency is a first-class ranking lane** (exponential decay from
  `recency_half_life_h`) and doubles as soft demotion (spec §7; handoff).
- **Memory is a tool surface the agent calls, not middleware**: no
  auto-injection, no predicting what the agent needs (handoff "Retrieval:
  the agent decides what it sees").

## Dependencies and stack

Committed — do not re-litigate without the maintainer:

- **.NET with the official MCP C# SDK over PostgreSQL, major pinned to 18**
  (minor floats). The schema and tool contracts are the product; the language
  is the vehicle (spec §2; decisions 2026-07-07 "Production substrate and
  deployment contract").
- **One datastore, period.** No ClickHouse, Redis, vector DB, or queue as a
  second store: trace↔memory joins are load-bearing (acceptance tests 1 and
  4 *are* joins), and scale-out, if ever needed, is partitioning in place
  (spec §11, §13; handoff "Important architectural preferences").
- **Full-text is built-in `tsvector` + GIN**, with `pg_trgm` optional for the
  identifier/exact lane; no pgvector yet (spec §2).
- **Migrations are DbUp plain-SQL**, journaled in the `schemaversions` table,
  and **never create roles** — provisioning owns roles (Ansible in prod, the
  Compose bootstrap in dev/CI) (`docs/deployment-contract.md`; decisions
  2026-07-07).
- **Package versions are centrally pinned** in `Directory.Packages.props`;
  any MCP hosting or tool change starts with `make sdk-reference` and the
  version-matched evidence in `reference/csharp-sdk/` (`AGENTS.md`).
- **Dev/test/CI use a disposable local database, never the production LXC** —
  contamination of an append-only, no-delete ledger is unrecoverable, so it
  is made structurally impossible rather than avoided by care (spec §2;
  decisions 2026-07-05).

## Major solution boundaries

- **The server is the only database door.** Exactly one app role (`memsrv`)
  holds a connection string; consumers get bearer keys, never connection
  strings. A direct connection silently bypasses the never-store gate, causal
  logging, namespace isolation, and the write policy (spec §1–2; handoff
  "Important architectural preferences").
- **Approve / edit-then-approve / reject / retire are `memctl`-only operator
  actions**, deliberately not agent-facing tools (spec §5, §8–9).
- **Governance lives in code, not prompts.** The never-store gate runs on
  every write path: **reject** for memory writes, **redact in place** for
  trace writes — dropping the event would trade a secrets risk for an audit
  hole (spec §5).
- **Canonical ledger, derived projections.** Truth is the trace + proposal +
  approved-memory ledgers; every index, export, wiki, or external memory tool
  is a rebuildable projection over them, never a candidate owner (handoff
  "What the project is really about"; spec §13).
- **The raw trace store is primary and immutable**; summaries, extractions,
  and compacted views are derived artifacts that point back into it and never
  replace it (handoff "The two paradigm commitments").
- **Harnesses are thin clients.** Memory logic never lives in harness
  extensions; every harness hits the same MCP surface, and building harness
  extensions now is Do-Not-Build (handoff "Harness directive"; spec §11).
- **Source-of-truth hierarchy resolves conflicts**: Git/IaC > approved memory
  > proposed memory > raw trace inference. Propose the *why*; the *what*
  lives in the repo, where memory would only rot against it (handoff "Update
  semantics" and "Memory placement discipline").
- **Tool responses are self-guiding JSON**: camelCase properties on the wire,
  ending with a `next` hint (spec §8; decisions 2026-07-12 camelCase).

## Open boundaries (surface to the human; do not settle silently)

- **The data-access library is not pinned by any current authority.** The
  spec commits to .NET + PostgreSQL; the repo uses Npgsql + Dapper with
  hand-written SQL (see `Directory.Packages.props`). Historical guidance
  declared "no EF Core" binding, but no current authority restates it. Treat
  introducing an ORM as a maintainer decision, in either direction.
- **`retrieve_trace` is referenced but never specified.** The `get_by_id`
  `next` hint in spec §8 points agents at `retrieve_trace`, and the handoff
  lists it in the tool surface, but the spec's tool list does not define it
  and the server does not implement it. Its contract — or the hint's removal
  — is a spec gap for the maintainer.

## Not owned here

- Testing conventions → `docs/testing.md`
- Deployment and runtime contract → `docs/deployment-contract.md`
- Domain vocabulary → `CONTEXT.md` and `docs/agents/domain.md`
- Always-on invariants and task routing → `AGENTS.md`
- Full DDL, tool contracts, and acceptance tests → the spec itself
