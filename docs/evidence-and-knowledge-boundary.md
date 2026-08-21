# Evidence, knowledge, and governance boundary

Status: **binding**. Recorded by the 2026-08-20 course-correction
([#203](https://github.com/faviann/overmind/issues/203), tracked for authority
cleanup by [#205](https://github.com/faviann/overmind/issues/205)). It
supersedes
[`conversation-capture-phase2-spec.md`](conversation-capture-phase2-spec.md) as
the authority over conversation capture.

This document states an ownership boundary. It is not a specification and it
does not design a replacement capture subsystem.

## The split

- **Evidence — what happened.** Durable capture and storage of external agent
  conversations and traces belong to Moraine. Moraine is the intended evidence
  substrate for Codex and Claude Code conversation history; adoption is tracked
  by [#206](https://github.com/faviann/overmind/issues/206) ("Adopt Moraine for
  Codex V1 evidence capture and historical backfill").
- **Knowledge — what was concluded.** Deriving propositions and facts from
  external evidence is a projection problem owned by Overmind, when and if that
  capability is built. Nothing here authorizes building it now.
- **Governance — what was decided.** Overmind/PostgreSQL remains authoritative
  for authored state: proposals, approvals, edits, rejections, supersession,
  retirement, and the durable history of those decisions.

A conversation is evidence, not governed knowledge. Two datastores are
acceptable only because they do not claim authority over the same fact: Moraine
owns what happened, Overmind owns what was concluded and governed.

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
- **The datastore rules still bind Overmind's own persistence.** Phase 1 spec
  §11 ("additional datastores") and §13 ("ClickHouse … never as the system of
  record") govern what Overmind stores: Overmind keeps one PostgreSQL database
  and gains no broker, cache, object store, or second database of its own.
  They are not a prohibition on an external system owning external evidence
  that Overmind does not store. Moraine is such a system, not a second
  datastore behind the Overmind server.
- **Repository or project association of a captured conversation is a derived
  classification**, not a canonical capture namespace decision for the current
  single-user milestone.
- **Existing Phase 2 capture code and documents are historical evidence.** They
  are not authority over the new direction, and they are not deleted before
  [#206](https://github.com/faviann/overmind/issues/206) proves the replacement
  path. Documents describing them stay available as provenance, marked frozen.

## Out of scope here

Moraine deployment and configuration, Moraine-to-Overmind projection,
provenance and citation identifiers, Codex hook capture, Git lifecycle
observation, and multi-user or multi-project security policy are all undecided
and are not authorized by this document.
