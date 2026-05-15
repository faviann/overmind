# Executive summary

You are **not** primarily building “memory for one chatbot.” You are building a **shared, inspectable memory substrate** for multiple development agents that should be able to coordinate without all sharing the same bloated prompt context.

The right V0 is:

**Postgres-first central substrate with strict namespaces + append-only event log + tool-call provenance + proposal/approval workflow + approved durable knowledge + simple retrieval API/MCP.**

Do **not** start with “vector DB + chunks” as the foundation. The uploaded corpus is very clear: flat vector RAG is useful as one retrieval index, but systems that treat it as the whole memory architecture run into temporal, relationship, multi-hop, update, provenance, and stale-memory problems. The article’s strongest claim is that every flat-vector-first system in the corpus ended up adding another structure on top. 

The recommended primary paradigm for your system is:

**Trace/event-log as source of truth + approved atomic knowledge as durable memory + hybrid retrieval later.**

Secondary paradigms:

1. **Multi-index hybrid retrieval** over approved knowledge, decisions, sessions, and tool traces.
2. **Filesystem/wiki export** as a human-readable derivative artifact, not the V0 source of truth.
3. **Graph augmentation** later, especially for repo/code structure, probably by integrating tools like Graphify rather than building your own graph layer immediately.

Your strongest design bias should be:

**Pay at write time, but only after the user-facing response path.**

That means V0 should capture provenance and proposals cheaply now, then let background jobs extract, type, deduplicate, summarize, and prepare memory for approval. The uploaded write-time article argues that durable facts are written once and read many times, so structured write-time investment amortizes well across future reads. 

---

# Key takeaways from the uploaded articles

## 1. Flat Vector RAG is not enough

The core failure is not that vector search is bad. It is that **semantic similarity is not memory**.

Flat vector RAG struggles when the question is:

“what changed since last week?”
“why did we decide this?”
“which tool command created this state?”
“what breaks if I change this?”
“which old memory is superseded?”
“what facts are approved versus merely observed?”
“what did Agent A hand off to Agent B?”

The uploaded “19 systems” article explicitly names temporal queries, relationship queries, multi-hop queries, provenance, navigation, structured filters, update semantics, and stale memories as the shapes that flat vector search alone does not support well. 

## 2. The eight paradigms are commitments, not menu items

The uploaded taxonomy names eight paradigms: Flat-RAG, Knowledge Graph, Progressive Compression, Multi-Index Hybrid, LLM-as-Retriever, Trace-as-Memory, Karpathy LLM Wiki, and Filesystem-Native. The important point is that these are different answers to “what is memory?” not just different implementations. 

For your use case, the most relevant insight is that paradigms compose only when their commitments do not collide. Trace-as-memory wants raw execution history to remain inspectable. Progressive compression wants old detail to fade into dense summaries. Both can exist, but not as the same primary store. 

## 3. Write-time investment is the compounding move

The uploaded write-time article identifies six high-value write-time moves:

1. online dedup/synthesis
2. atomization
3. multi-step ingest
4. provenance metadata
5. confidence/freshness scoring
6. type tagging

For V0, do **provenance, type tagging, namespaces, proposal state, and simple approval**. Defer heavier LLM-based dedup, confidence calibration, graph extraction, and progressive consolidation until you have real traffic. 

## 4. The repo list is a research corpus, not an adoption list

The uploaded repo list includes Graphify, Memex, EdgeQuake, SimpleMem, GitNexus, Agent0, Moraine, oh-my-kiro, Hindsight, MemoryOS, Supermemory, OpenContext, Understand-Anything, Second Brain, LLM Wiki, Tolaria, OpenKB, Graymatter, and mem9. Treat these as systems to learn from; do not assume any one is your substrate. 

---

# Lessons from the reference architecture diagram

The other builder’s system is valuable because it separates concerns cleanly:

**Transports are not the agent core.** Discord, console, web, voice, and peer-agent access are edge surfaces. They should not own memory semantics.

**The query loop emits structured events.** Turns, model calls, tool calls, tool results, compactions, memory injections, and handoffs should all become events.

**Memory has tiers.** Session-bound state, durable approved knowledge, pending proposals, task/project state, and handoffs are different objects.

**Tool calls are first-class provenance.** Parameters, results, status, side effects, and routing decisions should be inspectable later.

**Compaction is explicit.** A compaction boundary should be represented as an object with source range, summary, model/prompt version, and reversible pointers back to raw events.

**Approval gates matter.** Session observations should not silently become durable truth.

**Peer-agent access should be through a narrow API.** Claude Code, Codex, Pi.dev, Hermes/OpenClaw-style assistants, and MCP tools should call the memory substrate, not share one giant prompt context.

The part to copy is the **shape**, not the implementation details.

---

# Core diagnosis

You are solving four problems at once:

