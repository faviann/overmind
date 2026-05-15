# Memory Substrate Project Direction

We are not building "RAG for agents"; we are building a replayable memory ledger whose approved knowledge can be projected into RAG, wiki, graph, and trace/debug systems.

This project is a self-hosted memory substrate for a future multi-agent development assistant/harness.

This is **not** yet a full agent harness. Do not drift into building the full orchestrator, chat UI, agent runtime, or workflow automation platform. The current goal is to build the memory substrate and lifecycle primitives that will later allow multiple agents/tools to share durable context safely.

## High-Level Goal

We are building a replayable, auditable, provenance-first memory layer for assisted development.

The system should eventually support agents/tools such as Codex, Claude Code, Hermes, OpenClaw-style assistants, MCP tools, Discord/web/voice interfaces, and repo-specific coding agents.

The core problem is not "store embeddings and search them."

The core problem is:

> How do we preserve what happened, what was inferred, what was approved, why it was approved, where it came from, and how future agents can safely use it without losing replayability?

## Critical Architecture Principle

The project must distinguish between:

### 1. Raw Trace

What actually happened.

Examples:

- user messages
- agent messages
- tool calls
- tool results
- files touched
- commands run
- errors
- decisions
- approvals

This should be append-only as much as possible.

It exists so we can replay, debug, audit, compare agents, and understand why the system believes something.

### 2. Memory Proposals

Candidate facts, knowledge, or decisions extracted from traces.

These are not trusted yet.

They may be accepted, rejected, edited, superseded, or deferred.

### 3. Approved Knowledge

Curated memory that agents are allowed to use as durable context.

Every approved memory must retain provenance back to the trace, proposal, or source that created it.

### 4. Derived Indexes And Projections

Examples:

- text search
- vector search
- markdown wiki
- graph indexes
- summaries
- repo maps

These are derivative caches/views.

They are not the source of truth.

## Source Of Truth

The source of truth should be the trace plus approved memory ledger.

The source of truth is not:

- the vector database
- a wiki export
- a third-party memory system
- an embedding index
- an agent's current prompt context

Existing/open-source tools can be used as components, sidecars, indexes, or inspiration only if they preserve this source-of-truth boundary.

Short version:

```text
Own the ledger. Borrow the indexes.
```

## Current V0a

The current spike is:

```text
manual proposal -> approve/reject -> approved knowledge -> text search
```

This proves the approval-gated memory lifecycle before adding trace capture, provenance, MCP, REST, embeddings, graph memory, dashboard, or agent orchestration.

## Near-Term Direction

After V0a, prioritize the missing source-of-truth primitives before building broad integration surfaces:

1. raw trace/event foundation
2. provenance links from proposals and approved knowledge back to source material
3. proposal lifecycle refinements, including edit/supersede/defer
4. then MCP/API tools over the proven lifecycle

This keeps the project focused on replayable, auditable memory rather than drifting into a generic agent platform or opaque retrieval product.

For the detailed design doctrine and milestone framing, see [docs/architecture/memory-ledger-principles.md](docs/architecture/memory-ledger-principles.md).
