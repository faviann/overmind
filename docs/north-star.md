# North star — what the mature Overmind memory system is

> **Status: informational.** This doc orients; it never wins conflicts.
> `memory-server-phase1-spec.md` stays binding on build details,
> `agent-memory-handoff-v4.md` on intent and architecture. Provenance:
> wayfinder ticket [#13](https://github.com/faviann/overmind/issues/13),
> grounded in [`north-star-distillation.md`](north-star-distillation.md)
> (17 claims from the article corpus), settled in a grilling session with
> faviann on 2026-07-09.

## Identity

A **personal memory substrate, OSS-visible**: built for one operator's agents
and homelab. The repo is public and readable, but there is exactly one
operator — design trade-offs favor the single-operator case, and anything
that only benefits hypothetical other tenants is waste until proven
otherwise.

The mature system is what the handoff already names — a durable, inspectable,
self-hosted memory ledger with derived projections — with one identity fact
made explicit: **the working agent is the extractor, and the human is the
quality gate.** There is no ingestion pipeline pouring facts in; memories are
deliberate, typed, provenance-carrying atoms written at the moment of work
and admitted through review. Traces are the archive (append-only, outside
the search path, reached by reference); memories are the shelf (searchable,
human-curated). The shelf grows only as fast as the operator reads.

## The decision meta-rule

Positions here follow one pattern, taken from the project's own article
corpus: **the conservative rule stands, and the only thing that reopens a
question is named evidence.** Nothing changes automatically; an evidence
door describes what would justify *proposing* a change to the operator, not
what triggers one. Corollary (soft preference, not a binding principle):
when scoping, lean toward whatever starts real agent traffic sooner — every
evidence door below stays shut until real usage generates evidence.

## Standing positions

### Approval gate — human rule + evidence door

Human approval is the rule; no shared memory enters unreviewed, so no noise
enters the store. Every approve / edit-then-approve / reject is also
calibration data the schema deliberately records. Auto-approval is not
promised. **Evidence door:** a worker's proposals of a given type run months
with near-zero edits and rejections *and* review volume demonstrably hurts —
then, and only then, may policy-gated auto-approval be proposed, per
(worker, prompt-version, type), revocable. High-stakes types (`decision`,
`constraint`, `adr`) stay human-gated regardless. If review fatigue arrives
before the evidence, the first response is better review tooling (batching,
sampling), never a lower gate.

### Isolation — code-enforced single door; RLS consciously skipped

Namespace isolation and private-note visibility are enforced in server code
behind the single door. This is a deliberate YAGNI call, not an oversight:
no agent ever writes SQL, keys are operator-provisioned, and there is one
door. Postgres row-level security is **not** part of the destination.
**Tripwires (any one makes RLS mandatory):** an agent-facing raw-SQL tool;
a key class less trusted than the operator's own agents; any second door
into the database. To keep the retrofit cheap, isolation checks stay
centralized behind one seam in the service layer (the Session 2 per-request
context is that seam).

### Tiering and demotion — mechanics deferred with confidence

No tier mechanics, no TTLs, no promotion/demotion machinery until rot is
observed. The deferral is safe because the schema already captures every
signal a retroactive classifier needs: timestamps, types, version chains,
and — the one most systems lose forever — complete access history via
server-side `memory_consumed` events from day one. Accepted defenses until
then: recency decay in ranking and supersession status. **Evidence door:**
stale or irrelevant results crowding real answers become entries on the
dogfooding watch-list; recurring entries schedule the tiering work. "It is
an impediment now" must be a tally, not a vibe.

### Write-side quality — the human gate is the backstop

Atomisation, relevance gating, and placement discipline (why-not-what,
narrowest namespace) live in agent convention plus edit-then-approve review.
That is the accepted mechanism, not a stopgap. The staged exact-duplicate
warn (`content_hash`) is the only planned code-side check. **Evidence
door:** review burden or retrieval quality demonstrably suffering reopens
the question of mechanical write-time checks.

### Transport — an owned boundary

Plain HTTP with plaintext operator-provisioned bearer keys on the LAN is the
accepted day-1 posture (vault owns secrets; TLS/Traefik is a later,
purely infra-side add-on). Recorded here so it reads as a decision, not a
gap.

## Candidate horizons — written down, deliberately uncommitted

The operator does not yet know which of these the mature system includes,
and refuses to fake certainty. Each is a *candidate* with its known evidence
door; the post-v1.0.0 roadmap effort decides, with dogfooding evidence in
hand. None of this is promised; none of it is ruled out.

- **Vector lane** (pgvector, local model) — door: dogfooding miss-tallies
  show semantic misses hurting. Already the spec's documented seam.
- **Nightly reconciliation worker** — first LLM worker; reads the day's
  traces and source diffs, emits *proposals* only. Door: a consumer whose
  long-horizon needs justify it (the planned Phase 3 shape).
- **Hot-tier context bundle** (`get_project_context`) — door: session-start
  retrieval becoming repetitive enough to measure (draft shape preserved in
  deferred-notes §1.3).
- **Export boundary + wiki projection** (`memctl export`; Markdown first,
  external engines evaluated over exports, never as owners) — door: a real
  read-side consumer for exported memory.
- **Source-turn decoration; purpose-style prior** — corpus-suggested,
  cheap once provenance exists; doors unset. Roadmap-effort material.

Ruled out of the destination outright (unchanged from the standing
rejections): a second datastore, harness-owned memory logic, agent-facing
approval, auto-injection, and RLS absent its tripwires (above).

## One-line test

When a future decision is unclear, ask: *does this keep the store something
one human can trust because one human gated it, and would we know from
evidence — not vibes — that it stopped working?* If yes to both, it fits.