1. **Continuity:** agents should remember project decisions, conventions, failures, infrastructure facts, and user preferences across sessions.
2. **Cost reduction:** agents should retrieve a small approved context bundle instead of rereading full histories, logs, docs, or previous chats.
3. **Coordination:** multiple agents should share memory without sharing the same whole context.
4. **Auditability:** you need to know where a memory came from, who/what approved it, and what it supersedes.

For continuous assisted development, the critical retrieval shapes are:

| Query shape          | Example                                                      | Required store                   |
| -------------------- | ------------------------------------------------------------ | -------------------------------- |
| Current durable fact | “What is the current deployment rule for workstation tools?” | approved knowledge               |
| Decision rationale   | “Why did we choose Postgres-first?”                          | decisions + provenance           |
| Temporal trace       | “What did the agent do last Tuesday?”                        | event log                        |
| Tool provenance      | “What command changed this file?”                            | tool calls/results               |
| Project handoff      | “Where did Claude leave off?”                                | handoff objects                  |
| Repo convention      | “How do we structure Docker stacks?”                         | repo/project namespace knowledge |
| Relationship query   | “Which services depend on Traefik labels?”                   | later graph or typed relations   |
| Staleness query      | “Is this memory still true?”                                 | supersession/freshness fields    |
| Approval query       | “What observations are waiting for review?”                  | proposal queue                   |

Flat vector RAG answers only a subset of these.

---

# Memory paradigm comparison

| Paradigm                |                                                                 Fit for you | Recommendation                                                       |
| ----------------------- | --------------------------------------------------------------------------: | -------------------------------------------------------------------- |
| Flat-RAG                |                             Useful for fuzzy recall, bad as source of truth | Use only as one index later                                          |
| Knowledge Graph         | Strong for code structure, dependency, relationship, blast-radius questions | Defer as central layer; integrate per-repo tools like Graphify first |
| Progressive Compression |                     Useful for personal assistant and long-running sessions | Add after you have event logs and compaction boundaries              |
| Multi-Index Hybrid      |       Very relevant for retrieval across facts, sessions, decisions, traces | V1/V2 retrieval strategy                                             |
| LLM-as-Retriever        |                                         Useful for wiki/document navigation | Use later for wiki/docs, not V0 core                                 |
| Trace-as-Memory         |                    Extremely relevant for audit, tool provenance, debugging | Make this the V0 source-of-truth paradigm                            |
| Karpathy LLM Wiki       |                                 Useful as human-readable compiled knowledge | Use as derivative export, not DB replacement                         |
| Filesystem-Native       |                                   Great for human ownership and portability | Use for exports/artifacts; avoid dual source-of-truth in V0          |

Recommended primary paradigm:

**Trace-as-memory for source of truth, with approved atomic knowledge as the durable read model.**

Recommended secondary paradigms:

**Multi-index hybrid retrieval** and **filesystem/wiki derivative artifacts**.

Do not prioritize in V0:

* full knowledge graph
* autonomous memory writes without approval
* standalone vector database
* Discord/web/voice transports
* complex progressive memory hierarchy
* full agent harness orchestration
* automatic task planner
* “agent personality” memory
* speculative cross-agent synchronization

---

# Recommended architecture

## V0 architecture

```text
Agents / Tools
  Claude Code | Codex | Pi.dev | Hermes/OpenClaw | CLI scripts
        |
        | MCP / REST / CLI client
        v
Memory API
  - record_event()
  - record_tool_call()
  - search_knowledge()
  - get_context_bundle()
  - propose_memory()
  - list_proposals()
        |
        v
Postgres
  - append-only event log
  - sessions/messages
  - tool calls/results
  - memory proposals
  - approved knowledge
  - decisions
  - namespaces/domains
  - projects/tasks/handoffs
  - optional pgvector embeddings
        |
        v
Background worker
  - extraction
  - type tagging
  - atomization
  - dedup candidates
  - embedding generation
  - proposal generation
  - wiki export
```

Use **Postgres as the source of truth**. Add `pgvector` when you need semantic retrieval because it lets you keep vectors near the relational metadata, joins, provenance, and transactions. pgvector is specifically designed to store vectors in Postgres and supports nearest-neighbor search plus normal Postgres properties like joins and recovery. ([GitHub][1])

Use **MCP as the first integration surface**, because it gives multiple agents a standard way to access external context and tools. The official MCP spec describes it as an open protocol for connecting LLM applications to external data sources and tools, and the official server examples include filesystem, git, and memory-style servers. ([Model Context Protocol][2])

Do **not** make the memory service depend on one harness. The harness can come later.

---

# Central versus split memory

Use:

## Central substrate with separate namespaces/domains

Not one global soup. Not fully separate databases per agent.

Recommended namespace examples:

```text
personal
infra
infra/proxmox
infra/traefik
coding
repo/faviann/dotfiles
repo/faviann/traefik-kop
repo/client/kinesolution
assistant/email-calendar
global/conventions
```

