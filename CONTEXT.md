# Context — Overmind memory substrate

Glossary of domain terms. Implementation details live in `docs/`, not here.

## Terms

**Namespace** — the isolation unit for memories and traces. Every record belongs
to exactly one namespace, selected and authorized from the caller's identity.

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

**Agent identity (`agent_id`)** — who is acting. The server derives it from the
connection; tools never accept it as a self-asserted argument.

**Bearer key** — a provisioned credential identifying one agent identity over
HTTP and defining its default and allowed namespaces.

**Default namespace** — the namespace used by an unqualified call. An explicit
namespace is still checked against the caller's allowed set.

**Trace session (`session_id`)** — one contiguous agent run and the unit of
replay. The server derives it from transport or process context, never from a
tool argument.

**Retrieval scope** — a versioned, operator-owned, read-only grouping of
namespaces. Authorization still applies to every member.

**Review session** — a synthetic session (`review:<proposal_uuid>`) containing
an approval or rejection event attributed to its reviewer.

**Proposal** — a shared memory awaiting operator approval. It is not retrieved
by default and becomes shared knowledge only through operator review.

**Retirement** — an operator decision that withdraws a memory from normal
retrieval without deleting it. The memory and decision provenance remain
available for audit.
_Avoid_: Deletion, removal

**Private note** — a memory visible only to its owning agent. It is still
traceable and provenance-carrying.

**Workstream** — a unit of inflight work used for parallel-session
coordination. It moves through open, checked-out, done, or abandoned states and
has at most one checkout owner.

**Handoff** — a compact summary and reference set passed through an open
workstream. Full supporting traces remain retrievable by reference.

**Canonical ledger** — Overmind's traces, proposals, and approved memories.
Indexes and exports are rebuildable projections, never the only place truth
exists.

**Write safety** — the generic never-store boundary on all memory and trace
writes. Memory writes reject matched secrets; trace writes persist only the
defined redaction marker.
