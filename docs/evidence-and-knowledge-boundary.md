# Evidence, knowledge, and governance boundary

Status: **binding**. Recorded by the 2026-08-20 course-correction
([#203](https://github.com/faviann/overmind/issues/203), tracked for authority
cleanup by [#205](https://github.com/faviann/overmind/issues/205)). It
superseded the former Phase 2 specification as the authority over external
conversation evidence.

This document states an ownership boundary. It is not a specification and it
designs no subsystem — but where it decides ownership it governs, and the specs
are read subject to those decisions.

## The split

- **Evidence — what happened.** Durable capture and storage of the
  conversations and traces that external agent harnesses emit belong to
  Moraine, the owner of external agent conversation evidence. The Local
  Capture Proof completed in
  [#206](https://github.com/faviann/overmind/issues/206). Overmind's own
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
  external agent evidence. External evidence production remains outside
  Overmind.
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
- **The transitional capture credential and identity-separation rule has
  lapsed.** It bound only the legacy public endpoints, which have been removed
  together with their domain and persistence substrate.
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
- **Retired Phase 2 material is historical evidence, not active authority.**
  The replacement path in [#206](https://github.com/faviann/overmind/issues/206)
  has been proven and the retired Overmind implementation has been removed.
  The superseded Phase 2 specification may be removed from the working tree;
  Git history and durable issues and pull requests are the preservation layer
  for that specification and the retired architecture. General write safety is
  retained independently under [`write-safety.md`](write-safety.md), because
  Phase 1 spec §5 requires its never-store boundary on every Overmind memory and
  trace write.

## Out of scope here

Central Evidence Aggregation, Knowledge Provenance Integration, Moraine
deployment and configuration, provenance and citation identifiers, Git
lifecycle observation, and multi-user or multi-project security policy are not
authorized by this document.
