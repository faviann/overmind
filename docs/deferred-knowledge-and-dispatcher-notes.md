# Deferred Knowledge & Dispatcher Notes
 
> **Provenance:** salvaged 2026-07 from four retired predecessor documents — `memory-substrate-research.md`, `overmind-future-plan.md`, `memory-ledger-principles.md`, `harness-seam.md` — which are now deleted. Everything build-relevant *now* was merged into `agent-memory-handoff-v4.md` and `memory-server-phase1-spec.md` v1.1. This document holds what is pertinent **later**: deferred conventions, the dispatcher design corpus (build phase 4), and ideas that were considered and deliberately rejected, recorded so they are not re-litigated.
>
> Also notable: the predecessor project independently converged on the same core architecture (append-only trace as truth, proposal→approval, provenance-mandatory, Postgres-first, async write-time work, harness-as-client). Independent convergence is the strongest validation signal the research corpus recognizes. The current design is not a first draft; it is a second arrival at the same place.
 
---
 
## 1. Deferred conventions and schema ideas (pre-decided, not built)
 
These have landing spots already reserved (`metadata` JSONB, forward seams in spec §13). They activate on the trigger named, not before.
 
### 1.1 Extraction provenance
When the first LLM worker exists (nightly reconciliation, Phase 3), every proposal it emits records in `metadata`:
```json
{ "extraction_model": "…", "extraction_prompt_version": "…" }
```
Trigger: first worker ships. Purpose: calibrating auto-approval policy per model/prompt version; the approval-fatigue and edited-proposal watch-list tallies are meaningless without knowing which extractor produced each proposal.
 
### 1.2 Event date vs capture date
Recency of the write is not recency of the evidence. A memory captured today may describe a state from last month; the recency lane currently ranks by `created_at` only.
```json
{ "event_date": "2026-06-12" }
```
Trigger: the first stale-memory-retrieved-as-true incident on the watch list. Fix: `event_date` in `metadata` feeding the recency lane (fall back to `created_at` when absent). No migration.
 
### 1.3 Context bundle shape (`get_project_context`, hot tier)
When the hot-tier endpoint gets built, the predecessor's concrete draft of the bundle is a good starting spec:
```text
namespace: <ns>
task: <active workstream title, if checked out>
retrieved:
  - N approved facts/constraints (hot tier)
  - M recent decisions
  - K open workstreams / recent handoffs
  - provenance IDs on everything (uuid + source_id)
```
The point: the bundle is "the compiled current state," small and citable — not "the entire past." Every item carries its uuid so the agent can `get_by_id` / `retrieve_trace` deeper on demand (two-step rhythm preserved even in the hot path).
 
### 1.4 Richer tool-call trace fields
Beyond the v1.1 conventions (model/provider, repo/branch/cwd, duration_ms/exit_code), the predecessor schema suggests optional content keys worth adopting *if* debugging demands them: `files_read[]`, `files_written[]`, `commands_executed[]`, `side_effects_declared`, token/cost counters. All fit in the existing JSONB content — adopt per-key when a real debugging session misses them, not preemptively.
 
### 1.5 Compaction boundary enrichment
`trace_snapshots` exists. When harness compaction integration arrives, the snapshot's `summary` write should also record `model` and `prompt_version` used for summarization, plus a checksum of the source range — so a bad summary can be attributed to the summarizer version and the source verified intact. Fields fit in the snapshot JSONB.
 
### 1.6 Dedup activation order
`content_hash` is written and indexed from Phase 1. First consumer: exact-duplicate **warn** (not block) at propose time. Second: near-duplicate detection in **shadow mode** (record similarity scores in `metadata`, act on nothing) until the score distribution against real traffic picks the threshold. Only then does the online dedup-and-synthesis worker act. This is the corpus's shadow-mode discipline applied locally.
 
### 1.7 Projection evaluation plan
Once `memctl export` exists (spec §13), external systems get evaluated in this order, each against the single question *"is this useful as a projection/index/query engine over our canonical ledger?"* — never *"should this own our memory?"*:
- **Markdown/wiki export** first (cheapest, human-readable, tests the export boundary itself)
- **Graphify / Understand-Anything** per-repo, for structural "what breaks if…" questions (graph as derivative cache; the file/DB wins on conflict)
- **Hindsight / LLM-Wiki / OpenKB** as candidate read-side engines over exported approved memory
- Watch: **Acontext** (skills-as-memory — approved, reusable agent knowledge as editable skill files; overlaps with the reviewable-artifact write path)
A failure of any projection is disposable by construction: rebuild from the ledger.
 
---
 
