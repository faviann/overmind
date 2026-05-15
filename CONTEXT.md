# Cortex Memory

Cortex Memory is a local-first memory substrate for agent work. Its language distinguishes what happened, what was proposed from it, what was approved, and what is only a derived view.

## Language

**Approval**:
A review decision that a **Memory Proposal** is sufficiently supported by its **Source Event** to become **Approved Memory**.
_Avoid_: Text-only acceptance, blind approval

**Approval Event**:
A **Trace Event** that records an approval or rejection decision with explicit reviewer identity.
_Avoid_: Anonymous approval log

**Approved Memory**:
Durable knowledge that has passed **Approval** and may be used as trusted context by future agents.
_Avoid_: Note, embedding, search result

**Memory Proposal**:
A candidate memory awaiting review before it can become **Approved Memory**.
_Avoid_: Approved fact, note

**Proposal Inspection**:
A review step that presents a **Memory Proposal** together with its **Source Event** before **Approval**.
_Avoid_: Proposal list, full replay

**Provenance Status**:
A compact label that says whether a memory record has a linked **Source Event**.
_Avoid_: Confidence score, trust rating

**Replay View**:
A derived view that reconstructs surrounding session activity from **Trace Events**.
_Avoid_: Canonical memory, proposal inspection

**Source Event**:
The trace event that supports a **Memory Proposal**.
_Avoid_: Citation, attachment

**Transitional Provenance**:
A V0 rule where **Source Events** are expected and visibly reported, but not yet required for every **Approval**.
_Avoid_: Permanent optional provenance

**Trace Event**:
An append-only record of something that happened during agent, user, or tool work.
_Avoid_: Log line, transcript fragment

## Relationships

- A **Trace Event** may become the **Source Event** for one or more **Memory Proposals**.
- A **Memory Proposal** may have one **Source Event** in the current V0 model.
- A **Memory Proposal** becomes **Approved Memory** only through **Approval**.
- **Approval** requires the reviewer to have access to the **Source Event**.
- An **Approval Event** should not be created without reviewer identity.
- **Proposal Inspection** is distinct from listing proposals because it is the review-facing view of provenance.
- **Provenance Status** is visible during review and retrieval while **Transitional Provenance** is in effect.
- **Proposal Inspection** includes a single **Source Event**; a **Replay View** can later reconstruct nearby session activity from the trace ledger.
- **Transitional Provenance** allows older or manual **Memory Proposals** to be approved while making missing **Source Events** visible.

## Example dialogue

> **Dev:** "Can I approve this proposal just because the memory text looks right?"
> **Domain expert:** "No — in V0, **Approval** means the reviewer can inspect the **Source Event** and decide the **Memory Proposal** is supported well enough to become **Approved Memory**."

## Flagged ambiguities

- "approval" could mean accepting memory text alone or accepting memory text with source-event context — resolved: **Approval** requires source-event access, but not full session replay.
- "proposal list" could mean either a scannable queue or a review view — resolved: listing stays scannable, while **Proposal Inspection** shows the proposal with its **Source Event**.
- "source event required" could mean required immediately or eventually required — resolved: V0 uses **Transitional Provenance** until event-backed proposal creation is normal.
- "approval event" could mean any status change or a provenance-grade review record — resolved: an **Approval Event** requires explicit reviewer identity.
