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

Phase 1 is complete. `v1.0.0` is the first compatibility release: it serves
streamable HTTP MCP with bearer-key identity by default, retains stdio MCP for
trusted local agents, and includes the `memctl` operator CLI.

## Documents

| Doc | Role |
|---|---|
| [`docs/north-star.md`](docs/north-star.md) | What the mature system is (orientation; never wins conflicts) |
| [`docs/memory-server-phase1-spec.md`](docs/memory-server-phase1-spec.md) | **Binding** build spec, schema, tool contracts, Do-Not-Build list |
| [`docs/agent-memory-handoff-v4.md`](docs/agent-memory-handoff-v4.md) | Intent and architecture where the spec is silent |
| [`CONTEXT.md`](CONTEXT.md) | Domain vocabulary |
| [`docs/articles/`](docs/articles/) | The article series on agentic memory the design draws from |

## Connect an agent

Agents use MCP; they never receive an administrative database credential.
Choose HTTP for normal LAN use. Stdio is the trusted host-local option where a
Claude Code-spawned server process can reach PostgreSQL directly.

### Claude Code over HTTP

Ask the operator for your bearer key, export it before starting Claude Code,
and put this in the project `.mcp.json` (or the equivalent user-scoped MCP
configuration):

```sh
export OVERMIND_BEARER_KEY='<key provisioned for this agent>'
```

```json
{
  "mcpServers": {
    "overmind": {
      "type": "http",
      "url": "http://overmind.faviann.vms:8080/mcp",
      "headers": {
        "Authorization": "Bearer ${OVERMIND_BEARER_KEY}"
      }
    }
  }
}
```

The checked-in configuration contains only an environment-variable reference,
never the key itself. The day-1 endpoint is intentionally plain HTTP on the
trusted LAN. Run `/mcp` inside Claude Code to approve the project server and
check its connection.

### Claude Code over stdio

Stdio is restricted to trusted host-local use. Ask the operator to provision a
launcher at `/usr/local/bin/overmind-stdio` that starts the immutable image in
stdio mode and injects its runtime database credential *after* the consumer
process boundary. The launcher must not accept arbitrary arguments, expose its
environment, or write application logs to stdout.

Register that launcher as a user-scoped server:

```sh
claude mcp add --scope user --transport stdio overmind -- \
  /usr/local/bin/overmind-stdio
```

The Claude Code configuration contains neither a connection string nor a path
to a readable secret. The operator-owned launcher fixes the agent identity,
default namespace, and namespace allowlist; it is the privilege boundary, not
an agent-customizable convenience script. Run `claude mcp get overmind` to
inspect the saved consumer configuration.

## Operator path

Operator actions stay on the server host. SSH in, identify the service
container, and run the in-image CLI with its existing runtime environment:

```sh
ssh overmind.faviann.vms
docker ps --filter ancestor=ghcr.io/faviann/overmind:1.0.0

docker exec -it <overmind-container> memctl pending memory-system
docker exec -it <overmind-container> memctl show <proposal-uuid>
docker exec -it <overmind-container> \
  memctl approve <proposal-uuid> --by human:<name> --edit
```

The image includes `nano` for the interactive edit path. Approval, rejection,
retirement, audit, release, and migration remain operator-only; none is exposed
as an agent-facing MCP tool. See the
[deployment contract](docs/deployment-contract.md) for key-file provisioning,
the one-shot migration command, health semantics, and the complete runtime
contract.