Each object should carry:

```text
namespace_id
domain_type
project_id nullable
repo_id nullable
visibility_scope
created_by_agent_id
approval_status
```

## Why not separate knowledge bases?

Separate stores reduce contamination, but they make reuse and handoff painful. Your overmind/personal assistant, infra assistant, and coding agents will need to refer to shared conventions: deployment rules, repo layout, credentials policy, task status, and “what was already tried.” Fully split memory would force manual synchronization.

## Why not one undifferentiated KB?

Because personal memory, repo memory, infrastructure memory, and agent trace memory should not be retrieved together by default. A coding agent should not automatically see personal assistant memory. An infra agent should not pollute durable repo knowledge with a one-off observation.

## Direction

Use a **central event/knowledge substrate with hard namespace filters and scoped API tokens**.

Default retrieval should require a namespace or project scope. Global search should be explicit and logged.

---

# Proposed memory lifecycle

Full target lifecycle:

```text
observation
→ event log
→ extraction
→ atomization
→ typing
→ provenance attachment
→ confidence/freshness scoring
→ dedup/synthesis
→ proposal
→ approval
→ durable knowledge
→ retrieval
→ compaction
→ archival/deprecation
```

## V0 lifecycle

```text
agent/session/tool activity
→ append-only events
→ optional manual or simple LLM extraction
→ memory proposal
→ human edit/approve/reject
→ approved knowledge entry
→ simple retrieval by namespace/type/text
```

V0 should include:

* event log
* sessions/messages
* tool calls/results
* namespaces
* memory proposals
* approved knowledge entries
* provenance refs
* decisions
* basic search
* MCP/REST API
* approval CLI or minimal web view

V0 should not require:

* graph extraction
* auto-confidence scoring
* automatic contradiction resolution
* complex compaction
* multi-agent locking
* dashboards
* voice/Discord transports
* full task management

## V1 lifecycle additions

* background extraction from session closeout
* atomization into small claims
* type tagging
* embeddings
* supersedes links
* dedup candidate detection
* context bundle generation

## V2/V3 lifecycle additions

* confidence/freshness scoring
* compaction boundaries
* hybrid retrieval fusion
* graph augmentation
* wiki export/import
* task/project integration
* handoff objects
* cross-agent policies

---

# Write-time processing strategy

The V0 rule should be:

**Never block the user-facing answer on expensive memory processing.**

The agent can answer, then the worker processes the session afterward.

## Highest ROI write-time steps first

1. **Provenance capture**
   Cheap and foundational. Every future memory must point back to messages, tool calls, files, commands, or external issues.

2. **Namespace/type tagging**
   Cheap, improves retrieval immediately.

3. **Proposal generation**
   Prevents memory pollution. Nothing becomes durable truth silently.

4. **Atomization**
   Convert “session summary blob” into small claims. Add in V1.

5. **Dedup/supersession candidates**
   Add in V1/V2. Start with heuristic matching, then LLM-assisted review.

6. **Confidence scoring**
   Defer. Store fields now, calibrate later.

The uploaded write-time article suggests provenance first, types second, multi-step ingest third, atomization fourth, dedup fifth, and confidence scoring sixth. That order fits your V0/V1/V2 boundary well. 

## What runs async

* extraction from recent turns
* tool-result summarization
* memory proposal creation
* embedding generation
* dedup candidate search
* wiki export
* session closeout summary
* compaction candidate creation

## How this reduces token use

Instead of giving an agent:

```text
20k tokens of prior discussion + logs + old decisions
```

you give it:

```text
namespace: repo/faviann/dotfiles
task: workstation LXC setup
retrieved:
- 6 approved facts
- 3 decisions
- 2 recent handoffs
- 1 relevant tool result summary
- citations/provenance IDs
```

That is the difference between “read the entire past” and “read the compiled current state.”

---

# Provenance and auditability

Every durable memory should carry:

```text
id
namespace_id
type
content
status
created_at
updated_at
valid_from
valid_until nullable
created_by
approved_by nullable
approval_time nullable
source_refs[]
supersedes[]
superseded_by nullable
confidence nullable
freshness_state
extraction_model nullable
extraction_prompt_version nullable
embedding_model nullable
content_hash
```

Every tool call should log:

```text
tool_call_id
session_id
turn_id
agent_id
tool_name
tool_version
server_name
request_params_json
request_params_redacted_json
result_json_or_pointer
stdout_pointer nullable
stderr_pointer nullable
exit_code nullable
status
started_at
ended_at
latency_ms
side_effects_declared
files_read[]
files_written[]
commands_executed[]
cost_tokens_input nullable
cost_tokens_output nullable
cost_usd nullable
```

Record **observable decision summaries**, not hidden chain-of-thought. The system should store:

