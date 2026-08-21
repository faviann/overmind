# Conversation capture — Phase 2 authority

Status: **superseded — non-binding**. The 2026-08-20 course-correction (#203,
#205) moved durable capture of external agent conversations out of Overmind;
see [evidence-and-knowledge-boundary.md](evidence-and-knowledge-boundary.md),
which is the current authority. This document is retained as historical
evidence: it explains the capture code that still exists and the decisions
that produced it. Do not start new capture work against it, and do not treat
its Phase 1 amendments as still in force.

This document promotes the approved Phase 2 capture decisions into repository
authority. It is intentionally narrow. The Phase 1 spec continues to govern
the existing memory server, MCP tools, canonical ledger, and deployment
contracts except where this document explicitly amends it.

## Authority and scope

This document applies only to local Codex and Claude Code conversation capture
under the Phase 2 contract tracked by issue #73. Within that scope, it overrides
the conflicting `Do Not Build` entries in Phase 1 spec §11 only for the
additions named below. The Phase 1 spec wins everywhere else.

An implementation issue can select a smaller slice of this authority. It cannot
use this document to pull later capture capabilities into its own slice.

## Phase 1 contracts that remain binding

- The server is the only database door. The capture runtime and harness hooks
  receive no database connection.
- PostgreSQL remains the one datastore. Capture adds no broker, cache, object
  store, filesystem ledger, or second database.
- Traces and accepted capture history remain append-only. Recovery does not
  rewrite or silently fork accepted history.
- Existing MCP authentication, tool contracts, namespace isolation, memory
  governance, health behavior, migrations, stdio behavior, and stdout
  discipline remain compatible.
- Agent identity, capture source binding, capture credential, and interactive
  operator identity are separate authorities. Imported content supplies
  evidence and cannot grant any of them.
- Capture credentials cannot call MCP tools, perform operator actions, or read
  captured content. Agent bearer keys cannot import capture observations or
  perform capture-console actions.

## Approved Phase 2 additions

### Local capture runtime

Phase 2 may ship one Linux-first, headless OCI capture runtime per harness user.
It may discover and scan configured transcript trees, apply the local safety
gate, keep one durable capture queue, and send observations through the central
capture endpoint.

Startup and scheduled scans remain the scanner-first completeness path. Durable
queue responsibility and idempotent import receipts preserve convergence across
runtime restart, server restart, outage, and an ambiguous response.

The runtime receives configured read-only transcript and repository mounts and
one writable durable-state volume. It receives no Docker socket, privileged
mode, inbound host exposure, unrelated host access, operator credential, or
database connection. Images use immutable versions or digests; upgrades and
rollback remain external container operations.

Machine-owned configuration supplies mounts, durable state, server location,
and logging facts. Later server-owned capture policy does not move those host
facts into the console. Runtime logs are structured on stderr and contain no
transcript content, hook payload, credential, unsafe candidate, local
identifier, or complete capture request.

Temporary `memctl` enrollment is valid for the packaged catch-up baseline.
Browser enrollment, hooks, centralized health, and historical authorization
remain separate slices and are not prerequisites for that baseline.

### Harness hooks

Phase 2 may ship version-pinned Codex and, later, Claude Code hooks through each
harness's supported extension mechanism. Hooks call a bounded loopback-only
runtime endpoint and return successfully when the runtime is absent or slow.
They make no central server request. Scheduled transcript catch-up remains the
completeness mechanism.

### Focused capture console and OIDC

Phase 2 may add a focused capture console to the existing server for capture
enrollment, capture source binding and route policy, historical authorization,
content-free health, sanitized diagnostics, and recovery. This authorization
does not permit a conversation viewer or general administration dashboard.

Interactive capture-console actions use ASP.NET Core OIDC, with Authentik as
the first supported provider and the provider subject as the audited operator
identity. OIDC does not replace MCP bearer authentication or capture
credentials. `memctl` remains the break-glass operator path through the same
server-owned operations.

OIDC delivery may follow the packaged runtime and hook slices. Until OIDC is
implemented and fully configured, the capture console stays disabled; there is
no anonymous or bearer-key fallback.

An OIDC-capable server has three configuration outcomes:

1. All capture-console OIDC settings absent: the console is disabled and the
   existing HTTP server, MCP, capture ingestion, health, Compose, and release
   verification behavior remains available.
2. OIDC settings partially present or invalid: server startup fails with a
   configuration error that contains no secret value.
3. OIDC settings complete and valid: the console is enabled.

If the OIDC provider becomes unavailable, new sign-ins and session renewal
fail. An existing locally validated console session remains valid until its
fixed, non-sliding expiration; the server does not contact the provider on
every console request. Capture ingestion, MCP, and server health remain
available. Session expiration is documented and acceptance-tested by the
ticket that implements OIDC.

### Operations and historical capture

Capture runtimes keep outbound connections: they upload batches and
content-free health, then poll for predefined, bounded, idempotent capture
instructions. Instructions cannot execute arbitrary commands, access arbitrary
paths, edit host mounts, manage Docker, or grant authority.

Pre-enrollment history is inventoried locally without uploading transcript
content. Historical authorization is one audited approve-all or approve-none
decision for an identified inventory. Approved execution remains bounded and
isolated per capture source stream through the ordinary queue, safety, routing,
receipt, and checkpoint contracts.

## Scope fences

This authority does not add a general web dashboard, conversation viewer,
agent-facing operator action, anonymous console mode, second datastore, direct
database import, runtime self-update, Docker management, arbitrary remote
command execution, or Codex direct-event-stream implementation.

Production Claude Code delivery remains gated by successful Codex
qualification. Claude fixtures may continue to constrain the shared adapter
contract before that gate.
