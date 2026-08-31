# Context — Overmind memory substrate

Glossary of domain terms. Implementation details live in `docs/`, not here.

## Terms

Capture terminology anywhere in this glossary is historical/internal vocabulary
for inaccessible legacy ledger/core/schema residue. It grants no capability:
no actor can import, enroll, pair, route, authorize, or read capture state through
a packaged application. Current authority for capture is
[`docs/evidence-and-knowledge-boundary.md`](docs/evidence-and-knowledge-boundary.md),
which governs on any conflict. The memory, identity, and governance vocabulary
is unaffected and still binding.

**Namespace** — the isolation unit for memories and traces. Every row belongs to
exactly one namespace. Isolation is enforced server-side from the caller's
identity, never trusted from tool arguments. Path-style names give hierarchy
(`homelab`, `repo/<owner>/<name>`) without schema support.

**Moraine** — the external system intended to own durable capture and storage
of external agent conversation evidence. It sits outside Overmind's server and
database boundary: Overmind does not own that evidence going forward, and never
treats Moraine as one of its own datastores. What the split decides lives in
[`docs/evidence-and-knowledge-boundary.md`](docs/evidence-and-knowledge-boundary.md);
adoption is tracked by [#206](https://github.com/faviann/overmind/issues/206).

**Local Capture Proof** — the first Moraine adoption milestone: one producer
captures, retains, and exposes its own persisted harness evidence locally well
enough to prove the evidence replacement path. It does not include aggregation
across producers or Overmind consumption of that evidence.

**Central Evidence Aggregation** — collecting evidence from multiple local
Moraine producers into a central long-term evidence surface. It follows Local
Capture Proof and is not knowledge derivation or governance.

**Knowledge Provenance Integration** — the future boundary through which
Overmind resolves and cites Moraine evidence when deriving governed knowledge.
It does not make Overmind an owner or duplicate store of the cited evidence.

**Unscoped capture namespace (`capture/unscoped`)** — the legacy fallback
namespace stored when retired routing logic could not determine a repository or
semantic destination. It is an inert schema/bootstrap concept, not an available
write destination.

**Capture route** — the legacy ledger assignment of one captured source stream
to one namespace. The retained value explains existing core/schema relationships
only; no packaged interface can create, inspect, or change it.

**Capture route policy** — the legacy internal policy model that derived a route
from repository and directory evidence and constrained a source binding's
namespace. No operator command or packaged routing operation remains.

**Retrieval scope** — a versioned, operator-owned, read-only grouping of
namespaces. It expands a grouped retrieval while authorization still applies to
every member; it is never a capture route or write destination.

**Capture import capability** — the retired authority concept that once
permitted observations to enter the legacy ledger. It is no longer issued or
reachable through a packaged application; the name remains only to navigate
inaccessible core code.

**Capture source binding** — the legacy ledger record that associated a harness
installation, capture credential, harness kind, agent identity, and routing
metadata. No packaged enrollment, binding lifecycle, or source-management
interface remains.

**Capture credential** — the retired credential form formerly resolved for
capture import. It grants no current packaged authority, is not reserved by
agent-key provisioning, and remains only as an internal input needed by residue
tests pending contraction.

**Capture pairing request** — legacy short-lived coordination state retained in
the schema. No packaged pairing flow, console approval, polling capability, or
credential delivery path can reach it.

**Agent identity (`agent_id`)** — who is acting. Derived by the server from the
connection (bearer key over HTTP, process config over stdio), never
self-asserted in tool arguments. It identifies the provisioned actor, not the
model or provider used for a particular event. Codex and Claude Code are
provisioned as separate actors even when the same person operates both.

**Capture provenance** — legacy origin fields stored with inaccessible
observations and events: source binding, harness and version, provider/model
when exposed, source session/event identifiers, and adapter version.
Unavailable values remain unknown rather than inferred.

**Source observation** — one immutable record in the legacy internal capture
ledger. It may own several captured events, while each event retains exactly
one primary observation.

**Source record** — one harness-emitted unit before Overmind accepts it, such
as a persisted JSONL entry or one hook invocation. It is source material for an
observation, not itself part of the canonical ledger.

**Source locator** — the source-native identifier or verified position that
identifies one source record within a capture source stream. Its harness-specific
mechanics may vary, but network request and batch identities never substitute
for it.

**Capture source stream** — the legacy append-only internal sequence used to
group source observations and enforce source position. It is not a live import
channel.

**Capture checkpoint** — the legacy internal position through a stream's
contiguous accepted prefix. The retained core advances it atomically with
ledger rows and never across a failed or missing position.

**Import receipt** — the legacy internal result model for one source record. It
remains usable only at the authorized core/module test seam; no packaged
operator receipt command or public response exposes it.

**Content fidelity limit** — a deterministic property of source content that
prevents complete safe persistence, such as an accepted size ceiling or an
unsupported binary representation. Capture replaces the affected logical
field with an explicit omission rather than retaining a misleading fragment,
and may advance because retrying the same content under the same policy cannot
improve the result.

**Capture safety failure** — an operational failure of the pre-append safety
boundary, such as missing or invalid scanner rules or an internal scanner
error. No canonical observation is accepted and source progress does not
advance until the failure is repaired.

**Capture outcome projection** — the legacy content-free internal
health/fidelity model. It groups only closed reasons and bounded size bands and
contains no content, exact byte count, credential, locator, digest, or source
identity. No packaged diagnostics surface exposes it.

**Safe source payload** — the sanitized payload stored inside the inaccessible
legacy ledger after deterministic write safety. Recognized textual secrets are
replaced; no packaged capture write or read can reach the value.

**Incomplete source record** — a source record that may still be extended by
its harness, such as an unterminated final JSONL line in an active transcript.
It is deferred without advancing source progress.

**Malformed source record** — a legacy internal classification for terminal
records the retained core cannot interpret. Its persistence/omission semantics
remain tested only to protect the residue until contraction.

**Captured event** — an immutable event row in the inaccessible legacy ledger,
keyed to one source observation and deterministic part key. It is not a Phase 1
trace or a packaged replay artifact.

**Captured event envelope** — the legacy internal join model over one event and
its immutable observation provenance. No packaged wire response exposes it; it
remains solely for core/schema navigation and tests.

**Conversation reconstruction** — the retired capture read concept for
replaying legacy observations and provenance. No packaged replay or navigation
read exists; the definition survives only to interpret inaccessible internal
models.

**Event kind** — what a captured event represents, such as a message, tool
call, tool result, compaction, lifecycle event, annotation, or opaque record.
It is independent of the event's actor role.

**Actor role** — the source-stated producer or authority of an event, such as
user, assistant, system, developer, tool, harness, or operator. An unavailable
role remains unknown; capture does not infer one from the event kind.

**Source timestamp** — an optional raw and parsed timestamp stated for a source
observation. It is not silently promoted to the occurrence time of every event
derived from that observation.

**Occurrence time** — an optional timestamp explicitly stated for one semantic
event. When the source supplies no event-specific time, it remains unknown.

**Capture time** — the legacy ledger timestamp recording when an observation
was accepted. It is inert historical data, not evidence that an import
interface remains.

**Source order** — the exact order established within one verified capture
source stream, plus source-stated order among the semantic parts of one
observation. Cross-stream order exists only where explicit relationship or
timestamp evidence establishes it.

**Display order** — a derived merge of captured events for replay or
presentation. It may combine source order, relationships, and timestamps, but
it is not stored as canonical historical fact.

**Source relationship** — a typed relationship stated by a source observation.
It retains a sanitized source-native target identity and may resolve to an
Overmind trace or session at retrieval time; capture order does not determine
whether the relationship can be stored.

Distinct statements remain distinct relationships even when they name the same
native target. Parent, fork, spawn, and source-classification evidence never
becomes capture source-stream identity or a cross-stream ordering claim.

**Dangling relationship** — a source relationship whose stated target is not
present in the captured ledger. It differs from a legitimate root, for which
the source stated no parent. A dangling relationship is retained as evidence
of incomplete capture rather than dropped or repaired by inference.

**Captured session navigation** — the legacy authorization-aware read model
over stored stream identity and relationships. It has no packaged operator
command or server route; late ledger changes and authorization rules matter
only to internal residue awaiting contraction.

**Canonical event header** — the small relational portion of a captured event
containing stable, frequently queried invariants such as observation identity,
part key, kind, actor, session, source order, and occurrence time. Speculative
analytics dimensions do not become ledger columns merely because a future
projection may use them.

**Tagged event payload** — the versioned JSONB content whose contract is
selected by event kind. It holds kind-specific semantics while the safe source
payload preserves fields that have not earned canonical promotion; wide
analytics shapes remain rebuildable projections over the ledger.

**Context observation** — an immutable source-stated fact scoped to a session,
turn, event, or other source boundary, such as active model, provider, cwd, or
repository. Its scope and source evidence are stored rather than copied onto
events that did not state it.

**Resolved context** — a rebuildable retrieval projection that joins applicable
context observations to a captured event. It always reports scope and evidence,
never crosses a stream without an explicit relationship, and leaves ambiguous
values unknown; enriching it does not mutate the ledger event.

**Tool outcome** — the source-stated terminal state carried by a `tool_result`,
such as succeeded, failed, denied, interrupted, or unknown. Explicit failures
remain tool results rather than a separate event family; a call with no captured
result remains incomplete and is never converted into a failure by inference.

**Compaction operation** — a first-class harness operation that may have
request and completion events carrying source-stated trigger, instructions,
outcome, summary, and metrics. It reuses request/outcome vocabulary but is not
a tool call: its completion establishes a conversational context boundary and
never replaces or mutates the earlier trace history.

**Exposed reasoning** — reasoning or thinking content that a harness actually
persists and exposes. Capture preserves it as optional replay fidelity when it
passes the safety boundary, but never infers or decrypts hidden reasoning and
never makes durable decision provenance depend on its presence.

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
run — agent-logged and server-logged alike — shares one session. The
inaccessible legacy residue derived captured-session identity from stored
source, external-session, and optional subagent identifiers. No operator
import path remains; the original identifiers survive only as inert provenance.

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
