# Cortex Memory

Cortex Memory is a local-first memory substrate for agent work. Its language distinguishes what happened, what was proposed from it, what was approved, and what is only a derived view.

## Language

**Approval**:
A review decision that a **Memory Proposal** is sufficiently supported by its **Source Event** to become **Approved Memory**.
_Avoid_: Text-only acceptance, blind approval

**Review Event**:
A **Trace Event** that records an approval or rejection decision with explicit reviewer identity.
_Avoid_: Anonymous approval log

**Reviewer Identity**:
The actor identity recorded on a **Review Event** to explain who or what made the review decision.
_Avoid_: User account, auth identity

**Typed Actor String**:
A lightweight actor identifier shaped as `type:name`, such as `human:faviann` or `agent:codex`.
_Avoid_: Bare username, authenticated principal

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

**Review Provenance**:
The review-side provenance showing who reviewed a **Memory Proposal** and which **Review Event** recorded the decision.
_Avoid_: Authorization, reviewer profile

**Replay View**:
A derived view that reconstructs surrounding session activity from **Trace Events**.
_Avoid_: Canonical memory, proposal inspection

**Source Event**:
The trace event that supports a **Memory Proposal**.
_Avoid_: Citation, attachment

**Synthetic Review Session**:
A V0 trace convention that records review decisions under `review:<proposal_id>` instead of reusing the **Source Event** session.
_Avoid_: User session, full review workflow

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
- A **Review Event** should not be created without reviewer identity.
- **Review Events** cover both approval and rejection outcomes.
- Approving or rejecting a **Memory Proposal** requires **Reviewer Identity** once **Review Events** are introduced.
- **Reviewer Identity** is required for **Review Events** but does not imply user accounts or authentication in V0.
- **Reviewer Identity** is stored on reviewed ledger rows and in the corresponding **Review Event**.
- **Reviewer Identity** uses a **Typed Actor String** in V0.
- A **Review Event** uses a **Synthetic Review Session** so the review decision is not misattributed to the **Source Event** actor.
- Review commands return the created **Review Event** identifier so the decision can be audited immediately.
- **Review Provenance** is visible in proposal review outputs and approved-memory retrieval.
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
- "approval event" could mean any status change or a provenance-grade review record — resolved: use **Review Event** as the umbrella term, and require explicit reviewer identity.
- "reviewer identity" could mean authenticated user account or a simple recorded actor string — resolved: V0 uses **Reviewer Identity** as a recorded actor string, not an auth system.
- "reviewer" could mean a bare username or a typed actor — resolved: V0 uses a **Typed Actor String** such as `human:faviann`.
- "required reviewer" could mean optional with a local default or explicit input — resolved: approving or rejecting requires explicit **Reviewer Identity**.
- "where reviewer lives" could mean rows or events only — resolved: **Reviewer Identity** is stored both on rows for direct queries and in **Review Events** for replay/audit.
- "approval event session" could mean reusing the source session or recording a separate review context — resolved: V0 uses a **Synthetic Review Session** as a trace convention, not a permanent review workflow model.
