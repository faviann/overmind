# North-star distillation — the article corpus vs. the Phase 1 design

> Asset for wayfinder ticket [#12](https://github.com/faviann/overmind/issues/12)
> (map: [#11](https://github.com/faviann/overmind/issues/11)). Distills the 14
> articles in `docs/articles/` (read in publishing order) plus
> `agent-memory-handoff-v4.md` and `deferred-knowledge-and-dispatcher-notes.md`
> into the claims a *mature* agentic memory system must satisfy, and tags each
> against the Phase 1 spec (`memory-server-phase1-spec.md` v1.2) and the
> Session 2 plan. Distillation only — no new design. Input to ticket #13
> (write the north-star doc) and #14 (audit Session 2 scope).

**Tags.** `EMBODIED` — Phase 1 (incl. Session 2 scope) already builds or
structurally guarantees it. `DEFERRED` — consciously staged, with the
activation trigger named. `TENSION` — the corpus's claim and the current
design genuinely pull apart; the north star must take a position.
`NOT ADOPTED` — seen and consciously not pursued (or not yet considered).

---

## The corpus in one paragraph

The articles argue that a mature agent-memory system is: a **self-hosted,
inspectable substrate** (deployment shape chosen before paradigm, A3) whose
**canonical truth is an append-only ledger** with everything else a disposable
projection (A2's paradigm-collision resolution); **provenance travels with
every fact at six levels** and is nearly impossible to retrofit (A5);
**work is paid at write time in a known adoption order** (A4); retrieval is
**hybrid, RRF-fused at k=60, per-lane scores preserved** (A6); storage is
**tiered by access pattern, distinct from governance typology, with demotion
mandatory** (A7); the context window is **a budget managed by two-step
retrieval and previews, never a buffer to fill** (A8, A10–A12); memory reaches
the model as **tools the agent calls, never middleware injection**, with
self-guiding responses (A9); constraints are **enforced by the substrate, not
by prompts — ultimately by the storage engine itself** (A13); and every
threshold ships in **shadow mode first, calibrated from real traffic** (A13).

## Claim-by-claim

### 1. Flat vector RAG alone is not enough (A1)
**EMBODIED.** The founding assumption of handoff-v4. Phase 1 ships FTS +
recency + optional trigram; there is no naive embed-and-prepend path anywhere.
The vector lane is a documented seam, not a default.

### 2. Pick the paradigm by question shape; name primary vs secondary where commitments collide (A2)
**EMBODIED.** The trace-vs-compression collision is resolved explicitly:
raw trace primary and immutable, all summaries derived and pointing back.
Retrieval routing ("which store answers which question shape") is a named
component. The five use cases in handoff-v4 are exactly A2's
question-shape-first method.

### 3. Deployment shape before paradigm; self-hosted with a managed-migration escape hatch (A3)
**EMBODIED.** Self-hosted .NET/Postgres MCP server on owned hardware,
mem9-style ("remote is a base-URL and credential change, not a re-platform").
Session 2's HTTP + bearer keys completes the shape. Trust-boundary and
cost-cap arguments from A3 are the stated rationale.
*Note:* day-1 transport is plain HTTP on the LAN with plaintext key file —
a conscious, recorded trade (vault owns secrets; Traefik/TLS is a later
infra add-on). Worth restating in the north star as an accepted boundary,
not an oversight.

### 4. Pay at write time, in the proven adoption order (A4)
**EMBODIED (steps 1–2) / DEFERRED (steps 3–6, triggers named).**
- Provenance columns: v1 schema. ✔
- Type tagging: v1 (`type` CHECK, 10 values, convention-first). ✔
- Multi-step ingest: deferred to the first worker (Phase 3, nightly
  reconciliation; extraction provenance keys pre-reserved in `metadata`).
- Atomisation: deferred — currently a *convention* on proposing agents, no
  mechanical backstop. **No named trigger.** (Flagged for the north star:
  what event earns an atomisation gate — reviewer fatigue? retrieval-quality
  tallies?)
- Online dedup: staged order pre-decided (exact-dup **warn** at propose time
  → shadow-mode near-dup → acting worker). `content_hash` written and indexed
  from day one as substrate.
- Confidence scoring: deferred, shadow-mode-first discipline already adopted.

### 5. Write-time relevance gating: "if a constraint can be expressed in code, don't enforce it with words" (A4, A13/oh-my-kiro)
**EMBODIED for secrets / TENSION for relevance and placement.** The
never-store gate (reject for memories, redact-in-place for traces) is code,
not prompts — and the redact-vs-reject split is more nuanced than the corpus
asks for. But the *relevance* half of gating (polluted-blob defense) and the
placement discipline (why-not-what, narrowest namespace) live entirely in
agent-side convention. The code-side backstops are the human approval gate
and, later, dedup. The north star should say whether that's the endgame
(human gate = the relevance gate) or whether a mechanical relevance check is
ever earned.

### 6. Provenance at six levels, staged; Tier-3 read-time decoration; persist every computed signal (A5)
**EMBODIED (4 of 6 at v1) / DEFERRED (2, triggers named).**
Identity (uuid + version + content_hash), source, causal
(**server-side** `memory_consumed` logging — stronger than most corpus
systems, which trust the agent), and versioned (append-and-archive,
supersedes chain) are v1 schema. Capture confidence: deferred to shadow mode
(trigger: first worker / calibration need). Reciprocal: falls out of the
`source_id` index; the query exists (`memctl consumed`, acceptance test 2).
Read-time decoration is in the search result contract; "decoration polish" is
Session 2 scope. The four provenance acceptance questions (§10) are the
corpus's four debugging questions, verbatim.

### 7. Hybrid retrieval, RRF k=60, per-lane scores kept, lanes compose (A6)
**EMBODIED.** Spec §7 is a direct implementation: lane registry, fusion loop,
per-lane ranks and scores on every result, k=60. The trigram lane exists
precisely for A6's identifier failure mode. Vector lane deferred with an
explicit evidence trigger (dogfooding miss tallies), which is *more*
disciplined than the corpus norm.

### 8. Tiering ≠ typology; you need both; demotion is mandatory or the store rots (A7)
**EMBODIED (typology) / DEFERRED (tiering mechanics) / mild TENSION (demotion).**
Typology (status: proposed→approved→superseded→retired; visibility
private/shared) is fully built and grant-enforced. Tier columns exist from
day one; mechanics are Do-Not-Build. Soft demotion exists only as the recency
lane's exponential decay. A7 says "a system with promotion but no demotion
path rots" — Phase 1 has *neither*, deliberately. The trigger for tiering
mechanics is vague ("staged in against acceptance tests"). The north star
should name the rot signal that activates write-time tier classification and
freshness-state demotion, the same way the vector lane has its miss-tally
trigger. The hot-tier `get_project_context` bundle (draft shape preserved in
deferred-notes §1.3) belongs to the same decision.

### 9. Context is a budget: two-step retrieval, previews, operator-controlled volume (A8, A10–A12)
**EMBODIED.** Two-step is the default rhythm (search → previews + provenance;
`get_by_id` for full content, auto-logging consumption). `retrieval_config`
gives per-(agent, namespace) `max_results` / `preview_chars` — exactly A8's
"explicit parameters, not hardcoded defaults." Compaction-alone is explicitly
rejected; trace-first compaction (`trace_snapshots` before summarize-then-
delete) is the landing pad, harness integration deferred.

### 10. Memory is a tool surface the agent drives; never auto-injection; self-guiding responses (A9)
**EMBODIED.** The whole tool contract: 8-tool surface, no injection path
exists at all, every response ends with a `next` hint, complexity held inside
the engine, split read/write paths preferred for human-readable artifacts.
The harness-as-client rule (extensions are thin clients, memory logic never
in the harness) is the A12 harness/substrate complementarity, adopted as a
hard rule.

### 11. The harness matters more than the model; the substrate must be harness-agnostic (A10–A12)
**EMBODIED as a boundary.** The substrate deliberately does *not* try to
solve context engineering — it exposes the primitives (two-step, previews,
handoff summaries with refs, trace snapshots) that a good harness needs.
Harness extensions are Do-Not-Build in Phase 1 with named landing pads.

### 12. The application layer is not a trustworthy isolation boundary; the storage engine is the only authority that holds (A13)
**EMBODIED (append-only, single door) / TENSION (namespace isolation).**
The strongest corpus claim, and the most interesting gap:
- *Grant-level enforcement:* append-only traces via grants AND trigger; no
  DELETE granted anywhere; one login role; consumers never see a connection
  string. This is genuine storage-engine authority. ✔
- *But namespace isolation and private-note visibility are enforced in
  `MemoryService` (application code)* — and Session 2's per-request-context
  refactor deepens that (allowlist checks move *into* the service layer).
  There is no Postgres RLS; a bug in the service layer could cross
  namespaces or leak private notes silently.
- Mitigations that distinguish this from A13's attack shape: agents never
  hold SQL (the corpus's prompt-injection-to-SQL vector doesn't exist here);
  the surface is a fixed tool contract; there is exactly one door.
- **North-star question:** is app-enforced isolation behind a single door the
  accepted endgame, or does multi-tenant growth (more agents, less-trusted
  keys, a future raw-SQL escape hatch — spec §8 leaves one open "later, read
  only, if ever") eventually earn Postgres RLS keyed on a per-request
  setting? A13 predicts storage-layer authorization universalizes first of
  all emerging patterns. If the raw-SQL escape hatch is ever built, RLS stops
  being optional — the corpus is unambiguous there.

### 13. Ship thresholds in shadow mode; calibrate from real traffic (A13/mem9)
**EMBODIED as discipline.** Pre-decided for dedup similarity and confidence
scoring. Depends on real traffic existing — see claim 15.

### 14. Source-turn decoration: ground retrieved facts in their originating dialogue, budget-capped (A13)
**PARTIALLY / NOT ADOPTED.** `get_by_id` returns the full provenance chain
and the `next` hint points at `retrieve_trace` — grounding is *reachable* in
one hop, agent-driven (consistent with claim 10). Automatic decoration with
originating turns under a budget triple is not designed anywhere. A13 rates
it "two days' work once provenance + hybrid retrieval exist" and predicts it
universalizes second. Candidate for the post-v1.0.0 roadmap, not Phase 1.

### 15. Decisions are earned by dogfooding evidence, not intuition
**EMBODIED as method / structurally pending.** The whole trigger discipline
(vector lane, trigram completion, event_date, dedup thresholds, Phase 3
watch-list) presumes real usage generating tallies. Nothing generates
evidence until consumers actually run against the server. This is the
strongest corpus-side argument that **time-to-real-usage is the metric
Session 2 should be optimized for** — every week without dogfooding delays
every evidence-gated decision behind it. (Directly relevant to #14's
"too much / too little" question.)

### 16. purpose.md — a standing directional prior inlined into every LLM call (A13)
**NOT ADOPTED (nearest analog exists).** No single user-intent file
conditions system behavior. Namespace descriptions and `retrieval_config`
rows are the nearest structural slots. Only becomes meaningful when
LLM workers arrive (Phase 3) — a worker's extraction prompt is where a
purpose prior would plug in. Note for the roadmap effort, not for Phase 1.

### 17. AGENTS.md as contract, mechanically enforced (A13)
**PARTIALLY EMBODIED.** The repo treats several AGENTS.md clauses as
mechanically checked (append-only asserted by tests AND grants; test-diff
gate in workflow; `--by` required). Others remain hints. Low stakes; worth a
line in the north star, not a work item.

## What the corpus does not cover (homegrown ground)

Named so the north star doesn't mistake them for corpus-validated or
corpus-contradicted — they stand on this project's own reasoning:

- **Proposal→approval as the shared-memory gate with a human operator**, incl.
  edit-then-approve and the review event convention. The corpus has
  provenance and governance typology but no worked human-approval loop;
  approval-fatigue calibration (deferred-notes §1.1) is this project's own
  forward answer.
- **Workstreams / checkout / handoff** as coordination primitives. The corpus
  treats handoff as summary-plus-refs (adopted, claim 9-adjacent) but has no
  checkout/conflict model. Session 2 tools 6–8 are homegrown.
- **The dispatcher corpus** (deferred-notes §2) — predecessor-project
  convergence, not article-derived. Sequenced last; out of scope for the
  current map.

## Tensions and open questions, ranked (input to #13 and #14)

1. **Namespace isolation is app-enforced, not storage-enforced** (claim 12).
   The north star must take an explicit position; the audit (#14) should
   check that Session 2's per-request context refactor at least *centralizes*
   the checks so RLS can be added behind the same seam later.
2. **No demotion path and no named tiering trigger** (claim 8). Decide the
   rot signal now, build later — same pattern as the vector lane.
3. **Time-to-dogfooding is the binding constraint on every evidence-gated
   decision** (claim 15). Bears directly on whether Session 2 is "too much
   before usage."
4. **Atomisation has no trigger** (claim 4). Name one or explicitly accept
   convention-plus-review as sufficient.
5. **Relevance gating lives in prompts** (claim 5). Accept the human approval
   gate as the mechanical backstop, or name what earns a code-side gate.
6. **Plain HTTP + plaintext keys on the LAN** (claim 3). Fine as a recorded
   boundary; the north star should own it explicitly.
7. **Source-turn decoration and purpose.md** (claims 14, 16). Cheap,
   high-leverage, post-v1.0.0 — roadmap fodder, kept out of Phase 1 scope.
