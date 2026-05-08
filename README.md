# Overmind High-Level Plan

> My personal orchestration layer. Captures intent, routes work, tracks commitments, and helps me execute across life, projects, infrastructure, and communication.

## 1. High-Level Objective

Overmind is a personal agent orchestration system designed to reduce cognitive load across scattered tasks, projects, messages, infrastructure work, and software development.

The goal is not to build one giant all-knowing assistant. The goal is to build a **thin but capable main brain** that understands my priorities, projects, routing map, approval policies, and current commitments, then delegates detailed work to the right tools, workflows, or project-local agents.

Overmind should help me answer:

```text
What should I do next?
What am I waiting on?
What needs my approval?
What changed in my projects?
What tasks are hiding in email, Discord, GitHub/Gitea, or calendar?
Can you route this request to the right place?
```

A representative request:

```text
“Can you update Jellyfin?”
```

Overmind should understand that Jellyfin belongs to my homelab infrastructure project, create or update a durable work item, dispatch the request to the relevant project agent or workflow, track the resulting PR/status, ask for approval before risky actions, and report back with outcome-level information.

Overmind should **not** personally absorb every Ansible role, Docker Compose file, Traefik label, GPU passthrough detail, or implementation issue.

## 2. Core Design Principle

> **Overmind tracks promises. Projects track work. PRs track change. Docs track knowledge. Deployments track reality.**

Or shorter:

> **Overmind knows the map. Project agents know the roads. The repo is the law.**

This avoids two bad extremes:

```text
Bad extreme 1:
One bloated main brain that knows every implementation detail.

Bad extreme 2:
Many project agents working independently with no shared visibility or accountability.
```

The desired middle ground:

```text
Overmind owns:
- intent capture
- routing
- prioritization
- cross-project awareness
- approval boundaries
- outcome tracking

Project agents own:
- implementation details
- repo-local context
- issue breakdowns
- code/config changes
- local task logs
- tests/checks
- PR creation

Source-of-truth systems own:
- Git history
- issues
- PRs
- docs
- deployment state
- calendar/email/message records
```

## 3. System Architecture

```text
User
 |
 v
Overmind / Main Brain
 - chat-facing orchestration agent
 - understands user priorities and active projects
 - routes requests to the right project/domain
 - tracks commitments, approvals, and outcomes
 - summarizes current state
 |
 +-- n8n / Integration Plane
 |   - Discord ingestion
 |   - email ingestion
 |   - calendar/event ingestion
 |   - GitHub/Gitea polling/webhooks
 |   - scheduled checks
 |   - cheap deterministic API glue
 |
 +-- Personal Ops Agent
 |   - email
 |   - calendar
 |   - reminders
 |   - follow-ups
 |   - daily planning
 |   - communication drafts
 |
 +-- Project Agents
 |   +-- homelab-infra agent
 |   +-- software-project agent
 |   +-- business/admin project agent
 |   +-- research agent
 |
 +-- Durable Work Systems
     - GitHub/Gitea issues
     - PRs
     - commits
     - repo docs
     - task logs
     - deployment records
```

## 4. Major Components

### 4.1 Overmind / Main Brain

Overmind is the user-facing orchestration layer.

It should know:

```text
- who I am
- what projects exist
- which project owns which service/system
- where to route work
- what requires approval
- what is active, blocked, pending, or done
- what matters today
```

It should not know:

```text
- every implementation issue
- every repo file
- every Docker label
- every Ansible variable
- every task-log detail
- every email thread forever
```

Overmind’s job is to coordinate, not execute everything.

Example responsibilities:

```text
- “Capture this as a task.”
- “Route this to the homelab-infra project.”
- “Ask Personal Ops to draft a reply.”
- “Check what PRs are waiting for me.”
- “Show me today’s top 3 priorities.”
- “Summarize what happened with Jellyfin.”
- “Ask for deploy approval.”
```

### 4.2 n8n / Integration Plane

n8n should handle cheap, deterministic, always-on work.

It is the plumbing layer, not the brain.

n8n handles:

```text
- polling email
- watching Discord channels
- ingesting calendar events
- watching GitHub/Gitea issues and PRs
- normalizing events
- tagging obvious metadata
- triggering safe workflows
- exposing structured data to agents
```

This avoids wasting LLM tokens on basic data fetching.

LLMs should be used for:

```text
- triage
- summarization
- prioritization
- routing
- drafting
- ambiguity resolution
- decision support
```

Not for:

```text
- polling APIs
- fetching every email
- repeatedly checking issue state
- moving structured data from A to B
```

### 4.3 Personal Ops Agent

Personal Ops is a cross-cutting domain agent, separate from Overmind.

It handles:

```text
- calendar
- email
- Discord/messages
- reminders
- follow-ups
- daily planning
- communication drafting
- lightweight personal admin
```

