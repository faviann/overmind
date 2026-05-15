# Memory Subsystem Next Steps

This document is the working plan for the next implementation slice. `MEMORY_PLAN.md` remains the larger strategic plan and research context.

The immediate target is **V0a: a tiny local vertical memory loop**.

```text
manual proposal -> approve/reject -> approved knowledge -> plain text retrieval
```

The goal is to prove the smallest useful behavior before adding MCP, REST, workers, embeddings, graph memory, dashboards, or production deployment.

## V0a Detailed Plan

### Objective

Build a local development loop where a human can manually propose a memory, approve or reject it, and retrieve approved knowledge for this repo.

V0a should answer one practical question:

```text
Can this project store an approved memory and retrieve it later with enough structure to build on?
```

### Scope

V0a includes:

- A minimal local Postgres-backed schema.
- A local development database setup owned by this repo.
- A small CLI for the memory loop.
- Manual proposal creation.
- Pending proposal listing.
- Approve/reject review actions.
- Approved knowledge creation from approved proposals.
- Plain text retrieval over approved knowledge.
- Namespace/type filters for retrieval.
- A first namespace for this repo, `repo/memorySubsystem`.
- A smoke test or scripted check proving the full loop.

V0a intentionally uses direct CLI-to-Postgres access. REST and MCP can wrap the same concepts later, after the behavior is proven.

### Data Model

Keep the schema small and boring:

- `namespaces`
- `memory_proposals`
- `knowledge_entries`

Minimum useful fields:

- stable IDs
- namespace
- memory/proposal type
- content
- status
- timestamps
- optional rationale or note
- optional source text for manual provenance

Do not model the full event log, session log, tool calls, handoffs, compaction boundaries, embeddings, tasks, or agent identities in V0a.

### CLI Commands

Implement only the commands needed for the vertical loop:

```text
memory init
memory propose --namespace repo/memorySubsystem --type decision --content "..."
memory proposals list --namespace repo/memorySubsystem
memory proposals approve <proposal-id>
memory proposals reject <proposal-id>
memory search --namespace repo/memorySubsystem --query "..."
```

The exact command names can change during implementation if the chosen CLI framework makes a slightly different shape cleaner, but the workflow must remain this small.

### Approval Rules

Approval is intentionally simple:

- Pending proposals are not returned by approved knowledge search.
- Rejected proposals are not returned by approved knowledge search.
- Approving a proposal creates an approved knowledge entry.
- V0a supports approve/reject only.
- If proposal content is wrong, reject it and create a better proposal.
- Do not implement edit-then-approve yet.

### Retrieval Rules

Retrieval is intentionally simple:

- Search only approved knowledge entries.
- Require or default to a namespace.
- Support a plain text query over title/content or content only.
- Support filtering by memory type if cheap to add.
- Do not use embeddings, pgvector, reranking, LLM retrieval, or context bundle generation in V0a.

### Infrastructure Boundary

Local development infrastructure can live in this repo so iteration stays fast.

Long-term production deployment does not belong here. The separate Ansible/Docker infra project should eventually own:

- LXC placement
- production container/service definition
- persistent volumes
- secrets
- backups
- network exposure
- health checks
- deploy/restart policy

This repo should eventually provide the app image/config contract and migrations that the infra project deploys.

### ADR Boundary

Do not create formal ADR files in V0a.

After the first vertical loop works, do a small decision harvest and formalize only the decisions that still feel true. Likely candidates later:

- local dev here, production deploy in the infra repo
- manual proposals before automatic extraction
- approval before durable memory
- plain text retrieval before embeddings
- CLI before REST/MCP

## Not In V0a

Do not build:

- MCP server
- REST API
- async worker
- automatic extraction
- session/message/event logging
- tool call provenance
- handoffs
- compaction
- embeddings or pgvector
- graph memory
- dashboard or TUI
- Discord/email/web/voice transports
- production LXC deployment
- formal ADR files
- autonomous approval

## Broad Next Steps After V0a

### 1. Local Dev Foundation

Add the minimal project structure, local Postgres setup, migrations, and CLI skeleton needed to run the V0a loop repeatedly.

### 2. Vertical Memory Loop

Implement proposal creation, pending proposal review, approve/reject actions, approved knowledge creation, and plain text search.

### 3. Smoke Test The Loop

Add one scripted or automated check that proves:

```text
create proposal -> approve proposal -> search returns approved knowledge
```

Also verify namespace isolation and that pending/rejected proposals do not appear in approved search results.

### 4. Deployment Handoff Shape

After V0a is useful locally, define the minimum contract the infra repo needs:

- service/container name
- app command
- environment variables
- database connection requirements
- migration command
- persistent data expectations
- health check
- backup expectations

Do not implement production deployment in this repo.

### 5. V0 Expansion Decision

Only after V0a is working, decide the next vertical slice. Likely options:

- REST API around the same operations
- MCP tools for agent access
- event/tool provenance logging
- richer namespaces and project registry
- proposal provenance sources

Choose one next slice at a time.