```text
decision: "Use Postgres-first for V0"
rationale_summary: "Need joins, provenance, transactions, and simple self-hosting before vector scale."
alternatives_considered: ["Qdrant-first", "SQLite-only", "filesystem-only"]
source_refs: [...]
```

This gives you auditability without depending on private reasoning.

---

# Compaction strategy

Compaction should be represented as a first-class boundary:

```text
compaction_boundary
  id
  namespace_id
  session_id
  source_message_start_id
  source_message_end_id
  source_event_ids[]
  summary
  extracted_facts[]
  decisions[]
  unresolved_questions[]
  tool_calls_summarized[]
  model
  prompt_version
  created_at
  created_by
  checksum_of_source_range
```

V0 does not need to compact aggressively. It only needs schema fields that make compaction possible later.

Rules:

* Never delete raw messages/events in V0.
* Mark a range as compacted, but keep the source.
* Store summary plus source pointers.
* Store the prompt/model version used.
* Allow re-compaction later.
* Treat compaction output as a proposal unless approved.

This makes debugging possible when a future agent says, “why do we think this is true?”

---

# Minimal V0 schema/entity model

This is intentionally small but not boxed in.

## Core identity and scope

```sql
namespaces (
  id uuid primary key,
  name text unique not null,
  parent_id uuid null references namespaces(id),
  domain_type text not null, -- personal, infra, repo, project, global
  description text,
  created_at timestamptz not null default now()
);

agents (
  id uuid primary key,
  name text not null,
  kind text not null, -- codex, claude_code, hermes, openclaw, human, worker
  api_token_hash text null,
  default_namespace_id uuid null references namespaces(id),
  created_at timestamptz not null default now()
);

projects (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  name text not null,
  repo_url text null,
  external_tracker_url text null,
  status text not null default 'active',
  created_at timestamptz not null default now()
);
```

## Sessions, turns, events

```sql
sessions (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  project_id uuid null references projects(id),
  agent_id uuid null references agents(id),
  title text,
  transport text, -- cli, mcp, discord, web, voice
  status text not null default 'open',
  started_at timestamptz not null default now(),
  ended_at timestamptz null,
  metadata jsonb not null default '{}'
);

messages (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  role text not null, -- user, assistant, system, tool
  content text not null,
  token_count int null,
  created_at timestamptz not null default now(),
  metadata jsonb not null default '{}'
);

events (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  session_id uuid null references sessions(id),
  message_id uuid null references messages(id),
  agent_id uuid null references agents(id),
  event_type text not null,
  occurred_at timestamptz not null default now(),
  correlation_id text null,
  payload jsonb not null default '{}',
  content_hash text null
);
```

## Tool calls and results

```sql
tool_calls (
  id uuid primary key,
  event_id uuid not null references events(id),
  session_id uuid not null references sessions(id),
  agent_id uuid null references agents(id),
  tool_name text not null,
  tool_version text null,
  server_name text null,
  params jsonb not null default '{}',
  params_redacted jsonb not null default '{}',
  started_at timestamptz not null default now(),
  ended_at timestamptz null,
  status text not null,
  side_effects jsonb not null default '{}'
);

tool_results (
  id uuid primary key,
  tool_call_id uuid not null references tool_calls(id),
  result_kind text not null, -- json, text, file, error
  result_json jsonb null,
  result_text text null,
  artifact_uri text null,
  stdout text null,
  stderr text null,
  exit_code int null,
  created_at timestamptz not null default now()
);
```

## Memory proposals and durable knowledge

```sql
provenance_refs (
  id uuid primary key,
  source_type text not null, -- message, event, tool_call, tool_result, file, issue, url
  source_id uuid null,
  external_uri text null,
  source_hash text null,
  text_span jsonb null,
  created_at timestamptz not null default now()
);

memory_proposals (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  project_id uuid null references projects(id),
  proposed_by_agent_id uuid null references agents(id),
  proposal_type text not null, -- fact, decision, preference, task, convention, warning
  content text not null,
  rationale text null,
  status text not null default 'pending', -- pending, approved, rejected, needs_edit
  confidence numeric null,
  freshness_state text not null default 'unverified',
  extraction_model text null,
  extraction_prompt_version text null,
  created_at timestamptz not null default now(),
  reviewed_at timestamptz null,
  reviewed_by text null,
  metadata jsonb not null default '{}'
);

memory_proposal_sources (
  proposal_id uuid references memory_proposals(id),
  provenance_ref_id uuid references provenance_refs(id),
  primary key (proposal_id, provenance_ref_id)
);

knowledge_entries (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  project_id uuid null references projects(id),
  entry_type text not null,
  title text null,
  content text not null,
  status text not null default 'active', -- active, deprecated, superseded, archived
  confidence numeric null,
  freshness_state text not null default 'approved',
  approved_from_proposal_id uuid null references memory_proposals(id),
  valid_from timestamptz null,
  valid_until timestamptz null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  content_hash text not null,
  metadata jsonb not null default '{}'
);

knowledge_sources (
  knowledge_entry_id uuid references knowledge_entries(id),
  provenance_ref_id uuid references provenance_refs(id),
  primary key (knowledge_entry_id, provenance_ref_id)
);

knowledge_supersession (
  old_entry_id uuid references knowledge_entries(id),
  new_entry_id uuid references knowledge_entries(id),
  reason text,
  created_at timestamptz not null default now(),
  primary key (old_entry_id, new_entry_id)
);
```