It may touch multiple projects, but it should not become the owner of deep project implementation context.

Good examples:

```text
“Do I have time today to work on Jellyfin?”
“Draft a reply to this client.”
“Remind me to review the Jellyfin PR tomorrow.”
“Find emails related to this project.”
“Prepare my morning brief.”
```

The split:

```text
Overmind:
What matters and who should handle it?

Personal Ops:
When, with whom, and what communication/follow-up is needed?

Project Agent:
How do we actually do the project work?
```

### 4.4 Project Agents

Each major project may have its own project-local agent or workspace.

Examples:

```text
homelab-infra agent
kinesolution-web-app agent
personal-agent-system agent
DND/session-notes agent
business/admin agent
```

Project agents should know their own repo deeply.

They handle:

```text
- reading repo-local docs
- using AGENTS.md / project instructions
- managing local issues
- breaking work into implementation slices
- editing files
- running checks/tests
- creating PRs
- updating project docs/runbooks
- writing task logs
```

They should not own:

```text
- global personal memory
- unrelated projects
- cross-project prioritization
- global calendar/email authority
- deployment approval policy
```

## 5. Source of Truth Rules

Overmind should not become the source of truth for everything.

Use this hierarchy:

```text
Git repo:
Source of truth for code, config, docs, runbooks.

Issues:
Source of truth for granular project work.

PRs:
Source of truth for proposed changes and reviewable implementation state.

Commits:
Source of truth for actual historical changes.

Task logs:
Source of truth for agent work sessions.

Overmind:
Source of truth for high-level intent, routing, active commitments, approvals, and outcomes.

n8n:
Integration/event transport, not long-term truth.

Memory:
Navigation and reasoning aid, not replacement for durable artifacts.
```

## 6. Memory Strategy

Do not start with a big central memory product as the core architecture.

Memory should be treated as a set of boundaries:

```text
Global / Overmind Memory:
- user preferences
- project registry
- routing map
- approval policies
- active commitments
- high-level project summaries

Personal Ops Memory:
- communication preferences
- recurring obligations
- follow-up patterns
- calendar/planning preferences

Project Memory:
- repo-local architecture
- conventions
- deployment process
- service maps
- known pitfalls
- project decisions

Task / Episode Memory:
- what happened during a work session
- commands run
- files changed
- blockers
- decisions
- next actions
```

The key anti-bloat rule:

```text
Project-specific implementation knowledge belongs in the project repo,
not in Overmind global memory.
```

Example:

```text
Good Overmind memory:
“Jellyfin is managed by homelab-infra.”

Good project memory:
“Jellyfin requires Intel iGPU passthrough and specific group permissions.”

Bad Overmind memory:
“The Jellyfin compose template currently maps /dev/dri using variable X in file Y.”
```

## 7. Project Registry

Overmind needs a lightweight registry of projects and ownership.

Example:

```yaml
projects:
  homelab-infra:
    description: "Infrastructure-as-code for homelab services"
    owns:
      - jellyfin
      - traefik
      - authentik
      - proxmox
      - docker stacks
    repo: "gitea-or-github-url"
    agent: "homelab-hermes"
    default_policy: "cautious-infra"
    approval_required_for:
      - deploy
      - restart-services
      - modify-secrets
      - destructive-changes

  personal-ops:
    description: "Calendar, email, reminders, communications"
    agent: "personal-ops-agent"
    approval_required_for:
      - send-email
      - delete-email
      - schedule-meeting-with-others
      - message-someone
```

The registry lets Overmind route without needing deep implementation context.

## 8. Work Tracking Model

Overmind should track outcome-level work, not every implementation issue.

Recommended hierarchy:

```text
Overmind Work Item
  Broad user intent and status

Project Issue / Epic
  Optional bridge inside the repo

Implementation Issues
  Granular vertical slices for project agents

PR
  Main reviewable artifact

Commits
  Actual implementation

Task Log
  Agent session history
```

Example:

```text
Overmind:
“Update Jellyfin”

Project issue:
“Support/update Jellyfin deployment in homelab-infra”

Implementation issues:
- Update image/tag policy
- Add Intel GPU support
- Update Ansible inventory
- Update Docker Compose template
- Add health check
- Document rollback

PR:
“Update Jellyfin deployment with Intel GPU support”

Overmind tracks:
- in progress
- PR opened
- checks passed
- needs review
- deploy approved
- deployed
- verified
```

## 9. PRs as the Main Bridge

PRs should be the primary bridge between project-local implementation and Overmind-level awareness.

Overmind does not need to inspect every issue. It mostly needs to know:

```text
- what PR exists
- what it claims to do
- whether checks pass
- whether review is needed
- whether deployment is pending
- whether user approval is required
- whether it was merged/deployed
```

Useful labels:

