# Agent Handoff: Memory/Control Substrate Project (v4)
 
> **v4 changes:** integrated the salvage review of the retired predecessor docs (`memory-substrate-research.md`, `overmind-future-plan.md`, `memory-ledger-principles.md`, `harness-seam.md` — all now deleted; anything pertinent lives here or in `deferred-knowledge-and-dispatcher-notes.md`). New in v4: the **projection principle**, the **review event convention**, the **memory placement discipline**, **edit-then-approve** in the operator lifecycle, redact-vs-reject split on the never-store gate, seeding discipline, and small schema additions (metadata, content_hash, three new types). Companion docs: `memory-server-phase1-spec.md` v1.1 (**wins over this document on build details**), `ansible-integration-checklist.md`, and `deferred-knowledge-and-dispatcher-notes.md` (Phase 3+ material and explicitly rejected ideas).
>
> **v3 changes (retained):** committed stack (.NET + PostgreSQL on existing LXC), private/shared write-policy split, harness directive (Pi), build sequencing, workstream coordination.
 
## What the project is really about
 
This is **not primarily "build another chatbot memory feature."** It is a **memory/control substrate** for agents:
 
what was said, what was decided, what was merely proposed, what was approved, what source produced a fact, what agent used it, what changed later, and how to replay/debug the chain.
 
I'm not against assembling multiple EXISTING solutions together, but I'm also open to building my own version.
 
A core assumption is that **flat vector RAG alone is not enough**. Every serious system in the research corpus that started there added something on top: hybrid retrieval, provenance, write-time synthesis, graphs, traces, wikis, tiered memory, filesystem-native stores. Agent memory must support durable memory beyond the context window, and "embed everything, retrieve top-k, prepend to prompt" is not sufficient.
 
A second core assumption: **the memory substrate and the agent harness are complementary systems.** Memory decides what is durably known. The harness (compaction policy, tool-result clearing, subagent context firewalls) decides what occupies the context window on a given turn. This project is the memory substrate, but its tool surface must be designed knowing the harness exists — the handoff/replay use case below is essentially the subagent-summary pattern: the receiving agent gets a summary in context, and the full trace stays retrievable by reference.
 
A third framing, adopted in v4 (converged on independently by the predecessor project): **this is a memory ledger with projections, not a monolithic memory product.** The canonical truth is `trace ledger + proposal ledger + approved memory ledger`. Everything else — the FTS index, a future vector index, Markdown/wiki exports, graph views, external memory tools — is a **derived projection** that must be rebuildable from the canonical ledger. When evaluating external systems (Hindsight, Graphify, LLM-Wiki, OpenKB, …), the question is never *"which one should own our memory?"* but *"which one is useful as a projection/index/query engine over our canonical substrate?"* The `memctl export` boundary (Phase 2 seam) is what makes this operational rather than aspirational.
 
## The two paradigm commitments (read this before designing anything)
 
The architecture combines **trace-as-memory** with **progressive compression**, and these two paradigms have colliding commitments. Trace says the verbatim record is the artifact; compressing it destroys it. Compression says only the dense distillation should persist. Both cannot be primary. The resolution for this project is explicit:
 
1. **The raw trace store is primary and immutable.** Nothing compacts, rewrites, or garbage-collects it. Replayability and hallucination attribution depend on this.
2. **All summaries, extracted facts, and compacted views are derived artifacts.** They point back into the trace via provenance and never replace trace data.
3. **Retrieval routing decides which store answers which question shape.** "What did the agent do last Tuesday" goes to the trace. "What are this project's tech decisions" goes to the approved memory store. "What's the current state of the auth refactor" goes to the project namespace. A query router (even a simple one) is a first-class component, not an afterthought.
Separately: **governance typology and access-pattern tiering are different axes, and this system needs both.**
 