## Decisions, tasks, handoffs, compaction, embeddings

```sql
decisions (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  project_id uuid null references projects(id),
  title text not null,
  decision text not null,
  rationale_summary text not null,
  alternatives_considered jsonb not null default '[]',
  status text not null default 'active',
  created_at timestamptz not null default now()
);

tasks (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  project_id uuid null references projects(id),
  title text not null,
  status text not null default 'open',
  external_issue_uri text null,
  metadata jsonb not null default '{}',
  created_at timestamptz not null default now()
);

handoffs (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  project_id uuid null references projects(id),
  from_agent_id uuid null references agents(id),
  to_agent_kind text null,
  summary text not null,
  relevant_knowledge_ids uuid[] not null default '{}',
  relevant_event_ids uuid[] not null default '{}',
  status text not null default 'open',
  created_at timestamptz not null default now()
);

compaction_boundaries (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  session_id uuid references sessions(id),
  source_start_message_id uuid null references messages(id),
  source_end_message_id uuid null references messages(id),
  summary text not null,
  prompt_version text null,
  model text null,
  source_checksum text null,
  created_at timestamptz not null default now(),
  metadata jsonb not null default '{}'
);

embedding_records (
  id uuid primary key,
  namespace_id uuid not null references namespaces(id),
  object_type text not null, -- knowledge_entry, proposal, message, decision, tool_result
  object_id uuid not null,
  embedding_model text not null,
  text_hash text not null,
  vector vector null,
  created_at timestamptz not null default now()
);
```

---

# Open-source tools/libraries analysis

## Most relevant systems to inspect deeply

**Hindsight** is worth inspecting for routing and memory architecture. Its project describes itself as an agent memory system focused on agents that learn over time, not just conversation recall. For your purposes, learn from its routing, observation tier, and memory consolidation ideas rather than adopting it blindly. ([GitHub][3])

**mem9** is relevant because it targets OpenClaw, Hermes, Claude Code, Codex, and multi-agent shared memory. It appears close to your integration shape, but if you want self-hosted ownership, treat hosted/cloud aspects carefully. Learn from its agent integrations and dashboard; do not make it your only substrate until data ownership and self-hosting are clear. ([GitHub][4])

**Graphify** is highly relevant for repo-specific structural understanding. It maps code/docs/media into a knowledge graph that coding assistants can query, which fits the “what breaks if I change this?” class of questions. Use it as a per-repo structural tool, not as your global memory database. ([GitHub][5])

**OpenContext** is relevant because it is explicitly a personal context store that reuses existing CLI agents like Codex, Claude, and OpenCode. It is worth evaluating for UX and CLI integration patterns. ([GitHub][6])

**SimpleMem** is worth studying for write-time compression, adaptive retrieval, and provenance. Its README describes a long-term memory workflow, and its related documentation highlights provenance tracking and automatic consolidation. Learn from it; do not necessarily adopt its whole stack. ([GitHub][7])

**MemoryOS** is relevant later for progressive memory tiers. Its project describes a hierarchical architecture with storage, updating, retrieval, and generation modules for personalized agents. Use it as a V2/V3 inspiration for personal assistant memory, not V0. ([GitHub][8])

**OpenKB, LLM Wiki, and Tolaria** are relevant to the wiki/filesystem layer. OpenKB compiles raw documents into structured interlinked wiki-style knowledge; LLM Wiki builds and maintains a persistent wiki instead of re-deriving from RAG each query; Tolaria is an AI-first markdown vault/editor with agent setup paths. These support the idea of a human-readable derivative knowledge layer. ([GitHub][9])

**Understand-Anything** is relevant for turning codebases or knowledge bases into explorable graphs, and its docs mention workflows for codebase analysis, diff impact, onboarding, and Karpathy-style wiki analysis. Use it as an optional visual/graph exploration layer. ([GitHub][10])

**EdgeQuake** is relevant for GraphRAG, but too heavy for V0. Its docs position GraphRAG as combining knowledge graphs with vector search, and its README describes a Rust GraphRAG framework. Learn from it when you add relationship retrieval. ([GitHub][11])

## Layer-by-layer recommendation

