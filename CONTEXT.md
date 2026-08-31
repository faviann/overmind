# Context — Overmind memory substrate

Glossary of domain terms. Implementation details live in `docs/`, not here.

## Terms

**Namespace** — the isolation unit for memories and traces. Every memory and
trace belongs to exactly one namespace; the server enforces access from caller
identity rather than trusting a tool argument.

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

**Agent identity (`agent_id`)** — who is acting. Server-derived and never
self-asserted, it identifies the provisioned actor rather than a model or
provider; Codex and Claude Code remain distinct provisioned actors even when
one person operates both.

**Bearer key** — a credential mapping one agent identity to its default
namespace and allowed namespaces. Provisioning owns its lifecycle, not the
application.

**Default namespace** — the namespace used by an unqualified call. An explicit
namespace still requires authorization.

**Trace session (`session_id`)** — the unit of replay: one contiguous agent
run. Derived from trusted transport or process context rather than caller
assertion, every event in the run shares that session.

**Retrieval scope** — a versioned, operator-owned, read-only grouping of
namespaces. It expands a grouped retrieval while authorization still applies to
every member.

**Review session** — a synthetic `review:<proposal_uuid>` session carrying an
approval or rejection event. Its actor is a named `human:<name>` reviewer,
never the proposing agent or an anonymous actor.

**Proposal** — a shared memory with `status='proposed'`, not yet trusted and
hidden by default. Operator approval or edit-then-approval is the only route to
shared knowledge; rejection leaves it rejected, and agents cannot approve it.

**Retirement** — an operator decision that withdraws a memory from normal
retrieval without deleting it. The memory and decision provenance remain
available for audit.
_Avoid_: Deletion, removal

**Private note** — a directly written, auto-approved memory visible only to its
owning agent. It remains traced and provenance-carrying.

**Workstream** — a unit of inflight work used for parallel-session
coordination with lifecycle open → checked_out → open | done | abandoned and
exactly one checkout owner. Only its checkout owner may check in; an open
check-in is a handoff whose notes become the next owner's summary.

**Handoff** — a compact summary passed to a receiving agent, carrying
reference uuids through an open workstream. The full trace remains retrievable
by reference and is never inlined.

**Canonical ledger** — traces, proposals, and approved memories. Everything
outside that ledger is a derived, rebuildable projection and never the sole
place truth exists.

**Write safety** — the generic never-store boundary on all memory and trace
writes. Memory writes reject matched secrets; trace writes persist only the
defined redaction marker.
