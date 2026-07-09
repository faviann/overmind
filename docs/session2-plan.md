# Session 2 — implementation plan

Executes the Session 2 scope of `memory-server-phase1-spec.md` §12 under the
decisions recorded in `docs/decisions.md` (2026-07-07, grilling session).
Precondition: homelab-iac postgres/bootstrap work on the overmind LXC is done.
Scope is exactly the spec list; the Do-Not-Build list stays binding.

## 0. Seed the zeroth consumer (first act, before new code)

Over the existing stdio server: log this repo's build decisions as traces,
then `propose_memory` each Session-1 and grilling-session decision into
namespace `memory-system` with `source_type='trace'` pointing at those trace
uuids (seeding discipline — never a bare human source). Approve via
`memctl approve --by human:faviann`.

## 1. Per-request context refactor

`MemoryContext` is a process-wide singleton (`src/MemSrv.Core/MemoryContext.cs`,
registered in `src/MemSrv.Server/Program.cs`) — impossible under HTTP.

- Replace with a per-request context carrying `agent_id`, default namespace,
  allowed namespaces, and the transport-derived `session_id`.
- stdio mode: built once from env (current behavior, allowed = default).
- HTTP mode: built per MCP session from the bearer key entry + MCP session id.
- Namespace validation moves into `MemoryService`: qualified calls checked
  against the allowlist; `log_trace` gains an optional `namespace` parameter
  defaulting to the key's default namespace.

## 2. HTTP transport + bearer keys

- `MemSrv.Server` becomes an ASP.NET Core app. Default mode: streamable HTTP
  MCP at `/mcp` on `0.0.0.0:8080`; `--stdio` (or `MEMSRV_TRANSPORT=stdio`)
  keeps the local path. Dockerfile base moves `runtime:10.0` → `aspnet:10.0`.
- Key file: Ansible-provisioned YAML mounted into the container, path via env
  (e.g. `MEMSRV_AGENT_KEYS_PATH`); plaintext entries:
  `{key, agent_id, default_namespace, allowed_namespaces[]}`.
- Auth middleware on `/mcp`: unknown/missing key → 401; key resolves to the
  request context of step 1.
- `GET /healthz`, unauthenticated: 200 only if `SELECT 1` answers within ~2s.
- Session identity: one MCP protocol session = one trace session; all
  server-side auto-logging (`memory_consumed`, etc.) uses it.

## 3. Coordination tools 6–8

Against the existing `workstreams` table:

- `list_workstreams {namespace, status?}`
- `checkout_workstream {uuid | title}` — create-if-missing on title; conflict
  on a checked-out stream errors naming owner agent + session; no force-steal.
- `checkin_workstream {uuid, status: open|done|abandoned, notes, refs?}` —
  owner-only; `open` checkin notes are the handoff summary.
- `create_handoff {namespace, summary, refs}` — workstream born `open`.
- All four log trace events (`workstream_checkout`, `workstream_checkin`,
  `handoff`); all responses carry the `next` hint.

## 4. memctl completion

Existing commands: `migrate, pending, show, approve, reject, trace`
(`src/MemCtl/Program.cs`). Add:

- `retire <uuid>`
- `why <uuid>` (memory → source trace chain)
- `consumed <session_id>`
- `approve --edit` ($EDITOR, default nano — add nano to the runtime image) and
  `--content-file <path>` for non-interactive amendment. Amendment shape:
  proposal row keeps original content, flips to `superseded`; amended text is
  a new `approved` row (`version+1`, `supersedes=proposal_uuid`); approval
  trace event records `amended: true` per the review event convention.
- `workstream release <uuid>` (operator unlock for stale checkouts).

## 5. Acceptance tests

`AcceptanceTests` xunit class in `tests/MemSrv.Tests`, same CI-provisioned
`memory_test`: seed one scenario, then assert spec §10's four provenance
questions as queries plus the mechanical checks (append-only via trigger AND
grants, key auth, namespace isolation, private invisibility, born-proposed,
never-store reject/redact split, lane scores, content_hash, `--by` required,
review-session convention, `--edit` preserves original + `amended: true`).
memctl-facing checks run the published CLI as a subprocess.

## 6. Contract, docs, release

- Replace the DEFERRED section of `docs/deployment-contract.md` with the final
  runtime values (8080, `/mcp`, `/healthz` semantics, key-file mount, day-1
  URL `http://overmind.faviann.vms:8080/mcp`).
- README: wiring instructions for consumers (Claude Code stdio config, HTTP +
  bearer key config, memctl operator path via SSH → docker exec).
- Tag `v1.0.0` → CI publishes `ghcr.io/faviann/overmind:1.0.0`.
- Follow-on (homelab-iac, not this repo): `stacks/overmind/server` compose
  written against the updated contract; Traefik/TLS remains an optional later
  infra add-on.