| Layer                   | Build/adopt/defer       | Recommendation                                          |
| ----------------------- | ----------------------- | ------------------------------------------------------- |
| Relational/event store  | Adopt                   | Postgres first                                          |
| Vector index            | Adopt lightly           | pgvector in V0/V1; Qdrant only if scale demands it      |
| Graph/knowledge layer   | Defer                   | Use Graphify per repo before building central graph     |
| Filesystem/wiki layer   | Build derivative export | Generate Markdown from approved knowledge               |
| Embeddings              | Adopt                   | Start with local or API embeddings; store model/version |
| MCP interface           | Build small             | Use official MCP SDK/server pattern                     |
| Extraction pipeline     | Build minimal           | One worker, prompt-versioned, async                     |
| Approval workflow       | Build minimal           | CLI/TUI first, web dashboard later                      |
| Task/project tracker    | Integrate               | Use GitHub/Gitea issues; store pointers                 |
| Provider abstraction    | Defer or adopt          | LiteLLM later if needed                                 |
| Local agent integration | Build                   | AGENTS.md + MCP config + CLI client                     |

For vector search, pgvector keeps vectors in Postgres, while Qdrant is a dedicated vector database with filtering and a production API. Start with pgvector because V0 needs joins/provenance more than vector scale. ([GitHub][1])

For provider abstraction, LiteLLM is a reasonable later option because it exposes a unified OpenAI-compatible gateway for many providers, but it is not required for the memory substrate V0. ([GitHub][12])

Acontext is an additional project worth watching because it treats agent skills as a memory layer and stores learnings as readable/editable skill files; that overlaps with your desire for approved, reusable agent knowledge without opaque memory pollution. ([GitHub][13])

---

# V0/V1/V2/V3 roadmap

## V0 — smallest useful memory substrate

Goal: agents can record events, propose memory, approve durable knowledge, and retrieve approved context.

Build:

* Postgres schema
* namespace model
* sessions/messages/events
* tool calls/results
* memory proposals
* approved knowledge entries
* provenance refs
* decisions
* simple text search
* optional pgvector column
* REST API
* MCP server with read/propose tools
* CLI approval workflow
* AGENTS.md integration examples

V0 MCP tools:

```text
memory.search_knowledge
memory.get_context_bundle
memory.record_event
memory.record_tool_call
memory.propose_memory
memory.list_proposals
memory.get_decisions
memory.create_handoff
```

Non-goals:

* autonomous approval
* full graph
* dashboard
* voice/Discord/web transports
* complex compaction
* task planner
* provider router

## V1 — write-time extraction and better retrieval

Add:

* async extraction worker
* session closeout summaries
* atomization
* type tagging
* embeddings
* dedup candidates
* supersedes suggestions
* context bundle templates
* repo/project bootstrap command

## V2 — compaction, hybrid retrieval, task integration

Add:

* compaction boundaries
* BM25/text + vector fusion
* task/project table synced to GitHub/Gitea
* handoff workflows
* approval dashboard
* freshness states
* confidence shadow scoring
* wiki export

## V3 — graph and multi-agent policy layer

Add:

* Graphify/OpenKB/Understand-Anything integration
* relation extraction
* per-agent ACLs
* cross-namespace handoff approval
* conflict detection
* automatic stale-memory review
* provider abstraction if needed
* optional Discord/web/voice transports

---

# Risks and mitigations

| Risk                        | Mitigation                                           |
| --------------------------- | ---------------------------------------------------- |
| Overengineering             | V0 is Postgres + API + MCP + proposals only          |
| Memory pollution            | Approval-before-durable-memory                       |
| Stale memories              | status, valid_until, supersedes links                |
| Contradictions              | proposal review + supersession table                 |
| Excessive write cost        | async extraction, batch jobs, budget caps            |
| Bad retrieval               | namespace filters, type filters, provenance included |
| Schema lock-in              | append-only events + JSONB payloads + migrations     |
| Privacy/security            | scoped API tokens, namespace ACLs, redaction         |
| Agent drift                 | behavior enforced by API, not just prompts           |
| Multi-agent contention      | append-only events; durable writes require approval  |
| Operational burden          | one Postgres, one API, one worker                    |
| Premature graph complexity  | integrate per-repo graph tools later                 |
| Hidden reasoning dependency | store decision summaries, not chain-of-thought       |

---

# Architecture Decision Records

## ADR-001: Central substrate with namespaces

**Status:** proposed

**Context:** Multiple agents need shared memory, but personal, infra, repo, and task memories must not contaminate each other.

**Decision:** Use one central Postgres-backed substrate with strict namespaces/domains.

**Consequences:** Easier reuse and handoff; requires strong namespace filtering and scoped access.

**Alternatives considered:** fully separate DBs per agent; one global knowledge base.

---

## ADR-002: Postgres-first instead of vector-store-first

**Status:** proposed

**Context:** V0 needs provenance, joins, approvals, event logs, and durable records more than massive semantic scale.

**Decision:** Use Postgres as source of truth; add pgvector for embeddings when needed.

**Consequences:** Simpler self-hosting and auditability; less specialized vector performance than Qdrant.

