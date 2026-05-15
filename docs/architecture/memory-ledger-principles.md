# Memory Ledger Principles

We are not building "RAG for agents"; we are building a replayable memory ledger whose approved knowledge can be projected into RAG, wiki, graph, and trace/debug systems.

This project should not accidentally become a homemade monolithic memory system. The intent is to grow it into a small memory control plane and ledger that can later feed existing memory systems.

## Directional Target

```text
agent/user/tool event
        |
        v
append-only trace
        |
        v
memory proposal
        |
        v
approve / reject / edit / supersede
        |
        v
approved memory ledger
        |
        v
derived projections:
  - text/BM25 search
  - vector index
  - markdown/wiki export
  - graph projection
  - repo/code intelligence projection
  - trace/debug/replay views
```

## Non-Negotiable Invariants

### 1. Replayability Must Not Be Sacrificed

A future agent or human should be able to inspect what happened and reconstruct why a memory exists.

Do not design a flow where memory is written directly into an opaque store without a trace, proposal, or provenance path.

Bad:

```text
conversation -> vector DB -> retrieve later
```

Better:

```text
conversation/tool event -> trace -> proposal -> approved memory -> vector DB projection
```

### 2. Provenance Is Mandatory

Every approved memory should eventually answer:

- What source event produced this?
- Which agent/session produced it?
- Was it human-approved, policy-approved, or imported?
- When was it approved?
- Has it been superseded?
- What previous memory/version does it replace or refine?
- Which files, commands, tool calls, or conversations support it?

If a schema change makes provenance harder, reconsider it.

### 3. Derived Indexes Are Disposable

Search indexes, vector embeddings, graph databases, markdown exports, and wiki pages should be rebuildable from canonical records.

They may be useful and important, but they should not become the only place truth exists.

### 4. Approval Is Part Of The Memory Lifecycle

The system should support human-in-the-loop memory creation.

A memory proposal is not the same thing as approved knowledge.

Rejected proposals are still useful audit data and should not necessarily disappear.

### 5. Write-Time Investment Is Expected

The project should prefer doing careful work when memory enters the system rather than forcing every future read to re-derive context.

Examples of write-time work:

- extracting atomic candidate facts
- tagging memory type
- attaching provenance
- deduplicating against existing knowledge
- detecting contradiction/supersession
- producing a clean human-readable form
- producing structured metadata

Do not overbuild this immediately, but design so these steps can be added incrementally.

## Hybrid Memory Model

This project should not try to pick one memory paradigm too early.

The likely final system will be hybrid:

- **Trace-as-memory** for replay/debug/audit
- **Approved knowledge ledger** for curated durable facts/decisions
- **Filesystem/wiki projection** for human-readable compiled knowledge
- **Hybrid search** for retrieval
- **Graph/code intelligence** for repo-structure questions
- **Progressive compression/summaries** later for long-running continuity

The project's job is to make those possible without locking us into one tool.

## Current Concern To Avoid

Do not keep expanding the current Postgres/Python spike until it accidentally becomes a bespoke Hindsight/mem9/Graphify/LLM-Wiki clone.

Instead, make the local system the small canonical substrate:

```text
trace + proposal + approval + provenance + export/projection APIs
```

Then evaluate external/open-source tools as projections or adapters.

## Near-Term Milestones

### Milestone 0: Keep V0a Working

Preserve the current manual flow:

```text
manual proposal -> approve/reject -> approved knowledge -> text search
```

This is useful as the smallest vertical slice.

### Milestone 1: Add Trace Foundation

Introduce a first-class trace/event model.

Minimum event types may include:

- user_message
- agent_message
- tool_call
- tool_result
- file_observed
- file_modified
- command_run
- decision
- error
- memory_proposal_created
- memory_approved
- memory_rejected
- memory_superseded

The first implementation can be simple. The important part is that future memory records can point back to source events.

### Milestone 2: Link Proposals To Traces

Memory proposals should not be floating text.

They should reference their source event(s), session, agent, and context.

A proposal should be inspectable before approval.

### Milestone 3: Improve Approved Memory Schema

Approved memory should support, even if some fields are nullable at first:

- id
- title/name
- body/content
- type
- status
- tags
- source_event_ids
- source_proposal_id
- created_at
- approved_at
- approved_by
- agent/source
- confidence/status
- supersedes_id
- superseded_by_id
- metadata JSON

Memory type examples:

- fact
- decision
- constraint
- preference
- architecture_note
- repo_note
- workflow_note
- open_question
- warning
- runbook
- ADR_reference

### Milestone 4: Add Export/Projection Boundary

Add a clean way to export approved memories and traces.

Simple formats are fine:

- JSONL
- Markdown
- SQLite/Postgres views
- files under a wiki-like folder

The goal is to allow future sync into:

- vector DB
- markdown wiki
- graph memory
- repo intelligence system
- external memory tools

### Milestone 5: Add Basic Retrieval With Provenance

Text search is fine for now.

When returning search results, include provenance fields, not just content.

A retrieval result should help an agent answer:

> Why am I seeing this memory, and where did it come from?

### Milestone 6: Add Supersession/Update Semantics

Eventually, memory must support facts changing over time.

Do not only overwrite rows.

Prefer a versioned/supersession model:

```text
memory A: old decision
memory B: new decision, supersedes A
```

This protects auditability and replay.

### Milestone 7: Evaluate External Memory Systems As Projections

Only after the trace/proposal/provenance substrate exists, evaluate tools like:

- mem9
- Hindsight
- LLM Wiki
- OpenKB
- Graphify
- OpenContext
- other repo/code graph tools

The evaluation question is not:

> Which one should own our memory?

The evaluation question is:

> Which one is useful as a projection/index/query engine over our canonical memory substrate?

## Things To Avoid

Avoid building:

- a full agent orchestrator
- a full UI dashboard too early
- a bespoke graph memory system too early
- a complex vector pipeline before provenance exists
- an opaque memory write path
- memory records with no source
- summaries that replace raw traces
- direct writes from agents into approved knowledge without proposal/approval policy
- tool-specific assumptions that prevent later use by multiple agents

## Working Style

Prefer thin vertical slices.

Each slice should preserve the lifecycle:

```text
event -> proposal -> approval decision -> approved memory -> searchable/projection output
```

When implementing a feature, ask:

1. Does this preserve replayability?
2. Does this preserve provenance?
3. Can derived indexes be rebuilt?
4. Can another agent understand why this memory exists?
5. Does this help us move toward multi-agent shared memory rather than a single-agent notes app?

## Expected Final Shape

The long-term system should feel like a durable memory substrate with multiple read/write surfaces:

```text
Agents/tools produce traces
Humans/policies approve durable knowledge
The ledger preserves provenance and history
Indexes/projections make it useful
Replay/debug tools make it trustworthy
```

The system should make it possible for one agent to understand, debug, or replay the work of another agent.

That replay/debug property is a core design goal, not a nice-to-have.
