# Context — Overmind memory substrate

Glossary of domain terms. Implementation details live in `docs/`, not here.

## Terms

**Namespace** — the isolation unit for memories and traces. Every row belongs to
exactly one namespace. Isolation is enforced server-side from the caller's
identity, never trusted from tool arguments. Path-style names give hierarchy
(`homelab`, `repo/<owner>/<name>`) without schema support.

**Unscoped capture namespace (`capture/unscoped`)** — the fallback namespace
for a captured conversation whose repository or configured semantic route
cannot be determined. It records an unknown destination; it does not imply
that the conversation is personal context.

**Capture route** — the operator-owned assignment of a captured source session
to one namespace. It is fixed on first import and reused by all later catch-up.

**Capture route policy** — the operator-owned rules that derive a capture route
from repository and directory evidence, constrain the namespaces a capture
source binding may reach, and fall back to `capture/unscoped`. A route override
maps matching evidence to a semantic namespace; it is not a namespace alias.
Automatic repository routing uses the normalized `origin` remote only; other
remotes remain provenance unless an operator explicitly overrides the route.

**Retrieval scope** — a versioned, operator-owned, read-only grouping of
namespaces. It expands a grouped retrieval while authorization still applies to
every member; it is never a capture route or write destination.

**Capture import capability** — the operator-provisioned authority to append
canonical capture observations and their derived events. It is distinct from
an agent tool capability: imported payloads provide source evidence but cannot
expand the identity, session, or routing authority granted to the importer.
Live capture, catch-up, historical backfill, and recovery all use this same
capability rather than a privileged database or safety-gate bypass.

**Capture source binding** — the operator-provisioned association of one
harness installation with its capture credential, harness kind, agent identity,
and routing authority. Different harnesses and installations have distinct
bindings so their provenance, revocation, and session identities cannot collide.
Its stable identity survives credential rotation, upgrades, and explicit
single-installation recovery; a human-readable device/harness name is metadata.

**Capture credential** — a credential granting one capture source binding
access only to the capture import capability. It grants neither ordinary agent
tools nor human operator actions, and those other credential classes cannot use
it to import captured history. It may receive its own import receipts and, only
if required, its own operational stream status; it never grants captured-content
reads.

**Agent identity (`agent_id`)** — who is acting. Derived by the server from the
connection (bearer key over HTTP, process config over stdio), never
self-asserted in tool arguments. It identifies the provisioned actor, not the
model or provider used for a particular event. Codex and Claude Code are
provisioned as separate actors even when the same person operates both.

**Capture provenance** — origin information observed for an imported trace
event: the authenticated capture source binding, source harness and version,
provider and model when exposed, source session and event identifiers, and
capture-adapter version. It supplements agent identity; unavailable values
remain unknown rather than being inferred.

**Source observation** — one immutable hook invocation or persisted harness
record accepted by capture. One observation may yield multiple captured events,
but each captured event has exactly one primary source observation; correlated
observations are linked rather than merged.

**Source record** — one harness-emitted unit before Overmind accepts it, such
as a persisted JSONL entry or one hook invocation. It is source material for an
observation, not itself part of the canonical ledger.

**Source locator** — the source-native identifier or verified position that
identifies one source record within a capture source stream. Its harness-specific
mechanics may vary, but network request and batch identities never substitute
for it.

**Capture source stream** — the append-only sequence of source observations
for one trusted harness session or subagent. Native source identifiers locate
observations when available; otherwise a verified transcript position does,
and any changed prefix stops capture rather than creating a new stream history.

**Capture checkpoint** — the server-owned position through the contiguous
accepted prefix of one capture source stream. It advances atomically with an
observation and its derived ledger rows, and never advances across a failure or
unaccepted gap.

**Import receipt** — the server's per-source-record outcome within a delivery
batch, distinguishing newly accepted, already accepted, failed, and blocked by
an earlier stream gap. It reports the effective namespace and route basis;
delivery-batch boundaries do not affect ledger identity.

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

**Safe source payload** — source content after the universal deterministic
pre-append gate. Recognized textual secrets are replaced even when supplied by
the user; the payload never retains the matched value or a reversible
fingerprint of it.

**Incomplete source record** — a source record that may still be extended by
its harness, such as an unterminated final JSONL line in an active transcript.
It is deferred without advancing source progress.

**Malformed source record** — a terminal source record that capture cannot
interpret under its observed source variant. Capture preserves it as an opaque
event when it can be scanned safely, otherwise records an explicit omission,
then advances with a visible warning.

**Captured event** — a trace event imported from one source observation into
the canonical ledger. Each event has a deterministic part key within its source
observation. Conversation and tool records drive replay, duplicate UI or
lifecycle views are annotations, and unsupported records remain opaque,
redacted-safe events.

**Captured event envelope** — the self-contained retrieval view assembled from
one captured event and its source observation. Storage remains normalized so
capture provenance has one authoritative home; the wire response joins only
immutable ledger provenance to the event's semantic payload and source-stated
relationships. Resolved context, relationship targets, and display order are
future read models, not part of the canonical envelope.

**Conversation reconstruction** — replay of the captured conversation, its
source observations, and the provenance of any attachment. It does not promise
byte-perfect retention of opaque binary attachments or reconstruction of
provider-internal media preprocessing.

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

**Capture time** — the required server timestamp at which Overmind accepted a
source observation. It records ingestion, not when the represented activity
occurred.

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

**Dangling relationship** — a source relationship whose stated target is not
present in the captured ledger. It differs from a legitimate root, for which
the source stated no parent. A dangling relationship is retained as evidence
of incomplete capture rather than dropped or repaired by inference.

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
run — agent-logged and server-logged alike — shares one session. Preserving an
external or historical session identity (imports) is an operator-path concern,
not an agent-tool capability. A captured session is derived from its trusted
capture source, external session identifier, and optional subagent identifier;
the original source identifiers remain capture provenance.

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