```text
overmind-track
needs-user
needs-review
deploy-ready
blocked
risk-high
decision-needed
project-memory-updated
```

Overmind can watch these labels through n8n.

## 10. Direct Project-Agent Interaction

The system should support two modes:

```text
Mode A — Orchestrated
User → Overmind → Project Agent → PR/status → Overmind → User

Mode B — Direct Project Work
User → Project Agent → repo/issues/PR/task logs
```

Direct project-agent interaction is allowed and desirable.

But it must not become invisible shadow work.

Rule:

```text
Any non-trivial direct project-agent session must leave a durable artifact:
- issue
- PR
- commit
- task log
- updated doc/runbook
```

Overmind does not need to be in every conversation, but it should be able to reconstruct what happened from durable artifacts.

## 11. Approval and Safety Policy

Overmind should enforce approval boundaries.

Safe to automate or semi-automate:

```text
- read inbox/calendar/issues
- summarize items
- classify tasks
- create draft tasks
- create draft issues
- inspect repos
- propose diffs
- run tests/checks
- draft emails
- draft PR descriptions
```

Requires approval:

```text
- send external messages
- send emails
- create calendar events with other people
- commit/push to important branches
- merge PRs
- deploy services
- restart production services
- modify secrets
- delete data
- unsubscribe from services
- make billing/financial changes
```

Extra cautious for:

```text
- homelab infrastructure
- authentication systems
- DNS/reverse proxy
- storage/backups
- user-facing services
- anything involving secrets
```

## 12. Example Flows

### 12.1 “Can you update Jellyfin?”

```text
1. User asks Overmind.
2. Overmind identifies Jellyfin as homelab-infra.
3. Overmind creates high-level tracked work item.
4. Homelab project agent receives task.
5. Project agent creates issue/branch as needed.
6. Project agent edits Ansible/Docker config.
7. Project agent opens PR.
8. Checks run.
9. Overmind sees PR is ready/deploy-ready.
10. User approves deploy.
11. Deployment workflow runs.
12. Health checks run.
13. Overmind reports outcome.
```

Overmind reports:

```text
Jellyfin update is complete.
PR merged.
Deployment succeeded.
Health check passed.
No follow-up required.
```

### 12.2 “What should I work on today?”

```text
1. n8n has collected relevant events:
   - inbox items
   - calendar events
   - GitHub/Gitea updates
   - Discord captures
   - pending approvals

2. Overmind asks Personal Ops for schedule constraints.
3. Overmind asks project/task sources for active work.
4. Overmind returns top 3 options with reasoning.
```

Output shape:

```text
1. Review Jellyfin deploy PR
   Reason: deploy-ready, blocks media stack update, low time requirement.

2. Reply to client email
   Reason: external dependency, waiting on you.

3. Continue Blazor scheduling app issue #42
   Reason: high-impact project work, no blocker, fits available time.
```

### 12.3 “Draft a reply and create a follow-up task”

```text
1. User sends request to Overmind.
2. Overmind routes to Personal Ops.
3. Personal Ops drafts reply.
4. Overmind asks for approval before sending.
5. On approval, message/email is sent.
6. Follow-up task is created.
7. Overmind tracks the commitment.
```

### 12.4 “Continue work on my Blazor scheduling app”

```text
1. Overmind identifies project.
2. Checks active PRs/issues.
3. Routes to project agent.
4. Project agent loads repo context.
5. Work continues from issue/PR/task log.
6. Project agent updates durable artifacts.
7. Overmind only tracks outcome-level status.
```

### 12.5 “Capture useful tasks from Discord”

```text
1. n8n watches selected Discord channel.
2. Messages are normalized into candidate items.
3. Overmind or Personal Ops reviews candidates on demand.
4. Approved items become:
   - personal tasks
   - project issues
   - follow-ups
   - ignored/archive
```

## 13. Morning Planning Use Case

The user should start the day with Overmind, not by manually choosing between sub-agents.

Overmind is the front door for questions like:

```text
“What should I work on today?”
“Help me plan my morning.”
“What needs my attention?”
“What can I realistically do before lunch?”
```

Overmind may delegate parts of the planning process to Personal Ops and project agents behind the scenes:

```text
Overmind asks Personal Ops:
- What is on the calendar?
- Are there meetings, deadlines, reminders, or follow-ups?
- How much focus time is available?

Overmind checks project/task systems:
- Which issues or PRs need the user?
- What is blocked?
- What is deploy-ready?
- What work is high-impact?

Overmind returns a grounded plan:
- top 3 priorities
- suggested time blocks
- pending approvals
- quick wins
- one recommended starting point
```

Personal Ops is a specialist, not the main entry point. The user can talk to Personal Ops directly when the task is purely calendar/email/comms, but normal morning planning should start at Overmind.

## 14. Phased Roadmap

### V0 — Repo Foundation

Goal: make the project understandable and iterable.

