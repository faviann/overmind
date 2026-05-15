# Memory Substrate Harness Seam

The project is memory-substrate-first, but not harness-blind. We explicitly defer
building a full agent harness while introducing a minimal harness seam: a small
trace/event contract that future tools and agents can write to. This prevents
premature orchestrator scope while preserving replayability, provenance, and
agent-to-agent debugging as first-class requirements.

## Decision

Build the memory substrate and harness seam now. Do not build a full harness,
runtime, scheduler, chat UI, provider router, workflow engine, or tool execution
orchestrator in this slice.

The memory substrate owns canonical traces, proposals, approvals, and
provenance. Future harnesses such as Pi.dev-style wrappers,
OpenClaw/Hermes-style orchestrators, Codex/Claude Code adapters, MCP tools, or
CLIProxyAPI-backed provider routing should be clients and producers of events,
not owners of memory truth.

## Boundary

Canonical source of truth:

```text
trace ledger + proposal ledger + approved memory ledger
```

Not canonical source of truth:

- vector database
- markdown/wiki export
- graph database
- external memory product
- future harness runtime
- chat transcript alone

Those systems may be projections, adapters, caches, clients, or producers.
Derived memory systems and indexes must be rebuildable from the canonical
ledger.

## Minimal Event Contract

A future harness or adapter should be able to append events with this conceptual
shape:

```json
{
  "event_id": "uuid",
  "session_id": "uuid-or-string",
  "project_id": "string",
  "agent_id": "string",
  "event_type": "user_message | agent_message | tool_call | tool_result | command_run | file_observed | file_modified | decision | error | memory_proposal_created | memory_approved | memory_rejected | memory_superseded",
  "timestamp": "iso-8601",
  "content": "string or structured payload",
  "metadata": {
    "repo": "optional",
    "branch": "optional",
    "cwd": "optional",
    "command": "optional",
    "tool_name": "optional",
    "tool_call_id": "optional",
    "files": [],
    "model": "optional",
    "provider": "optional",
    "exit_code": "optional",
    "duration_ms": "optional"
  }
}
```

The first implementation may keep `content` and `metadata` flexible while the
schema is still learning. It must not omit session identity, agent identity,
project or repo identity, event type, timestamp, or provenance linkability.

## Lifecycle Target

The preferred vertical slice is:

```text
agent/user/tool event
        |
        v
append-only trace ledger
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

The current manual proposal flow is a valid seed, but it must evolve toward
event-backed proposals. A slice that creates approved memories without source
events is architectural debt. A slice that captures trace but never links it to
proposals is incomplete.

## Minimal Operations

Near-term CLI/API surfaces should evolve toward these conceptual operations:

```bash
memory trace append --type tool_call --agent codex --session <id> --project <id> --json event.json
memory trace list --session <id>
memory proposal create --from-event <event_id>
memory proposal approve <proposal_id>
memory proposal reject <proposal_id>
memory search "query text"
memory export approved --format jsonl
memory export trace --session <id> --format jsonl
```

Do not implement the full surface at once. Prefer the next smallest vertical
slice:

```text
manual or synthetic event -> proposal linked to event -> approve/reject
-> approved knowledge linked to proposal/source event -> text search with provenance
```

## Invariants

- Trace capture is foundational, not a later plugin.
- Replayability, provenance, and approval lifecycle are non-negotiable.
- Approved memory must retain links back to proposals and source events.
- Future harnesses, MCP tools, adapters, and orchestration systems append events
  and consume approved memory through documented boundaries.
- Existing memory tools should be evaluated as projections or query engines, not
  canonical truth stores.

## Near-Term Deferral

Approval and rejection events are part of the event contract, but the next
proposal-review slice should not append `memory_approved` or `memory_rejected`
events until reviewer identity is explicit. Anonymous approval events would look
like stronger provenance than they are.
