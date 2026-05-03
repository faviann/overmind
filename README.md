# Cortex

> My external executive function. Captures everything, organizes nothing on my behalf, helps me execute.

## The Problem

I lose tasks because capturing them is too slow or too structured. By the time I've decided where something goes, the moment has passed — or I just don't bother. The result: things fall through the cracks, not because I don't care but because the system requires too much from me at capture time.

When tasks do make it in, I waste time deciding what to do next rather than just doing it.

## Why This Matters

I work better when I don't have to organize. The cognitive overhead of maintaining a task system is often larger than the value it provides. Cortex should invert that: capture is zero-friction, organization is automatic, and at any moment I can ask "what's next?" and get a clear answer.

## What Success Looks Like

- I can dump a thought in under 5 seconds from wherever I am (phone or desktop)
- I never open a task system to organize — only to validate or execute
- When I ask "what should I do right now?", I get 3 prioritized options with reasoning
- Tasks that come through email are surfaced to me without me having to read every email
- Any agent picking up this project can understand what to build and why without asking me

## Architecture

Cortex is built in tiers that are introduced progressively as the system matures.

### Tier 1 — Integration & Ingestion (n8n)
Self-hosted n8n handles all always-on integrations: Discord, email, and future sources. It watches for events, runs AI preprocessing (via external LLM APIs — OpenAI, OpenRouter, etc.), and stores structured items in a queue. n8n is the only process that runs continuously; it never requires Hermes to be event-driven.

n8n exposes its workflows as MCP tools so Hermes can query the queue on demand without a custom bridge.

### Tier 2 — Reasoning Engine (Hermes)
Self-hosted Hermes is the on-demand agent. It only activates when the user initiates a conversation. It connects to n8n via MCP to read the processed queue, then handles validation, prioritization, and task breakdown through its skills system. Hermes is never triggered externally — it is purely reactive to the user.

LLM provider is external and configurable (Claude, OpenAI, OpenRouter, etc.). No local model required.

### Tier 3 — Personal Knowledge Graph (OB1) — introduced in v3
OB1 is a centralized, vectorized memory layer that stores persistent facts about the user: financial posture, ongoing projects, constraints, values, and infrastructure knowledge. It is not a task queue — it is a profile of the user and their world that compounds over time.

Any agent, dashboard, or future tool connects to OB1 via MCP to contextualize its reasoning. Updating one fact (e.g. "money is no longer the constraint, time is") propagates to all consumers automatically. This makes Cortex's intelligence portable and avoids locking knowledge inside any single tool.

OB1 is deliberately deferred to v3. v1 and v2 must prove the core capture-and-execute loop works before introducing a cross-system knowledge layer.

## Core Design Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Interaction model | Pull-on-demand | Interruptions are friction; I engage when I'm ready |
| Validation UX | Light edit of pre-filled fields | Binary is too coarse, conversational is too slow |
| Prioritization output | Top 3 with reasoning | I pick; I may know something the agent doesn't |
| Task breakdown | On-demand | Unsolicited planning is an interruption |
| Metadata at validation | Due date (optional) + low/medium/high importance | Minimal input, maximum reliability |
| Primary capture channel | Discord #inbox (private) | Covers mobile and desktop, already in daily use |
| Processing model | Event-driven in n8n, pull-only for Hermes | n8n handles ingestion; Hermes only activates on user request |
| n8n→Hermes integration | n8n MCP server | Clean tool-based interface, no custom bridge code |
| LLM workload | External APIs (OpenAI, OpenRouter, etc.) | Self-hosted n8n and Hermes, external AI providers |
| Task store | TBD | Deferred until v1 implementation |
| Agent interface | Provider-agnostic conversation | Claude/Codex/other; no lock-in |
| OB1 timing | Introduced in v3 | v1–v2 prove the loop; v3 adds the knowledge layer when justified |

## Prioritization Criteria

When surfacing the top 3 tasks, the agent reasons over these factors:

| Factor | Source |
|--------|--------|
| Due date | Provided by user at validation (optional) |
| Importance | Provided by user at validation (low / medium / high) |
| Urgency | Inferred by agent — how time-sensitive is this right now |
| Impact on others | Inferred by agent — does someone else depend on this |
| Task impact | Inferred by agent — how much does completing this unblock or change things |

The agent must explain which factors drove each recommendation so the user can override with context the agent lacks.

## Versioned Roadmap

### v1 — Capture & Execute
**Stack: n8n + Hermes**

The core loop.

- **Active capture**: Discord `#inbox` → n8n watches channel → AI preprocessing (intent, title, priority) → structured queue
  - All messages in the channel are candidates (private channel, controlled access)
- **Passive capture**: Email monitoring via n8n → same queue, labeled by source
- **Validation**: Pull-on-demand agent conversation (Hermes via MCP to n8n queue)
  - Pre-filled fields (title, importance, due date)
  - Approve / light-edit / reject per item
- **Prioritization**: "What's next?" → top 3 tasks with reasoning per factor
- **Task breakdown**: On-demand when facing a large task ("break this down for me")

### v2 — Clean Inbox
**Stack: n8n + Hermes**

Focused entirely on email hygiene.

- Identify and action spam, ads, newsletters (unsubscribe, auto-archive, ignore)
- Surface emails that require a response
- Reduce inbox noise so passive capture in v1 stays signal-rich
- TBD: specific email actions and rules (to be defined before implementation)

### v3 — Agent Takes Action + Personal Knowledge Graph
**Stack: n8n + Hermes + OB1**

Two things happen in v3:

**Agent takes action** — stops suggesting, starts doing:
- Draft email replies for approval
- Create calendar blocks for tasks
- Auto-unsubscribe from flagged senders
- Open PR or issue drafts for dev tasks

**OB1 introduced** as the personal knowledge layer:
- Seed with current profile: projects, financial posture, constraints, goals
- Hermes queries OB1 via MCP to contextualize every session
- Updating a profile fact (e.g. spending posture, time availability) propagates to all reasoning automatically
- Foundation for cross-project awareness and multi-tool memory sharing

### v4 — More Inputs + Review Cadences
**Stack: n8n + Hermes + OB1**

Expand capture surface and add reflection.

- Voice notes → tasks (via Whisper when available)
- Calendar events that imply tasks → surfaced at validation
- Weekly summary: what got done, what slipped
- Retrospective input loop
- OB1 grows richer: infrastructure knowledge, cross-project relationships

### v5 — Smarter Context + Integrations
**Stack: n8n + Hermes + OB1**

The intelligent layer.

- Calendar-aware prioritization ("you have 20 min, here's what fits")
- Learning loop: system learns what I actually complete vs. defer
- GitHub/Gitea integration: issue notifications → task queue
- Collaboration awareness: flags tasks blocking others, tracks follow-ups
- Multiple agents or interfaces all read from OB1 as shared brain

## Non-Goals (for v1)

- This is not a team tool — single user only
- No proactive notifications or pings — pull-on-demand only
- No automatic task execution without my validation
- No calendar integration until v3+
- No energy/context-aware scheduling until v5
- No OB1 / personal knowledge graph until v3