**Alternatives considered:** Qdrant-first, SQLite-first, filesystem-only.

---

## ADR-003: Event log as source of truth

**Status:** proposed

**Context:** Agents need auditability for messages, tool calls, compactions, proposals, approvals, and handoffs.

**Decision:** Store all agent/session/tool activity as append-only events.

**Consequences:** Durable knowledge can be regenerated; storage grows over time.

**Alternatives considered:** only storing approved memories; storing only conversation transcripts.

---

## ADR-004: Approval before durable memory

**Status:** proposed

**Context:** Session observations may be wrong, temporary, private, or contradicted later.

**Decision:** Extracted memories become proposals first. Human approval is required before durable knowledge.

**Consequences:** Prevents pollution; adds review friction.

**Alternatives considered:** auto-write all memories; trust confidence score; agent self-approval.

---

## ADR-005: Write-time extraction runs asynchronously

**Status:** proposed

**Context:** Write-time processing improves future reads but can increase latency and token cost.

**Decision:** User-facing responses must not wait for heavy extraction. Extraction, atomization, embeddings, and dedup run in background jobs.

**Consequences:** New memories may appear after a delay; read path stays cheap.

**Alternatives considered:** synchronous extraction; read-time-only extraction.

---

## ADR-006: Provenance required on all durable memories

**Status:** proposed

**Context:** Memory without source references is hard to debug and unsafe to trust.

**Decision:** Every approved knowledge entry must link to provenance refs.

**Consequences:** More schema complexity; much better auditability.

**Alternatives considered:** optional provenance; storing only text content.

---

## ADR-007: Memory atoms over raw chunks

**Status:** proposed

**Context:** Raw chunks are too coarse for precise retrieval and dedup.

**Decision:** Durable knowledge should be small typed entries or atoms, not arbitrary chunks.

**Consequences:** Better retrieval and supersession; requires extraction work.

**Alternatives considered:** chunk all transcripts; store only summaries.

---

## ADR-008: Compaction boundaries are first-class objects

**Status:** proposed

**Context:** Long sessions need summarization, but compaction can hide errors.

**Decision:** Represent each compaction as an auditable boundary with source range, summary, model, prompt version, and checksum.

**Consequences:** Compaction is reversible enough for debugging; schema must support it before implementation.

**Alternatives considered:** overwrite old messages; store summaries only.

---

## ADR-009: Filesystem/wiki layer is derivative in V0

**Status:** proposed

**Context:** Markdown wikis are human-readable and agent-friendly, but dual source-of-truth creates sync problems.

**Decision:** Generate wiki/Markdown exports from approved DB knowledge. Manual edits should re-enter as proposals.

**Consequences:** Clear source of truth; less natural manual editing until import workflow exists.

**Alternatives considered:** Markdown vault as primary store; no filesystem artifacts.

---

## ADR-010: Graph layer deferred

**Status:** proposed

**Context:** Graph memory is valuable for repo structure and relationships, but expensive to maintain.

**Decision:** Do not build a central graph layer in V0. Integrate per-repo tools like Graphify later.

**Consequences:** V0 cannot answer deep relationship/blast-radius queries natively; avoids premature complexity.

**Alternatives considered:** Neo4j-first, Graphify as central memory, EdgeQuake-first.

---

## ADR-011: MCP/API access for peer agents

**Status:** proposed

**Context:** Codex, Claude Code, Pi.dev, Hermes/OpenClaw-style assistants, and future tools need a common access surface.

**Decision:** Expose memory through REST and MCP. Agents receive scoped tokens and namespace defaults.

**Consequences:** Agents can share memory without sharing full context; requires API design and auth.

**Alternatives considered:** direct DB access; per-agent files; harness-specific plugin only.

---

## ADR-012: V0 scope boundary

**Status:** proposed

**Context:** The project could easily become a full harness.

**Decision:** V0 is only the memory substrate: event logging, provenance, proposals, approved knowledge, simple retrieval, MCP/API.

**Consequences:** Faster delivery; transports, orchestration, graph, dashboard, and provider routing are deferred.

**Alternatives considered:** build full OpenClaw/Hermes-style assistant first; adopt a hosted memory service.

---

# Final implementation brief for Codex/Claude Code

## Project purpose

Build a self-hosted memory substrate for multi-agent assisted development. The system should let agents such as Codex, Claude Code, Pi.dev, Hermes/OpenClaw-style assistants, and MCP tools record session/tool activity, propose durable memories, retrieve approved knowledge, and preserve provenance across projects.

## V0 scope

Build:

* Postgres-backed memory API
* namespaces/domains
* sessions/messages/events
* tool calls/results
* provenance refs
* memory proposals
* approved knowledge entries
* decisions
* handoffs
* simple search
* optional pgvector support
* MCP server exposing memory tools
* CLI approval workflow
* seed AGENTS.md examples for Codex/Claude Code