- **Typology (governance):** raw trace → proposed → approved. This answers *who can mutate this, under what conditions.* The proposal/approval distinction is central and non-negotiable **for shared memory**. Agent-**private** notes (`visibility='private'`) are direct-write, auto-approved, and owner-scoped — still traced, still provenance-carrying, never visible to other agents. Only **shared** durable memory is proposal-gated, and approval is an **operator action** (`memctl approve`), never an agent-facing tool. The operator lifecycle is **approve / edit-then-approve / reject / supersede** — the edit path exists because the realistic common case is a proposal that is 90% right with wrong wording, and reject-and-lose-it wastes both the fact and the calibration signal. Edits are recorded (original content preserved on the proposal, amended flag + reviewer on the approval).
- **Tiering (access patterns):** hot / warm / cold. This answers *how often is this needed, and what representation serves that frequency.*
  - **Hot:** what an agent needs every turn — project profile, active task state, standing constraints and preferences. Small, compact, cheap to load, possibly its own endpoint/path with its own latency budget.
  - **Warm:** recent decisions, open questions, episodic facts from recent sessions.
  - **Cold:** old traces, completed tasks, superseded facts, raw documents. Findable on demand, never burdening the default retrieval path.
Tier assignment happens at write time (extraction-time classification, supermemory-style, is the simplest starting mechanism; heat-based or consolidation-driven promotion can come later). **Demotion is required, not optional:** TTLs, recency decay in ranking, or freshness states. Demotion is not deletion — cold material moves to a cheaper, unqueried-by-default representation. A system with promotion but no demotion path rots.
 
## The practical use cases
 
### 1. Coding project continuity
 
"Continue work on my Blazor scheduling app" — and the agent knows project state, tech choices, prior decisions, constraints, pending tasks, codebase structure, and *why* choices were made. No re-explaining the project every session. For coding agents this needs repository-aware memory: files, decisions, issues, ADRs, traces, task state, and possibly structural/code-graph signals. "What breaks if I change this function's return signature" wants structural intelligence, not cosine similarity over code chunks.
 
### 2. Homelab / infra assistant
 
"Can you update Jellyfin?" "Why did we configure Authentik/Traefik this way?" "What changed last time this broke?" "Which server owns this service?" Memory over infra decisions, configs, deployment traces, rollback notes, credential boundaries, topology, and runbooks. Git/IaC/docs are source of truth, not conversational memory.
 
### 3. Agent handoff and replay
 
One agent hands off to another without losing context: Codex proposes a repo change, Claude reviews it, Hermes logs it, OpenClaw orchestrates the next task, a later agent inspects the trace. The point is not "remember the conclusion" — it is enough provenance to answer: Why did we do that? What evidence did the agent use? Was this approved or just proposed? Can we replay the steps? **What did the agent actually consume from memory?** (That last question is causal provenance — see below — and it is a schema requirement, not a nice-to-have.)
 
The `create_handoff` tool is specified as: a compact summary goes into the receiving agent's context; the full trace is retrievable by reference IDs included in the summary.
 
### 4. Personal ops assistant
 
"What should I work on today?" "Draft a reply and create a follow-up task." "Capture this Discord idea into a GitHub issue." Personal memory, project memory, repo memory, infra memory, and task logs must be **separated namespaces** — no polluted memory blob. The dispatcher/orchestrator design distilled from the predecessor project (ownership split, project registry, PRs-as-bridge, action-approval taxonomy) lives in `deferred-knowledge-and-dispatcher-notes.md` and is sequenced **last**.
 
### 5. Research assistant / document digestion
 
Building a useful internal map of long documents and repos: summaries, comparisons, extracted patterns, links back to sources. For long docs the right shape is hierarchical navigation / LLM-as-retriever / wiki-like synthesis, not just embeddings. Question shape drives paradigm: coding wants structure, research wants navigation, long-term personal context wants compression, observability wants trace.
 
## Provenance: the concrete spec, not a slogan
 
"Provenance-first" means specific columns, staged deliberately. Six orthogonal levels:
 
| Level | Answers | Status |
|---|---|---|
| **Identity** | Which fact, exactly? (stable UUID + version counter + content_hash) | **v1 required** |
| **Source** | Where did it come from? (source type + ID, session, agent, capture timestamp) | **v1 required** |
| **Causal** | Which agent step retrieved and *used* this? (observation log of consumed facts) | **v1 required** — this is what makes replay and hallucination attribution possible, and it is nearly impossible to retrofit |
| **Versioned** | What did we believe before? (append-and-archive, never destructive overwrite) | **v1 required** |
| **Capture confidence** | How sure were we at write time? (CONFIRMED / LIKELY / AMBIGUOUS or a float) | v1.5 — ship in **shadow mode** first: record scores without acting on them, calibrate thresholds from real traffic, act later |
| **Reciprocal** | What else shares this origin? ("show me everything from that conversation") | Falls out of Source if source IDs are indexed; make sure the query exists |
 