## 2. Dispatcher / orchestrator design corpus (build phase 4 — the "Overmind" plan, distilled)
 
The predecessor project designed the personal orchestration layer (use case 4) in detail. The memory server makes almost none of this; the dispatcher consumes the memory server. Sequenced **last** — meaningless until ≥2 consumers exist. Everything below is design input, not commitment.
 
### 2.1 Core principle
> **The dispatcher tracks promises. Projects track work. PRs track change. Docs track knowledge. Deployments track reality.**
> Shorter: **the dispatcher knows the map, project agents know the roads, the repo is the law.**
 
Two failure modes it exists to avoid: (a) one bloated main brain that knows every implementation detail; (b) many project agents working independently with no shared visibility or accountability.
 
### 2.2 Ownership split
- **Dispatcher owns:** intent capture, routing, prioritization, cross-project awareness, approval boundaries, outcome tracking. It should *not* absorb Ansible variables, Docker labels, or repo file contents.
- **Personal ops agent owns:** calendar, email, comms, reminders, follow-ups, daily planning. A specialist, not the front door — morning planning starts at the dispatcher, which delegates.
- **Project agents own:** repo-local context, implementation slices, issue breakdowns, code/config changes, tests, PR creation, task logs.
- **Source-of-truth systems own:** Git history, issues, PRs, docs, deployment state, calendar/email records.
The split maps directly onto memory namespaces: dispatcher memory = routing map + preferences + policies + commitments + high-level summaries (thin, hot-tier-shaped); project memory = rationale, conventions, pitfalls, decisions; task/episode memory = the trace. The anti-bloat rule is the placement discipline already in the handoff: the *why* goes to memory, the *what* stays in the repo, routing facts go global, and the narrowest namespace wins.
 
### 2.3 Project registry
A lightweight registry of projects and ownership is what lets the dispatcher route without deep context:
```yaml
projects:
  homelab-infra:
    description: "Infrastructure-as-code for homelab services"
    owns: [jellyfin, traefik, authentik, proxmox, docker-stacks]
    repo: "<url>"
    agent: "homelab-agent"
    default_policy: "cautious-infra"
    approval_required_for: [deploy, restart-services, modify-secrets, destructive-changes]
```
**Placement decision (fits the source-of-truth hierarchy):** the registry is a YAML file in Git — Git is authoritative — with approved memories as its *projection* (facts of type `fact` in a routing namespace, source_id = the file). The reconciliation worker's source-ID cascade keeps the projection honest when the file changes.
 
### 2.4 Work tracking hierarchy & PRs as the bridge
```text
Dispatcher work item (broad intent + status)
  → project issue/epic (optional bridge)
    → implementation issues (agent-sized slices)
      → PR (the main reviewable artifact)
        → commits (actual change)
          → task log / trace (session history)
```
The dispatcher mostly needs PR-level awareness: what exists, what it claims, whether checks pass, whether review/deploy/approval is pending, whether it merged. Useful labels the integration plane can watch: `overmind-track`, `needs-user`, `needs-review`, `deploy-ready`, `blocked`, `risk-high`, `decision-needed`, `project-memory-updated` (that last one flags "a PR changed something memory derives from" — a reconciliation trigger).
 
### 2.5 Two interaction modes + the durable-artifact rule
- **Mode A (orchestrated):** user → dispatcher → project agent → PR/status → dispatcher → user.
- **Mode B (direct):** user → project agent → repo/issues/PRs — allowed and desirable, **but no shadow work**: any non-trivial direct session must leave a durable artifact (issue, PR, commit, updated doc, or workstream check-in). The dispatcher doesn't need to be in every conversation; it needs to be able to reconstruct what happened from durable artifacts. (The rule is already in the consumer instruction files; restated here because it is what makes Mode B safe.)
### 2.6 Action-approval taxonomy (harness/orchestrator-side — NOT memory-server scope)
Distinct from *memory* approval. Per the harness directive: memory governance is server-side; execution sandboxing and action gating are harness-side.
- **Safe to automate:** read inbox/calendar/issues, summarize, classify, draft tasks/issues/emails/PR descriptions, inspect repos, propose diffs, run tests.
- **Requires explicit approval:** send external messages/email, calendar events involving others, commit/push to important branches, merge, deploy, restart services, modify secrets, delete data, billing changes.
- **Extra caution domains:** auth systems, DNS/reverse proxy, storage/backups, user-facing services, anything touching secrets.
### 2.7 Integration plane (n8n or equivalent)
Deterministic glue for polling email/Discord/calendar/Git forges, normalizing events, watching labels, triggering safe workflows. **LLMs for triage/summarize/prioritize/route/draft; never for polling APIs or moving structured data A→B.** This is the "if a constraint can be expressed in code…" principle applied to token spend. The plane appends normalized events to the trace via `log_trace` like any other client — it is a producer at the harness seam, never an owner of truth.
 
