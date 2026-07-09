# Decisions

## 2026-07-07 — Session 2 design (grilling session)

Scope confirmed as exactly spec §12 Session 2; Do-Not-Build stays binding.

**Transport & auth**

- One binary: `MemSrv.Server` becomes an ASP.NET Core app serving MCP over
  HTTP by default; a stdio mode flag keeps the local-agent path. Base image
  moves `runtime:10.0` → `aspnet:10.0`.
- Runtime contract: bind `0.0.0.0:8080`; MCP at `/mcp` (bearer-authenticated);
  `GET /healthz` unauthenticated, 200 only when the DB answers a cheap ping
  (`SELECT 1`, ~2s timeout). Day-1 agent URL:
  `http://overmind.faviann.vms:8080/mcp` — DNS, not IP; plain HTTP on the LAN.
  Traefik/TLS is a later, purely infra-side add-on (base-URL change for
  agents; no app or contract change).
- Bearer keys live in an Ansible-provisioned YAML file mounted into the
  container (path via env), plaintext (consistent with how the postgres
  password flows; vault owns the secrets). Each entry: key, `agent_id`,
  `default_namespace`, `allowed_namespaces`. Rotation = redeploy. No
  key-management code in the app.
- Namespace selection: unqualified calls land in the key's default namespace;
  qualified calls are validated against the allowlist. `log_trace` gains an
  optional namespace parameter.
- Session identity is transport-derived: one MCP protocol session = one trace
  session, so server-side causal logging never depends on agent cooperation.
  `log_trace` keeps an explicit `session_id` override (imports), but
  auto-logged events stay transport-scoped. Requires replacing the singleton
  `MemoryContext` with per-request context.

**Workstreams (tools 6–8)**

- `checkout_workstream` by title creates-if-missing; checkout of a
  checked-out stream errors naming the owner — no force-steal.
- `checkin_workstream` is owner-only and takes a status: `open` (notes become
  the handoff summary), `done`, or `abandoned`.
- `create_handoff` creates a workstream in `open` with summary + refs.
- Stale checkouts have no auto-expiry; an operator flips them via a small
  memctl command.

**memctl**

- Production path is SSH → `docker exec -it`; the image ships `nano` so
  `approve --edit` works interactively; `--content-file` added as the
  non-interactive alternative.
- Amended approvals use the version chain: the proposal row keeps its original
  content and flips to `superseded`; the amended text becomes a new
  `approved` row (`version+1`, `supersedes=proposal_uuid`). No
  `metadata.original_content` special case.

**Verification & release**

- Acceptance tests are an xunit class in `MemSrv.Tests` against the existing
  CI-provisioned `memory_test`; memctl-facing checks run the real CLI as a
  subprocess.
- First act of Session 2: seed the zeroth consumer — propose Session-1 and
  this session's decisions over the working stdio server with trace-backed
  sources (`source_type='trace'`, never bare human), approve via memctl.
- Session 2 completes Phase 1 → tag `v1.0.0`, replace the deployment
  contract's DEFERRED section with the final runtime values. The homelab-iac
  service stack (`stacks/overmind/server`) is follow-on infra work written
  against the updated contract, not part of this repo's session.

## 2026-07-07 — Production substrate and deployment contract

Recorded so the homelab infra PRD can converge without guessing (see
`docs/deployment-contract.md` for the machine-consumable contract):

- Production runs on a dedicated `overmind` LXC; Docker compose managed by
  Ansible in the homelab repo (`stacks/overmind/...` is the production source
  of truth, not this repo's `compose.yaml`).
- Postgres major pinned to **18** everywhere (dev, CI, prod); minor floats.
- Ansible owns the database, roles, secrets, backups, and migration
  invocation; the app owns schema content through its migrations.
- **Migrations never create roles.** `memsrv` must pre-exist (Ansible in prod,
  `docker/postgres-init/` in dev/CI). Rationale: the previous guarded
  `CREATE ROLE` produced a NOLOGIN dev role diverging from the Ansible-created
  LOGIN role; one provisioning path, dev mirrors prod.
- Admin path is Ansible → SSH → docker exec; consumers never get direct
  database access; normal service identity is `memsrv`; no `mem_readonly` for
  now.
- Image is `ghcr.io/faviann/overmind:<version>`, immutable tags from CI on
  `v*` git tags; `0.x` until Session 2 defines the service runtime contract.
- Connection-string env vars accept both `postgres://` URLs and Npgsql keyword
  strings — infra tooling speaks URLs, Npgsql speaks keywords.
- The service runtime contract (port, health endpoint, bind address) is
  deliberately **deferred to Session 2** rather than promised ahead of the
  HTTP transport existing.

## 2026-07-05 — Keep development databases disposable

Development runs against a disposable local PostgreSQL instance, with separate
`memory_dev` and `memory_test` databases. The production `memory` database on the
LXC is never used for development traffic because traces are intentionally
append-only and no table grants DELETE.
