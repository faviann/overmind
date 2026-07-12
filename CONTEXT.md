# Context — Overmind memory substrate

Glossary of domain terms. Implementation details live in `docs/`, not here.

## Terms

**Namespace** — the isolation unit for memories and traces. Every row belongs to
exactly one namespace. Isolation is enforced server-side from the caller's
identity, never trusted from tool arguments. Path-style names give hierarchy
(`homelab`, `repo/<owner>/<name>`) without schema support.

**Agent identity (`agent_id`)** — who is acting. Derived by the server from the
connection (bearer key over HTTP, process config over stdio), never
self-asserted in tool arguments.

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
run — agent-logged and server-logged alike — shares one session. Preserving an
external or historical session identity (imports) is an operator-path concern,
not an agent-tool capability.

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
