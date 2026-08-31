# Context — Overmind memory substrate

Glossary of domain terms. Implementation details live in `docs/`, not here.

## Terms

**Namespace** — the isolation unit for memories and traces. Every row belongs to
exactly one namespace. Isolation is enforced server-side from the caller's
identity, never trusted from tool arguments. Path-style names give hierarchy
(`homelab`, `repo/<owner>/<name>`) without schema support.

**Moraine** — the external system that owns durable external agent conversation
and session evidence. It is outside Overmind's server and database boundary;
Overmind does not duplicate its evidence store.

**Local Capture Proof** — the completed Moraine adoption milestone in which one
producer proved local capture, retention, inspection, and safe redaction of its
persisted harness evidence.

**Central Evidence Aggregation** — the prospective milestone for collecting
evidence from multiple local Moraine producers into a central long-term evidence
surface. It is not knowledge derivation or governance.

**Knowledge Provenance Integration** — the future boundary through which
Overmind may resolve and cite Moraine evidence when deriving governed knowledge.
It does not make Overmind an owner or duplicate store of that evidence.

**Agent identity (`agent_id`)** — who is acting. Derived by the server from the
connection (bearer key over HTTP, process config over stdio), never
self-asserted in tool arguments. It identifies the provisioned actor, not the
model or provider used for a particular event. Codex and Claude Code are
provisioned as separate actors even when the same person operates both.

**Bearer key** — a static credential identifying one agent identity over HTTP.
Each key maps to: one `agent_id`, one default namespace, and a list of allowed
namespaces. Keys are provisioning-owned (Ansible), not managed by the app.

**Default namespace** — the namespace a key's unqualified calls land in.
Calls naming a namespace explicitly are validated against the key's allowed
list.

**Trace session (`session_id`)** — the unit of replay: one contiguous agent
run. Server-derived, never trusted from tool arguments (same rule as agent
identity and namespace): the MCP protocol session over HTTP, process
configuration or a generated per-process id over stdio. Every event from one
run — agent-logged and server-logged alike — shares one session.

**Retrieval scope** — a versioned, operator-owned, read-only grouping of
namespaces. It expands a grouped retrieval while authorization still applies to
every member.

**Review session** — a synthetic session (`review:<proposal_uuid>`) carrying an
approval/rejection event. Its actor is the reviewer (`human:<name>`), never the
proposing agent. No anonymous reviews.

**Proposal** — a shared memory in `status='proposed'`; not yet trusted, not
retrieved by default. Becomes shared knowledge only through operator approval
(approve / edit-then-approve / reject), never through an agent-facing tool.

**Retirement** — an operator decision that withdraws a memory from normal
retrieval without deleting it. The memory and the provenance of its retirement
remain available for audit.
_Avoid_: Deletion, removal

**Private note** — a memory with `visibility='private'`: direct-write,
auto-approved, only ever retrieved by its owning agent. Still traced and
provenance-carrying.

**Workstream** — a unit of inflight work used for parallel-session
coordination. Lifecycle: open → checked_out → open | done | abandoned.
Checked out by exactly one agent at a time; only the owner checks in.
Checking in with status `open` is a handoff: the notes become the summary the
next agent starts from.

**Handoff** — a compact summary passed to a receiving agent, carrying
reference uuids; the full trace stays retrievable by reference, never inlined.
Created as a workstream in `open`.

**Canonical ledger** — traces + proposals + approved memories. Everything else
(FTS index, future vector index, exports) is a derived projection, rebuildable
from the ledger, never the only place truth exists.

**Write safety** — the generic never-store boundary on all memory and trace
writes. Memory writes reject matched secrets; trace writes persist only the
defined redaction marker.