### 2.8 Dispatcher phasing (indicative, from the predecessor roadmap)
1. Capture & prioritize: dump tasks from Discord/email, review on demand, "what should I do today?" → top 3 with reasoning. No autonomous execution.
2. Project routing & artifact tracking: registry, issue/PR integration, labels, status summaries. ("Update Jellyfin" → routed → issue/PR tracked → outcome reported.)
3. Project-local agents: repo AGENTS.md conventions, direct sessions, artifact-level sync.
4. Personal ops split-out (approval required for external actions).
5. Safer execution: deploy workflows, health checks, rollback notes, approval gates.
First milestone worth anything: *"show me my captured tasks and suggest the top 3 for today."* Not autonomous infra deployment.
 
### 2.9 Dispatcher non-goals (carried forward verbatim in spirit)
No fully autonomous god agent. No silent production deployments. No replacing Git forges' issues or repo docs with chat memory. No requiring every agent to update central memory after every action. No proactive notification spam. No multi-agent science project before the capture/routing loop works.
 
---
 
## 3. Considered and rejected (do not re-litigate without new evidence)
 
- **Wide normalized schema** (separate `sessions`, `messages`, `events`, `tool_calls`, `tool_results`, `decisions`, `tasks`, `provenance_refs` tables — ~18 tables in the predecessor draft). Rejected: the collapsed two-table core (traces + memories, JSONB content, type column) is the right size for a 1–2 session build and the predecessor's own principles doc warned against drifting into "a bespoke Hindsight/mem9/Graphify clone." Widen a table only when a JSONB key proves join-load-bearing.
- **Separate `decisions` / `tasks` tables.** Rejected: memory `type` column covers it; the decision *shape* (decision + rationale + alternatives) is a content convention, not schema.
- **REST API alongside MCP in v1.** Rejected: MCP (stdio + HTTP) plus `memctl` is the whole surface until a consumer that can't speak MCP actually exists.
- **pgvector in v0/v1.** Rejected (predecessor had it "optional in V0"): the vector lane is earned by dogfooding miss-tallies, not shipped speculatively. The seam is documented; that is all.
- **Markdown vault as primary store.** Rejected for this system: the ledger is canonical; wiki is a projection; manual edits to exported files re-enter as proposals. (Filesystem-native is a legitimate paradigm — for a different project shape.)
- **Anonymous or agent-borrowed approval events.** Rejected: an approval without a distinct reviewer identity is fake provenance. (Now enforced — spec §6b.)
- **Reject-on-secret for trace writes.** Rejected in favor of redact-in-place: dropping the event trades a secrets risk for an audit hole. (Now spec §5.)
- **Storing chain-of-thought as decision provenance.** Rejected: store *observable decision summaries* (decision, rationale summary, alternatives considered, source refs). Auditability must not depend on private reasoning being retained.
- **A second datastore for anything** (ClickHouse, Redis, dedicated vector DB, queue). Standing rejection from the spec's Do Not Build; the predecessor docs agreed. Trace↔memory joins are load-bearing.
- **Building the harness/orchestrator before ≥2 memory consumers exist.** Both projects independently concluded the dispatcher comes last.
---
 
## 4. One-line index of where the merged ideas landed
 
For future archaeology — what moved out of the retired docs and where it lives now:
 
| Idea | Landed in |
|---|---|
| Projection principle / "ledger with projections" | handoff v4 (framing + preferences), spec §13 (export seam) |
| Review event convention (reviewer identity, synthetic session) | spec §6b, handoff provenance section |
| Edit-then-approve lifecycle | spec §5/§9, handoff typology section |
| `metadata` JSONB + `content_hash` + 3 new types | spec §4 |
| Redact-vs-reject never-store split | spec §5 |
| Trace metadata conventions (model/provider, repo/branch) | spec §4 taxonomy |
| Memory placement discipline (why-not-what, narrowest namespace) | handoff v4, checklist §2 |
| Durable-artifact / no-shadow-work rule | checklist §2, §2.5 here |
| Seeding discipline (seeds carry source traces) | checklist §3, handoff provenance |
| Decision content convention (rationale + alternatives) | checklist §3 |
| Hierarchical path-style namespace names | spec §3 |
| Harness-as-client seam | handoff harness directive (was already there; predecessor confirmed) |
| Everything dispatcher-shaped | §2 here, build phase 4 |