## Non-goals

Do not build:

* full autonomous agent harness
* Discord/web/voice transports
* central knowledge graph
* complex dashboard
* automatic durable memory approval
* provider router
* task planner
* advanced compaction
* multi-agent orchestration

## Proposed architecture

```text
apps/api        REST API for memory operations
apps/mcp        MCP server exposing memory tools
apps/worker     async extraction/indexing jobs
apps/cli        operator approval/review CLI
packages/client typed client library
packages/schema shared schema/types
migrations      database migrations
docs/adrs       architecture decision records
docs/architecture design notes
examples        Codex/Claude/Pi integration examples
tests           unit/integration/e2e tests
```

## First schema draft

Implement tables:

* namespaces
* agents
* projects
* sessions
* messages
* events
* tool_calls
* tool_results
* provenance_refs
* memory_proposals
* memory_proposal_sources
* knowledge_entries
* knowledge_sources
* knowledge_supersession
* decisions
* tasks
* handoffs
* compaction_boundaries
* embedding_records

## First milestones

1. **Database foundation**

   * migrations
   * namespace seed data
   * event/session/message tables
   * tests for append-only event logging

2. **Tool provenance**

   * tool call/result APIs
   * redacted params support
   * artifact/result pointer support

3. **Memory proposals**

   * create/list/update proposal APIs
   * approve/reject/edit workflow
   * provenance required before approval

4. **Approved knowledge retrieval**

   * simple namespace/type/text search
   * return provenance with every result
   * context bundle endpoint

5. **MCP integration**

   * expose `search_knowledge`, `get_context_bundle`, `propose_memory`, `record_event`, `record_tool_call`, `create_handoff`

6. **CLI approval**

   * list pending proposals
   * inspect provenance
   * approve/reject/edit

7. **Agent integration examples**

   * AGENTS.md instructions
   * Claude Code config example
   * Codex/Pi-style CLI usage example

## Test strategy

* migration tests
* schema constraint tests
* event immutability tests
* provenance-required approval tests
* namespace isolation tests
* retrieval snapshot tests
* MCP tool contract tests
* end-to-end fake agent session:

  1. create session
  2. log messages
  3. log tool call/result
  4. create proposal
  5. approve proposal
  6. retrieve context bundle

## Open questions

* API implementation language: Python/FastAPI, TypeScript, or .NET Minimal API
* first UI: CLI only, TUI, or tiny web page
* local embedding model versus API embeddings
* whether pgvector is included in V0 or V1
* how to map repo namespaces automatically
* how approval should work from Discord/web later
* whether Graphify becomes a per-repo companion tool in V1 or V2

## What not to build yet

Do not build the agent harness. Do not build graph memory. Do not build voice/Discord/web transports. Do not build automatic memory approval. Do not build a complex dashboard. Do not optimize for novelty. Build the boring substrate first.

[1]: https://github.com/pgvector/pgvector?utm_source=chatgpt.com "pgvector/pgvector: Open-source vector similarity search for ..."
[2]: https://modelcontextprotocol.io/specification/2025-03-26?utm_source=chatgpt.com "Specification"
[3]: https://github.com/vectorize-io/hindsight?utm_source=chatgpt.com "vectorize-io/hindsight - Agent Memory That Learns"
[4]: https://github.com/mem9-ai/mem9?utm_source=chatgpt.com "mem9-ai/mem9: Unlimited memory for OpenClaw"
[5]: https://github.com/safishamsi/graphify?utm_source=chatgpt.com "safishamsi/graphify: AI coding assistant skill (Claude ..."
[6]: https://github.com/0xranx/OpenContext?utm_source=chatgpt.com "0xranx/OpenContext: A personal context store for AI agents ..."
[7]: https://github.com/aiming-lab/SimpleMem/blob/main/README.md?utm_source=chatgpt.com "README.md - aiming-lab/SimpleMem"
[8]: https://github.com/BAI-LAB/MemoryOS?utm_source=chatgpt.com "BAI-LAB/MemoryOS: [EMNLP 2025 Oral] ..."
[9]: https://github.com/VectifyAI/OpenKB?utm_source=chatgpt.com "OpenKB: Open LLM Knowledge Base"
[10]: https://github.com/Lum1104/Understand-Anything?utm_source=chatgpt.com "Lum1104/Understand-Anything: Graphs that teach ..."
[11]: https://github.com/raphaelmansuy/edgequake?utm_source=chatgpt.com "raphaelmansuy/edgequake: EdegQuake 🌋 High- ..."
[12]: https://github.com/BerriAI/litellm?utm_source=chatgpt.com "BerriAI/litellm: Python SDK, Proxy Server (AI Gateway) ..."
[13]: https://github.com/memodb-io/Acontext?utm_source=chatgpt.com "memodb-io/Acontext: Agent Skills as a Memory Layer"
