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

## Core Design Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Interaction model | Pull-on-demand | Interruptions are friction; I engage when I'm ready |
| Validation UX | Light edit of pre-filled fields | Binary is too coarse, conversational is too slow |
| Prioritization output | Top 3 with reasoning | I pick; I may know something the agent doesn't |
| Task breakdown | On-demand | Unsolicited planning is an interruption |
| Metadata at validation | Due date (optional) + low/medium/high importance | Minimal input, maximum reliability |
| Primary capture channel | Discord #inbox (private) | Covers mobile and desktop, already in daily use |
| Processing model | Event-driven smart queue | Items are clean and ready before I open the agent |
| Task store | TBD | Deferred until v1 implementation |
| Agent interface | Provider-agnostic conversation | Claude/Codex/other; no lock-in |

## Versioned Roadmap

### v1 — Capture & Execute
The core loop.

- **Active capture**: Discord `#inbox` → event-driven LLM processing → smart queue
  - All messages in the channel are candidates (private channel, controlled access)
  - AI pre-processes each message: extracts intent, suggests title, infers priority
- **Passive capture**: Email monitoring → same unified queue, labeled by source
- **Validation**: Pull-on-demand agent conversation
  - Pre-filled fields (title, importance, due date)
  - Approve / light-edit / reject per item
- **Prioritization**: "What's next?" → top 3 tasks with reasoning
- **Task breakdown**: On-demand when facing a large task ("break this down for me")

### v2 — Clean Inbox
Focused entirely on email hygiene.

- Identify and action spam, ads, newsletters (unsubscribe, auto-archive, ignore)
- Surface emails that require a response
- Reduce inbox noise so passive capture in v1 stays signal-rich
- TBD: specific email actions and rules (to be defined before implementation)

### v3 — Agent Takes Action
Agent stops suggesting and starts doing.

- Draft email replies for my approval
- Create calendar blocks for tasks
- Auto-unsubscribe from flagged senders
- Open PR or issue drafts for dev tasks

### v4 — More Inputs + Review Cadences
Expand capture surface, add reflection.

- Voice notes → tasks (via Whisper when available)
- Calendar events that imply tasks → surfaced at validation
- Weekly summary: what got done, what slipped
- Retrospective input loop

### v5 — Smarter Context + Integrations
The intelligent layer.

- Calendar-aware prioritization ("you have 20 min, here's what fits")
- Learning loop: system learns what I actually complete vs. defer
- GitHub/Gitea integration: issue notifications → task queue
- Collaboration awareness: flags tasks blocking others, tracks follow-ups

## Non-Goals (for v1)

- This is not a team tool — single user only
- No proactive notifications or pings — pull-on-demand only
- No automatic task execution without my validation
- No calendar integration until v3+
- No energy/context-aware scheduling until v5