Deliverables:

```text
- project charter
- architecture overview
- terminology glossary
- project registry schema
- approval policy draft
- task artifact conventions
- example flows
- non-goals
```

No heavy automation yet.

### V1 — Capture and Prioritize

Goal: recreate the useful part of Cortex.

Stack:

```text
- n8n
- Discord private inbox
- email ingestion
- simple task queue/store
- Overmind chat interface, even if manual/prototype
```

Capabilities:

```text
- dump thoughts quickly
- ingest Discord/email candidates
- validate captured items
- ask “what should I do next?”
- return top 3 priorities with reasoning
```

No autonomous execution.

### V2 — Project Routing and Artifact Tracking

Goal: Overmind can route work to projects and track durable artifacts.

Capabilities:

```text
- project registry
- GitHub/Gitea issue integration
- PR tracking
- labels like needs-user/deploy-ready
- project ownership map
- high-level status summaries
```

Example:

```text
“Update Jellyfin”
→ routed to homelab-infra
→ issue/PR tracked
→ Overmind watches status
```

### V3 — Project-Local Agents

Goal: specialized agents can work inside repositories.

Capabilities:

```text
- repo-local AGENTS.md conventions
- task log conventions
- project agent profiles
- project-specific issue/PR workflow
- direct project-agent sessions
- Overmind artifact-level sync
```

Important rule:

```text
Project agents update project artifacts.
Overmind tracks outcomes.
```

### V4 — Personal Ops Agent

Goal: separate cross-cutting personal operations from Overmind.

Capabilities:

```text
- email triage
- reply drafting
- calendar-aware planning
- reminders
- follow-up tracking
- daily brief
```

Approval required for external actions.

### V5 — Safer Execution and Deployment

Goal: move from suggestions to controlled action.

Capabilities:

```text
- deploy workflows
- health checks
- rollback notes
- approval gates
- CI/CD integration
- service status reporting
```

For infrastructure:

```text
- propose changes automatically
- open PRs automatically
- run checks automatically
- deploy only with explicit approval or narrow policy
```

### V6 — Advanced Memory and Learning

Goal: introduce richer memory only after the workflow proves useful.

Research later:

```text
- memory framework/tool choice
- vector search
- repo indexing
- personal knowledge stores
- long-term preference learning
- cross-project semantic lookup
```

Do not choose this first.

## 15. Non-Goals for Early Versions

```text
- No fully autonomous god agent.
- No global memory system as the foundation.
- No silent production deployments.
- No replacing GitHub/Gitea issues.
- No replacing repo docs with chat memory.
- No requiring every agent to update a central memory after every action.
- No proactive notification spam.
- No complex multi-agent science project before the capture/routing loop works.
```

## 16. Recommended Initial Repo Structure

```text
overmind/
  README.md
  docs/
    00-charter.md
    01-architecture.md
    02-terminology.md
    03-project-registry.md
    04-approval-policy.md
    05-workflows.md
    06-memory-boundaries.md
    07-roadmap.md
    examples/
      update-jellyfin.md
      what-should-i-do-today.md
      draft-reply-followup.md
  schemas/
    project-registry.schema.yaml
    work-item.schema.yaml
    agent-report.schema.yaml
  decisions/
    ADR-0001-overmind-not-god-agent.md
    ADR-0002-n8n-as-integration-plane.md
    ADR-0003-prs-as-bridge.md
  backlog/
    v0-foundation.md
    v1-capture-prioritize.md
    v2-project-routing.md
```

## 17. First Implementation Target

The first practical target should not be “multi-agent autonomous infra deployment.”

It should be:

```text
I can dump tasks from Discord/email,
review them on demand,
ask what matters today,
and have Overmind route project-related tasks to the right repo/project.
```

A good first milestone:

```text
“Show me my current captured tasks and suggest the top 3 things to do today.”
```

Second milestone:

```text
“This task belongs to homelab-infra. Create a Gitea issue and track it.”
```

Third milestone:

```text
“Track PRs/issues labeled needs-user or deploy-ready and surface them to me.”
```

## 18. Final Architecture Summary

Overmind should start as a **personal orchestration and routing system**, not a giant autonomous assistant.

The recommended architecture is:

```text
n8n = integration and ingestion plane
Overmind = main brain / orchestrator
Personal Ops Agent = calendar/email/comms/follow-ups
Project Agents = repo-local implementation coworkers
GitHub/Gitea Issues = granular work tracking
PRs = reviewable change boundary
Repo docs/task logs = project memory
Global memory = thin routing/preferences/policy layer
```

The system succeeds if I can say:

```text
“Can you update Jellyfin?”
```

and Overmind can respond by routing the work to the right place, tracking the right artifact, asking for approval at the right time, and reporting the outcome — without becoming polluted by every implementation detail required to make Jellyfin actually work.
