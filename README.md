# Overmind — memory server

A self-hosted **memory/control substrate for agents**: one .NET server
exposing a small MCP tool surface over one PostgreSQL database. It records
what was said, what was decided, what was merely proposed, what was approved,
what source produced a fact, what agent used it, and how to replay the chain.

Not another chatbot memory feature, and not flat vector RAG: the working
agent is the extractor, and a human is the quality gate. Nothing enters
shared memory unreviewed.

## Core commitments

- **Append-only trace ledger** — the raw record of what happened is
  immutable (enforced by grants *and* trigger) and primary; every summary or
  index is a derived, rebuildable projection.
- **Provenance on every fact** — identity, source, versioning, and causal
  ("what did the agent actually consume?") provenance are v1 schema, logged
  server-side, never dependent on agent cooperation.
- **Proposal → approval** — agents propose shared memories; only an operator
  approves (`memctl`, reviewer identity required). Private notes are
  direct-write and owner-scoped.
- **Two-step hybrid retrieval** — search returns previews + provenance
  (FTS + recency, RRF-fused, per-lane scores kept); full content is fetched
  by id, and that fetch is itself traced.
- **The server is the only door** — consumers get bearer keys, never
  connection strings; governance lives in code, not prompts.

## Status

Phase 1, Session 2 in progress — HTTP transport, coordination tools, and
`memctl` completion, releasing as `v1.0.0`. Session 1 (schema, stdio MCP,
retrieval, approval flow) is done.

## Documents

| Doc | Role |
|---|---|
| [`docs/north-star.md`](docs/north-star.md) | What the mature system is (orientation; never wins conflicts) |
| [`docs/memory-server-phase1-spec.md`](docs/memory-server-phase1-spec.md) | **Binding** build spec, schema, tool contracts, Do-Not-Build list |
| [`docs/agent-memory-handoff-v4.md`](docs/agent-memory-handoff-v4.md) | Intent and architecture where the spec is silent |
| [`CONTEXT.md`](CONTEXT.md) | Domain vocabulary |
| [`docs/articles/`](docs/articles/) | The article series on agentic memory the design draws from |

Consumer wiring instructions (Claude Code stdio, HTTP + bearer keys, operator
path) land with the `v1.0.0` release.
