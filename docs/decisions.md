# Decisions

## 2026-07-12 — tool-response JSON uses camelCase (#31)

- JSON tool-response properties use the MCP SDK's camelCase convention; for
  example, `log_trace` returns `{traceUuid, sessionId}`. Conceptual and
  storage-layer names remain snake_case, including PostgreSQL columns such as
  `trace_uuid`.
- Chose the existing SDK default over switching serializer policy before
  v1.0.0. A switch would require custom configuration across every tool
  response and break existing clients even though the full surface and test
  suite already agree on camelCase, solely to resemble storage names clients
  never see.

## 2026-07-10 — retirement is provenance-carrying (#18; supersedes Slice 5 retirement decisions)

- Retirement is a distinct operator action, not a proposal review. It appends a
  `retirement` trace event under synthetic session `retirement:<memory_uuid>`;
  the target memory is the event's sole ref. Sessions describe individual
  actions, while refs connect the memory's history.
- `memctl retire <uuid> --by <name> --reason "..."` requires a named operator
  and non-blank reason. Unqualified names normalize to `human:<name>`; event
  content is `{operator, reason}`.
- Only `approved` memories may be retired, whether shared or private. Missing,
  proposed, rejected, superseded, or already-retired memories fail without a
  row change or trace append. Repeated retirement is an error, not an
  idempotent success.
- The guarded status transition, `retired_at`, and trace append are atomic.
  Rationale: retirement changes what the system presents as current knowledge;
  keeping only its timestamp discards actor and reason provenance that cannot
  be reconstructed later.

## 2026-07-10 — log_trace session selection (#17; amends Session 2 decision)

Supersedes the Session 2 line "`log_trace` keeps an explicit `session_id`
override (imports)". Considered: optional-with-override (DES-002 as filed),
capability-gated override, full removal. Chose **removal**:

- **`session_id` is removed from the `log_trace` input schema.** Session
  identity joins `agent_id` and namespace under the existing rule: server-
  derived, never trusted from tool arguments. Rationale: an optional override
  doesn't fix fragmentation for existing clients (they all send `session_id`
  because it was required — explicit-wins means they keep splitting their runs),
  and it lets any agent write events into any session in its namespace,
  polluting the session joins the provenance questions depend on. Removal makes
  fragmentation unexpressible rather than merely defaulted away.
- **Compatibility: a caller-supplied `session_id` is ignored** (SDK drops
  unmatched arguments; no custom validation). Every legitimate current caller
  invented its session id arbitrarily, so ignoring converges unmodified clients
  onto correct joinable behavior. The response makes the substitution
  observable rather than silent.
- **`log_trace` returns `{traceUuid, sessionId}`** — the server-derived
  session is echoed so agents can reference their own run (handoffs) and
  legacy callers can see what session their event actually landed in.
- **stdio default: unique per process.** When `MEMSRV_SESSION_ID` is unset,
  generate a fresh id at startup (was the constant `"local-session"`, which
  collapsed all unconfigured runs into one unbounded session). The env var
  stays as the explicit pin — over stdio the process launcher is the trusted
  identity source, same as for `agent_id`. HTTP behavior unchanged: transport
  session, fail loudly if `Mcp-Session-Id` is absent.
- **Imports get no agent-tool path.** Preserving an external session identity
  is operator tooling (precedent: memctl writes synthetic `review:<uuid>`
  sessions without touching `log_trace`), to be designed when #15 maps
  conversation capture. Evidence door: a real importer needing MCP-level
  session override is the trigger to revisit a capability-gated parameter.
- **Ordering: lands before the v1.0.0 tag** (Slice 8, #10) — pre-1.0 with only
  first-party clients is the cheap moment to break the tool contract.

## 2026-07-10 — memctl audit commands (Slice 5, #5; retirement bullets superseded by #18)

- `retire <uuid>` is a bare status flip (`status='retired'`, sets `retired_at`) —
  no `--by`, no trace event. Rationale: spec §9's signature is bare, and
  retirement is deliberately absent from the trace event taxonomy (§191) and the
  review-event convention (§6b). Retiring is not an adjudication; it just drops a
  memory from the default retrieval path (search already filters
  `status='approved'`). Don't "fix" this by inventing a retirement event — that's
  a taxonomy change, not this slice.
- `retire` has **no status guard** (`WHERE uuid = @Uuid` only): it will flip a
  `proposed`/`rejected`/`superseded` row too. Intentional — an operator may want
  to fully drop a superseded row from retrieval; the CLI shouldn't second-guess.
- `show` gained a `retired=` line so retirement is inspectable at the operator
  seam (supports the AC "the row remains queryable").
- `why` walks the `supersedes` chain backward and resolves each version's
  `source_id` to its trace only when `source_type='trace'` (only traces live
  in-DB; other source types print as `type:id`).

## 2026-07-09 — North star + Session 2 audit (wayfinder map #11)

- `docs/north-star.md` added (informational — never wins conflicts): identity
  (personal substrate, OSS-visible; agent-as-extractor, human-as-gate),
  evidence-door meta-rule, approval gate human with named reopen evidence,
  RLS consciously skipped with tripwires, tiering mechanics deferred on the
  stale-retrieval watch-list trigger, candidate horizons deliberately
  uncommitted. Full detail: distillation doc + wayfinder tickets #12/#13.
- Session 2 scope audited against the north star (#14): **confirmed** — no
  cuts, no reorders, no contradictions. Slice 4 (workstreams) consciously
  kept. One amendment: Slice 2 gains an acceptance criterion that all
  namespace/visibility checks flow through a single service-layer seam
  (RLS-ready). Slice execution is unblocked.

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
