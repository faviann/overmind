# Evidence, knowledge, and governance boundary

Status: **binding**. Recorded by the 2026-08-20 course-correction
([#203](https://github.com/faviann/overmind/issues/203), tracked for authority
cleanup by [#205](https://github.com/faviann/overmind/issues/205)). It
supersedes
[`conversation-capture-phase2-spec.md`](conversation-capture-phase2-spec.md) as
the authority over conversation capture.

This document states an ownership boundary. It is not a specification and it
designs no subsystem — but where it decides ownership it governs, and the specs
are read subject to those decisions.

## The split

- **Evidence — what happened.** Durable capture and storage of the
  conversations and traces that external agent harnesses emit belong to
  Moraine, the intended substrate for external agent conversation evidence,
  Codex first; adoption is tracked by
  [#206](https://github.com/faviann/overmind/issues/206) ("Adopt Moraine for
  Codex V1 evidence capture and historical backfill"). Overmind's own
  append-only `traces` ledger is not that evidence and is unaffected: it stays
  in Overmind's database, joinable against memories.
- **Knowledge — what was concluded.** Deriving propositions and facts from
  external evidence is a projection problem owned by Overmind, when and if that
  capability is built. Nothing here authorizes building it now.
- **Governance — what was decided.** Overmind/PostgreSQL remains authoritative
  for authored state: proposals, approvals, edits, rejections, supersession,
  retirement, and the durable history of those decisions.

A conversation is evidence, not governed knowledge. Two datastores are
acceptable when they do not claim authority over the same fact: Moraine owns
what happened, Overmind owns what was concluded and governed.

## What this decides

- **Overmind does not own duplicate canonical conversation storage** for
  external agent evidence. No new capture implementation inside Overmind is
  justified by the existence of Overmind capture code alone.
- **Phase 1 memory and governance invariants remain binding** where they still
  apply: one datastore for Overmind's own state, append-only traces, provenance
  on memories, proposal→approval for shared memories, the server as the only
  database door, and the Phase 1 spec §11 `Do Not Build` list. Capture-specific
  Phase 2 amendments to those invariants do **not** remain binding merely
  because they were implemented.
- **No capture term or capture document grants authority to build.** The
  Phase 2 capture authorizations are withdrawn (Phase 1 spec §11). Where the
  frozen capture vocabulary restates one of the invariants above, the binding
  force is Phase 1's, not the capture term's.
- **Credential and identity separation remains binding** for the capture
  endpoints that still ship. A capture credential grants no MCP tool access, no
  operator action, and no read of captured content; an agent bearer key imports
  no capture observations and performs no capture-console action. Agent
  identity, capture source binding, capture credential, and interactive
  operator identity stay separate authorities, and imported content grants
  none of them. This is a preserved authorization invariant over deployed
  endpoints; it authorizes no new capture work.
- **The datastore rules still bind Overmind's own persistence.** Phase 1 spec
  §11 ("additional datastores", naming ClickHouse) and §13 ("ClickHouse …
  never as the system of record") govern what Overmind stores: Overmind keeps
  one PostgreSQL database and gains no broker, cache, object store, or second
  database of its own. They are not a prohibition on an external system owning
  external evidence that Overmind does not store. Moraine — ClickHouse-backed —
  is such a system, not a second datastore behind the Overmind server.
- **Repository or project association of a captured conversation is a derived
  classification**, not a canonical capture namespace decision for the current
  single-user milestone.
- **Existing Phase 2 capture code and documents are historical evidence.** They
  are not authority over the new direction. Per
  [#203](https://github.com/faviann/overmind/issues/203)'s recorded guardrails,
  that code is not deleted before
  [#206](https://github.com/faviann/overmind/issues/206) proves the replacement
  path, and documents describing it stay available as provenance, marked
  frozen. One exception to the freeze: the capture safety budgets and
  never-store rule set (`capture-safety-budgets.md`) remain binding, because
  Phase 1 spec §5 cites them for the never-store gate on every Overmind write
  path.

## Out of scope here

Moraine deployment and configuration, Moraine-to-Overmind projection,
provenance and citation identifiers, Codex hook capture, Git lifecycle
observation, and multi-user or multi-project security policy are all undecided
and are not authorized by this document.