Target maturity: **Tier 3 — read-time decoration.** Every retrieved fact arrives with source type, confidence, freshness state, and version info already attached, kept concise (enums and floats, not provenance trees inline). One rule from the corpus that costs nothing and prevents a known failure: **if a signal is computed at write time, persist it.** Never compute a confidence or a lane score and then discard it before the row is written. (`content_hash` and the `metadata` escape hatch exist for exactly this reason.)
 
**Provenance of the approval itself (v4, from the predecessor's review event convention).** The approval/rejection trace event must not reuse the *proposing* session or agent identity — that would make the review look like stronger provenance than it is. The source event says what produced the candidate; the review event says who accepted or rejected it. Review events carry a synthetic session (`review:<proposal_uuid>`), a reviewer identity (`human:<name>` — required, never anonymous, never the proposing agent), and refs linking proposal, source trace, and (on approval) the resulting memory. Corollary: **no anonymous approvals** — reviewer identity is mandatory on the operator CLI.
 
**Seeding discipline (v4).** A memory created without a source event is architectural debt. Even hand-seeded memories (the checklist's "interview your own project" evening) must point at the traces of the session that produced them: `source_type='trace'`, `source_id=<trace_uuid>` — never a bare `source_type='human'` with a null source.
 
**Acceptance tests.** The system is not done until it can answer all four, with evidence:
 
1. "Why did you say that?" → trace from the response back to the consumed facts back to their sources.
2. "This source changed — re-validate everything derived from it." → source-ID cascade.
3. "These two facts contradict — adjudicate." → confidence, capture timestamps, and version history make the adjudication mechanical — and the review events show *who* approved each, distinctly from who proposed each.
4. "This was a hallucination — was it bad memory or model invention?" → the causal log shows whether the claim existed in consumed memory or not.
## Retrieval: the agent decides what it sees
 
The delivery mechanism is a commitment, not an implementation detail:
 
- **Memory is a tool surface the agent calls, not middleware that auto-injects.** No predicting what the agent needs and prepending it. The agent asks; the trace shows what it asked for; the cost reflects its choices; follow-up retrieval is always possible.
- **Two-step retrieval is the default rhythm.** `search_memory` returns identifiers + short previews (+ provenance decoration). `get_by_id` fetches full records only when the agent chooses. This is the single cheapest token-cost lever available (~450 tokens of previews vs ~15K tokens of full matches per recall; compounds to ~200K tokens saved across a long session) and requires no architectural change — it is purely an interface decision.
- **Hybrid retrieval with RRF at k=60.** Lexical (tsvector) + recency in v1; **vector is a staged lane** (pgvector in the same database, added when dogfooding tallies show semantic misses hurting — not before); graph/trace lanes as they arrive. Lanes compose without redesigning fusion. **Per-lane scores stay on the result struct** — one fused score for ranking, one score per lane for debugging and future reranking. Discarding lane scores after fusion is a known, avoidable mistake.
- **Recency is a first-class ranking lane** (exponential decay), which doubles as soft demotion. (Deferred refinement: an *event date* distinct from capture date — recency of the write is not recency of the evidence. Lives in `metadata` when the first stale-memory incident earns it; see the deferred-knowledge doc.)
- **Self-guiding tool responses.** Every tool result ends with a `Next:` hint — the most likely follow-up call, a related ID, a warning about what the result implies. One line per response; it is what makes an 8–10 tool surface navigable for agents that have never seen it, and it makes traces self-documenting.
- **Keep the tool surface small and hold complexity inside the engine.** The agent should not need to know about tiers, lanes, or consolidation; it calls `search_memory` and the routing happens behind it. A raw read-only SQL/query escape hatch alongside the convenience verbs is acceptable and useful.
Tool surface (agent-facing): `search_memory`, `get_by_id`, `get_project_context` (hot-tier pull, staged), `propose_memory`, `save_note` (private direct-write), `log_trace`, `retrieve_trace`, `create_handoff`, `list_workstreams` / `checkout_workstream` / `checkin_workstream` (parallel-session coordination — agents check inflight work before starting). **Approve/edit/reject are operator CLI commands, deliberately not tools.** Write-path note: prefer split read/write where the agent's native tooling can own mutation of human-readable artifacts (Markdown/ADRs edited as reviewable patches), while the memory engine owns discovery and resolution.
 
## Update semantics: the position, not just the problem
 
Contradictions, staleness, and superseded facts are the known hard problem. The default policy here, not left to improvisation:
 
- **Append-and-archive, never destructive overwrite.** New versions point at old ones; old beliefs remain queryable ("what did we believe on Tuesday").
- **Consolidation captures evolution, not just latest state.** "User preferred React" + "user switched to Vue" becomes a refined observation recording the journey, with both source IDs attached — not an overwrite.
- **Superseded ≠ deleted.** Superseded facts get a freshness/superseded state, are down-weighted or filtered at retrieval, and demote to cold. They stay reachable for audit.
- **Source-of-truth hierarchy resolves conflicts:** Git/IaC > approved memory > proposed memory > raw trace inference. When an authoritative source changes, the source-ID cascade flags derived facts for re-validation rather than silently coexisting with them.
- **Human edits and LLM rewrites take different write paths** (in-place mutation vs append-and-archive respectively), because they carry different trust. Operator edit-at-approval is the human path: the proposal's original content is preserved, the amended content becomes the approved memory, and the review event records both the reviewer and the fact of amendment.
## Write-time investment: ordered, not a grab-bag
 
Facts are written once and read orders of magnitude more often; work paid at write time amortizes across every read. But write-time work runs **behind the response, not in front of it** — extraction may be synchronous, consolidation is a background/async pass. Adoption order, chosen so each step pays back immediately and enables the next:
 
1. **Provenance columns** (cheapest, prerequisite for everything)
2. **Type tagging** (decision / fact / preference / task / adr / runbook / note / **constraint / open_question / warning** — convention-only is fine at first; unlocks faceted retrieval immediately)
3. **Multi-step ingest** (deterministic scripts where possible, LLM judgment where necessary)
4. **Atomisation** (smallest individually retrievable propositions, not wall-of-text chunks)
5. **Online dedup-and-synthesis** (batch LLM call emitting add/update/delete/no-op per fact; most invasive, so it comes after the store has structure — `content_hash` written from day one is its substrate)
6. **Confidence scoring** (shadow mode first, calibrate from data, act later)
**Write-time relevance gating** belongs in this pipeline too: material that doesn't clear a relevance threshold never enters memory at all. This is the cheapest defense against the polluted-blob failure mode and where the "must never be stored" governance rules (credentials, secrets, ephemeral noise) are enforced — in code at the ingest gate, not in prompt instructions. If a constraint can be expressed in code, it should not be enforced with words. **v4 refinement — redact vs reject:** for *memory* writes the gate **rejects**; for *trace* writes it **redacts** the matched span in place (`[REDACTED:<rule>]`) and still records the event, because a tool result that happens to contain a token still describes something that happened, and the acceptance tests are trace joins. Dropping the event silently would trade a secrets risk for an audit hole.
 
**Memory placement discipline (v4, write-side companion to namespaces).** Before proposing, an agent asks two questions: *does this belong in memory at all,* and *in which namespace?*
 
- Propose the **why**, not the **what**. The *what* (config values, file contents, variable mappings) lives in the repo — Git is its source of truth and memory would only rot against it. The *why* (rationale, constraint, pitfall, rollback note) is what memory is for.
- Pick the **narrowest namespace** that will need the fact. Routing-level facts ("Jellyfin is managed by homelab-infra") belong in a global/routing namespace; implementation rationale ("Jellyfin needs Intel iGPU passthrough because X") belongs in the project namespace; implementation detail ("the compose template maps /dev/dri via variable X in file Y") belongs in the repo, **not in memory**.
## Important architectural preferences (constraints, not tastes)
 
- **Self-hosting and inspectability.** Privacy/cost/control sensitive. Committed deployment shape: **self-hosted server exposing an MCP/API surface**, mem9-style — engineered so that a managed or remote option is a base-URL and credential change, not a re-platform. Managed APIs (Supermemory et al.) are references and possible components, not the substrate. **Committed stack: a .NET server (official MCP C# SDK) over PostgreSQL on the existing database LXC.** One datastore, period — no ClickHouse or second store; trace↔memory joins are load-bearing (acceptance tests 1 and 4 *are* joins), and if trace volume ever hurts, the answer is partitioning in place. The server is the **only door**: exactly one app role holds DB credentials, consumers get bearer keys and never connection strings, and append-only/no-delete are enforced at the grant level, not just in application code.
- **Derived projections are disposable.** Any index or export must be rebuildable from the canonical ledger; nothing derived is ever the only place truth exists. This is the acceptance criterion for the future vector lane (embeddings rebuildable from `memories` via the jobs outbox) and for the wiki layer.
- **Token cost matters.** Two-step retrieval, previews, compact hot-tier profiles, write-time synthesis, and durable docs all serve this. The context window is a resource to manage, not a buffer to fill.
- **Practical beats fancy.** Boring, maintainable, inspectable pieces that compound. No full-time-maintenance science project.
- **Memory must be debuggable.** A fact without origin is dangerous — and an approval without a reviewer is a fact without origin.
- **Memory must resist rot.** Demotion paths, freshness states, supersession handling, and dedup are all rot defenses — required, not optional.
- **Source of truth must be explicit.** Git, GitHub Issues, Markdown docs, DB rows, raw traces, approved memory — the system knows which is authoritative for what, and conflict resolution follows that hierarchy.
## Harness directive
 
Harness: **Pi (pi.dev)** as the frame — chosen for its extension surface (dynamic per-turn context injection, replaceable compaction, skills with progressive disclosure, RPC/SDK modes) and OpenClaw compatibility. Rules:
 
- **Memory logic never lives in harness extensions.** Extensions are thin clients (~100 lines) against the memory server's MCP/API surface: a context extension (hot-tier pull per turn), a trace extension (session events → `log_trace`), a compaction replacement, and tool registrations. This keeps the substrate harness-agnostic — Codex, Claude Code, and Hermes hit the same surface without Pi. (The predecessor project's `harness-seam.md` reached the identical position independently: harnesses are clients and producers of events, never owners of memory truth.)
- **Default compaction is replaced with trace-first compaction:** write the full pre-compaction state to `trace_snapshots`, then summarize, then delete. Never the reverse order.
- **Governance is server-side; sandboxing is harness-side.** Pi ships no permission system by design — containerize/sandbox it for execution boundaries, but never rely on the harness for memory governance.
- The harness is a **versioned, swappable dependency, not the platform.** Evaluate the oh-my-pi fork before hand-building subagent orchestration.
- The dispatcher (use case 4's router) is primarily a harness/orchestration project with thin memory needs (workstreams, handoffs, hot task state) — it comes **last** in the sequencing below. Its design notes live in `deferred-knowledge-and-dispatcher-notes.md`.
## Target architecture
 
1. **Append-only raw trace store** — conversations, tool calls, agent actions, *retrieved-and-consumed memories* (causal provenance), outputs, approvals (with reviewer identity). Immutable. Primary for "what happened" questions.
2. **Proposal queue** — candidate memories/ADRs/tasks generated by agents, not yet trusted.
3. **Approved durable memory store** — atomic, typed, provenance-carrying, versioned facts. Tiered hot/warm/cold with write-time tier assignment and demotion.
4. **Project/repo memory namespaces** — per project: decisions, architecture, task state, codebase summaries, handoffs. Namespace isolation is enforced by the substrate.
5. **Filesystem/wiki layer** — human-readable Markdown/ADRs; agents update via reviewable patches; files win over derived indexes on conflict.
6. **Hybrid retrieval layer** — lexical + vector + recency (+ graph/trace later), RRF k=60, per-lane scores preserved, two-step interface, query routing across stores.
7. **Write-time workers** — the ordered pipeline above, async where possible, with relevance gating at the front.
8. **Agent-facing MCP/API tools** — small surface, self-guiding responses, split read/write paths.
9. **Governance rules in code** — what auto-writes, what needs approval, what is never stored (reject for memories, redact for traces), which source wins.
10. **Projection/export boundary** — `memctl export` (JSONL/Markdown) as the documented seam through which external memory systems, wikis, and future indexes consume the canonical ledger. Phase 2.
### The v1 slice (build this first)
 
The authoritative version of this slice is **`memory-server-phase1-spec.md` v1.1**, including its binding **Do Not Build** list. Summary:
 
- Trace store with **server-side causal logging** — `get_by_id` auto-logs `memory_consumed`; the audit trail never depends on agent cooperation (**1**)
- Approved store with identity/source/version provenance (incl. `content_hash` and `metadata`), type tags (incl. `constraint`/`open_question`/`warning`), and the **private/shared visibility split**; tier columns exist from day one, tiering mechanics do not (**3**)
- Two-step hybrid retrieval: **tsvector + recency**, RRF k=60, per-lane scores kept. **No vector lane in v1** — pgvector is the documented seam, earned by dogfooding tallies (**6**)
- Tool surface: `log_trace`, `search_memory`, `get_by_id`, `propose_memory`, `save_note`, plus workstream checkout/checkin and `create_handoff`. Approval is `memctl` (approve / approve --edit / reject, reviewer identity required), not a tool (**8**)
- Never-store gate in code at every write path — reject for memories, redact for traces; an **empty `jobs` outbox** so writes are queue-shaped before any worker exists (**7**, **9** partial)
- Review events follow the **review event convention** (synthetic review session, reviewer identity, proposal + source + memory refs)
Everything else — consolidation workers, confidence, graph lanes, wiki patch flow, hot-tier endpoint, export boundary — stages in against the acceptance tests, in the write-time adoption order. Each stage must leave the system shippable.
 
### Build sequencing
 
1. **Memory server** — own repo, .NET, the Phase 1 spec. It is its own **zeroth consumer**: build decisions are logged into namespace `memory-system` from session one (the recursive self-improvement loop is seeded, not built).
2. **Homelab/ansible project as first consumer** — integration only, zero new product (`ansible-integration-checklist.md`). Dogfood 2–3 weeks; the checklist's watch-list *is* the Phase 3 requirements document.
3. **Personal assistant** — own repo, second consumer. Its long-horizon needs justify the first consolidation work. First worker overall: the **nightly reconciliation job** (diff sources + day's traces → proposals, cheap model, never direct writes).
4. **Dispatcher/orchestrator last** — meaningless until there are ≥2 consumers to route between. Design corpus for it: `deferred-knowledge-and-dispatcher-notes.md`.
## Things another agent should not misunderstand
 
- **"He wants a vector database for agent memory."** Too shallow. He wants a **memory lifecycle system**: capture → trace → extract → propose → approve (or edit-then-approve) → store → retrieve → cite → update → retire.
- **"Pick the best existing memory repo and install it."** Existing repos are references, possible components, and candidate **projections over the canonical ledger** — never candidate owners of it. The design is driven by the actual use cases: self-hosted multi-agent development, infra ops, personal continuity, replayability, provenance, cost control.
- **"Just let agents write whatever they infer into memory."** No. Proposal/approval is central. Governance lives in code.
- **"Compact the trace to save space."** Never. The trace is immutable; summaries are derived views that point back into it.
- **"Retrieval quality is the hard part."** Retrieval quality matters, but the delivery mechanism (agent-driven tools, two-step) and the write-time discipline matter as much or more. Do not build auto-injection.
- **"Provenance is metadata we can add later."** Causal provenance and versioning are nearly impossible to retrofit. They are v1 schema.
- **"Approvals are just a status flip."** An approval is itself a provenance-carrying event with its own actor. Never let a review event borrow the proposing agent's identity, and never record an anonymous approval.
- **"Seed memories don't need sources."** They do. A memory without a source event is architectural debt from day one.
- **"Store the config values so the agent doesn't have to read the repo."** No. Propose the why, not the what; the what lives in the repo and memory would rot against it.
- **"Just hand this consumer a Postgres connection string."** Never, no matter how convenient. The server is the only door; a direct connection bypasses the never-store gate, server-side causal logging, namespace isolation, and the write policy — silently. Bearer keys at the server are the governance boundary.
## North star
 
**Build a durable, inspectable, self-hosted memory substrate that lets agents become useful long-term collaborators — without pretending that context-window summaries or flat vector RAG are enough.**
 
Priorities, in one line: provenance-first (all six levels, staged, including provenance of approvals), approval before **shared** durable memory (private notes direct-write; edit-then-approve supported), immutable trace with derived compression, typology *and* tiering, agent-driven two-step retrieval with per-lane debuggability, write-time investment in the proven order with placement discipline, single-door single-datastore substrate with a projection/export boundary, explicit source-of-truth boundaries, namespace separation, token-cost discipline, and practical maintainability.
 
If another agent joins the project: **do not start by choosing a vector DB. Start by preserving the memory lifecycle, the trace-is-primary commitment, and the source-of-truth boundaries — and check every design decision against the four provenance acceptance tests.**
