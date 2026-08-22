# AGENTS.md — Memory Server

Self-hosted memory/control substrate: one .NET server exposing a small MCP tool
surface over one PostgreSQL database. Traces are append-only, memories carry
provenance, shared memories follow proposal→approval, and retrieval is two-step.

## Authority

1. `docs/evidence-and-knowledge-boundary.md` — binding for the evidence /
   knowledge / governance ownership split, for the Phase 1 invariants it
   preserves, and for the transitional legacy-capture credential and identity
   separation it keeps in force (2026-08-20 course-correction)
2. `docs/memory-server-phase1-spec.md` — binding for Phase 1 contracts and
   every area whose ownership the boundary does not decide
3. `docs/decisions.md` (dated entries) and `docs/adr/` — decisions that refine
   the above; a decision changes binding scope only after an applicable spec
   records it

This file and `docs/design-rules.md` summarize and route. They introduce no
independent authority: where either differs from the documents above, those
documents win. `docs/north-star.md` orients and never wins conflicts.

Where all three tiers are silent, nothing else decides for them: do not treat
an older or informational document as authority. Raise the gap with the
operator only when it is a **material policy or architecture decision** — one
that changes a contract, an invariant, or a binding scope. Ordinary
implementation choices that the binding documents deliberately leave open stay
with the implementer.

The Phase 2 capture spec (`docs/conversation-capture-phase2-spec.md`) is
superseded and no longer binding. Phase 1 memory and governance invariants
remain binding; capture-specific Phase 2 amendments do not remain binding
merely because they were implemented.

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
- **Do not broaden scope beyond the applicable binding spec.**

## Read before changing

- MCP hosting or tools → run `make sdk-reference`, then inspect the relevant
  centrally pinned, version-matched upstream documentation or sample in
  `reference/csharp-sdk/`. Trust that evidence over memory. Remote transport
  uses Streamable HTTP, never legacy SSE.
- Tests or refactors → `docs/testing.md`
- New conversation-capture or evidence-storage work →
  `docs/evidence-and-knowledge-boundary.md`. It decides who owns what; read it
  before proposing any capture change.
- Maintaining the capture code that already ships → the frozen capture
  documents, which stay accurate for that purpose and decide nothing new:
  `docs/capture-modules.md` (enrollment, ingestion, receipt reads),
  `docs/capture-adapter-contract.md` (adapters, fixture parsing),
  `docs/codex-capture-runtime.md` (catch-up runtime),
  `docs/capture-synthetic-slice.md` (synthetic fixture).
  The superseded Phase 2 spec is not part of this route.
- Never-store rules, scan budgets, or redaction markers →
  `docs/capture-safety-budgets.md` (unchanged by the course-correction)
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
