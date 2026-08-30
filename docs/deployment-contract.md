# Deployment contract (consumed by homelab-iac)

What the homelab repo may rely on when deploying this application. Anything not
stated here is not a contract. Every section is now **FINAL**: the Session 2
HTTP transport has landed, so the service runtime shape (port, health, bind
address, key file) is defined below rather than deferred.

The capture material in this document — capture schema verification, the
capture-console OIDC variables, the `/capture/console`, pairing, and observation
endpoints, and the per-user Codex catch-up runtime — describes capture code
and deployment surface that already ship. It stays accurate because that code is
still deployed, and it is frozen: it authorizes no new capture work. Current
authority for capture is
[evidence-and-knowledge-boundary.md](evidence-and-knowledge-boundary.md).

## Image — FINAL

- `ghcr.io/faviann/overmind:<version>` — immutable tags, published by CI on git
  tags `v*` (tag `v0.3.0` → image `0.3.0`).
- `v1.0.0` is the first compatibility release for the complete contract in this
  document. Pre-1.0 (`0.x`) tags promise only the migration contract that
  accompanied that tag. No `latest` tag is published.
- The image contains the memory server runtime (`MemSrv.Server`, streamable
  HTTP by default and stdio on request) and the operator/migration CLI
  (`memctl`, on `PATH`), with migrations baked in at `/app/migrations`.

## Reference Compose deployment — FINAL

The repository's default `compose.yaml` is the canonical, portable reference
for the complete application topology. With a configured `.env` and local
bearer-key YAML, this is the entire convergence procedure:

```sh
docker compose up -d --wait
```

The dependency chain is PostgreSQL healthy → provisioning completed → schema
migration completed → HTTP server. The provisioning container is the role and
database owner for this deployment mode: it idempotently creates the `memsrv`
LOGIN role and `memory` database when absent, and always converges the configured
`memsrv` password. The migration container remains the schema owner and never
creates or manages roles.

Reference Compose inputs:

| Variable | Requirement | Purpose |
| --- | --- | --- |
| `OVERMIND_VERSION` | Required | Immutable `ghcr.io/faviann/overmind` release version; there is no `latest` fallback. |
| `POSTGRES_ADMIN_PASSWORD` | Required | PostgreSQL administrative credential used by PostgreSQL, provisioning, and the one-shot migration. |
| `MEMSRV_PASSWORD` | Required | Runtime credential converged onto the `memsrv` LOGIN role. |
| `OVERMIND_HTTP_BIND` | Optional; `0.0.0.0` | Host address on which the HTTP endpoint is published. |
| `OVERMIND_HTTP_PORT` | Optional; `8080` | Host port mapped to the server's container port 8080. |
| `OVERMIND_AGENT_KEYS_FILE` | Optional; `./agent-keys.yaml` | Operator-owned bearer-key YAML on the host. |
| `MEMSRV_AGENT_KEYS_PATH` | Optional; `/run/secrets/agent-keys.yaml` | Read-only bearer-key path inside the server container. |

The committed `.env.example` and `agent-keys.example.yaml` contain placeholders
and safe non-secret defaults only. For this reference mode, the operator copies
them to local `.env` and `agent-keys.yaml` files, replaces the placeholders, and
restricts both files to mode `0600`. Those operator secret files are ignored.
Compose passes each database password separately through `PGPASSWORD`, rather
than interpolating it into Npgsql's semicolon-delimited connection string, so
ordinary PostgreSQL password characters do not alter connection parameters.
PostgreSQL 18 stores data in a Compose-managed named volume and has no published
host port; only the HTTP server is host-published. Re-running the command uses
the same volume and safely reruns provisioning and migrations.

Downstream infrastructure may replace the named volume, local secret inputs,
and Compose provisioning container with host-specific storage, backup,
templating, and provisioning equivalents. It must preserve the application
contract and dependency order; it does not need to consume the reference file
verbatim. In particular, Ansible remains the provisioning owner for the
homelab deployment.

The separately selected `compose.dev.yaml` owns disposable local development
provisioning and remains the default selected by repository Make targets and
developer scripts. Test targets may instead be explicitly configured with
`MEMSRV_TEST_ADMIN_CONNECTION_STRING` to use an already-running PostgreSQL 18
test instance; in that mode the surrounding environment owns PostgreSQL
provisioning, including creation of the restricted `memsrv` LOGIN role, and the
repository invokes no Docker/Compose command. External preflight verifies that
role without creating, altering, or managing its password. The default
production-oriented Compose deployment is never used for `memory_dev` or the
test database lifecycle. See `docs/testing.md` for the external test-instance
authority and lifecycle contract.

## Direct migration contract — FINAL

The direct `docker run` adapter below is a one-shot, non-interactive invocation.
For this adapter, the admin credential is supplied through the process
environment only and is never written to disk. This rule is distinct from the
reference Compose mode above, whose explicit operator contract uses an ignored,
mode-`0600` `.env` file.

```sh
docker run --rm \
  -e MEMSRV_ADMIN_CONNECTION_STRING='postgres://postgres:<REPLACE_ME>@postgres:5432/memory' \
  --entrypoint memctl \
  ghcr.io/faviann/overmind:<version> migrate
```

- Exit `0` — migrations applied, or already current (safe to run repeatedly).
- Exit `1` — migration failure.
- Exit `2` — usage error.
- Journal: DbUp `schemaversions` table in the target database. Production never
  applies migration files via raw `psql`.
- **The `memsrv` role must exist before migrations run.** Roles are owned by
  provisioning (the reference Compose bootstrap in reference deployments,
  the development bootstrap locally, and Ansible in the homelab deployment).
  Migrations grant to `memsrv` but never create roles or manage passwords.

## Schema verification — FINAL

`memctl verify-schema` asserts that a migrated database matches the schema,
grant, and trigger contract owned by overmind, so homelab-iac never has to
duplicate schema internals. Intended flow: create a disposable database, run
`memctl migrate`, run `memctl verify-schema`, then drop the database.

```sh
docker run --rm \
  -e MEMSRV_ADMIN_CONNECTION_STRING='postgres://postgres:<REPLACE_ME>@postgres:5432/<disposable>' \
  --entrypoint memctl \
  ghcr.io/faviann/overmind:<version> verify-schema
```

- Reads `MEMSRV_ADMIN_CONNECTION_STRING` only; needs no production data.
- Exit `0` — schema matches the contract (prints `schema verification passed`).
- Exit `1` — one or more contract violations; each is printed to stderr as a
  `  - <message>` line naming the broken contract.
- Exit `2` — usage error (`MEMSRV_ADMIN_CONNECTION_STRING` not set).
- Never prints connection strings or secrets.
- Safe against a real migrated database: the append-only probe writes a trace
  row inside a transaction that is always rolled back.

What it asserts:

- Required tables, the `forbid_mutation()` function, and the `traces_immutable`
  trigger exist.
- `traces` is append-only by trigger: UPDATE and DELETE attempted as the
  admin-capable verifier identity both fail with the append-only error.
- `memsrv` cannot UPDATE or DELETE `traces` by grant, and has no DELETE grant on
  any table in `public`.
- `memsrv` holds the expected schema/table/sequence privileges from the
  migration.
- Bootstrap rows exist: the `memory-system` and `homelab` namespaces and the
  default (`*`/`*`) retrieval config.
- The capture slice's binding, append-only route-policy, stream, observation,
  event, relationship, pairing-request coordination, and pairing-audit tables
  exist; immutable capture-ledger triggers and restricted grants are present;
  and `capture/unscoped` exists.
- `memsrv` has the expected SELECT, INSERT, and UPDATE grants on mutable pairing
  request state. Pairing audit has the `capture_pairing_audit_immutable`
  trigger, the expected SELECT and INSERT grants, no UPDATE grant, and no
  DELETE grant.

Run it against a **disposable** target only — dev/test/CI use a locally
provisioned database, never the persistent production `memory`.

## Environment variables — FINAL

| Variable | Purpose |
| --- | --- |
| `MEMSRV_CONNECTION_STRING` | Runtime database connection, as the `memsrv` role. |
| `MEMSRV_ADMIN_CONNECTION_STRING` | Admin connection, migrations only, passed per-invocation. |

Both accept either a `postgres://user:pass@host:port/db` URL or an Npgsql
keyword string (`Host=...;Port=...;Database=...;Username=...;Password=...`).

HTTP transport (default mode):

| Variable | Purpose |
| --- | --- |
| `MEMSRV_AGENT_KEYS_PATH` | Path to the provisioning-owned bearer-key YAML, mounted into the container. Required in HTTP mode; the server fails fast at startup if it is missing. |
| `MEMSRV_CAPTURE_CONSOLE_OIDC_AUTHORITY` | Optional HTTPS OpenID Connect issuer/authority for interactive capture-console operators. For Authentik, use the provider's application slug authority. |
| `MEMSRV_CAPTURE_CONSOLE_OIDC_CLIENT_ID` | Optional confidential OIDC client identifier registered for the capture console. |
| `MEMSRV_CAPTURE_CONSOLE_OIDC_CLIENT_SECRET` | Optional confidential OIDC client secret. Supply it through deployment secret handling; never commit it. |

The three capture-console OIDC variables form one optional configuration set.
When all three are absent, the console is disabled and the existing HTTP
surface remains available. Supplying only part of the set, or an invalid
authority, fails server startup with a secret-free configuration error. A
complete valid set enables the console.

Optional:

| Variable | Purpose |
| --- | --- |
| `MEMSRV_TRANSPORT` | `stdio` selects the local stdio transport; unset (default) serves HTTP. `--stdio` on the command line is equivalent. |
| `MEMSRV_HTTP_URL` | Kestrel bind address; defaults to `http://0.0.0.0:8080`. |
| `MEMSRV_AGENT_ID`, `MEMSRV_NAMESPACE`, `MEMSRV_SESSION_ID` | stdio-mode identity/session (defaults are sensible for a single-agent local setup). Ignored in HTTP mode, where identity comes from the bearer key and the session is transport-derived. |
| `MEMSRV_ALLOWED_NAMESPACES` | Comma-separated stdio-mode namespace allowlist. Unset confines the process to its default `MEMSRV_NAMESPACE`. Ignored in HTTP mode. |
| `MEMSRV_NEVER_STORE_PATH` | General Phase 1 write-safety rule file. Defaults to `config/never_store.yaml`, which ships in the image. A missing, empty, or invalid rule file makes the policy unusable and fails every governed Overmind write closed. The legacy capture effects remain: enrollment and ingestion refuse, and the tracer exits non-zero. |
| `MEMSRV_NEVER_STORE_LITERALS_PATH` | **Operator-owned** general Phase 1 write-safety file of exact credential values the installation already knows, one per line, mounted read-only. Unset, absent, or empty is valid and is not a fail-closed condition; an invalid file makes the policy unusable and fails every governed Overmind write closed. Never commit this file; the tracked rule file must never contain a real credential. |

No other application configuration is required; `config/never_store.yaml` ships in the
image. The numeric scan budgets are versioned runtime constants, not
configuration — see [write safety](write-safety.md). The separate legacy
128 MiB capture observation-size and fidelity ceiling remains governed by
[capture safety budgets](capture-safety-budgets.md).

## Postgres — FINAL

- Major version pinned to **18** (dev, CI, and production; minor floats).
- Production database `memory`, application role `memsrv` (LOGIN, password
  managed by the active provisioning owner: reference Compose or downstream
  infrastructure such as Ansible). Consumers never receive a connection
  string; the server is the only door.

## Service runtime (port, health, bind address) — FINAL

The server is one binary with a mode flag. Default mode serves streamable-HTTP
MCP; `MEMSRV_TRANSPORT=stdio` (or `--stdio`) keeps the local stdio path. Both
modes run from the same image.

- **Bind address:** `0.0.0.0:8080` by default (override with `MEMSRV_HTTP_URL`).
- **MCP endpoint:** `POST/GET/DELETE /mcp`, streamable HTTP, **bearer-authenticated**.
  A missing or unknown key is rejected with `401` before any tool runs. The
  agent identity and namespace allowlist come from the key entry; the trace
  session is the MCP protocol session (transport-derived, one MCP session = one
  trace session).
- **Health endpoint:** `GET /healthz`, **unauthenticated**. Returns `200` only
  when the database answers `SELECT 1` within ~2s; otherwise `503`. Suitable for
  a compose healthcheck — a non-200 reflects a real database outage, not just
  process liveness.
- **Bearer keys:** a provisioning-owned YAML file mounted read-only into the
  container,
  path via `MEMSRV_AGENT_KEYS_PATH`. Plaintext entries under a top-level `keys:`
  list, each `{key, agent_id, default_namespace, allowed_namespaces[]}`.
  Rotation is a redeploy; there is no key CRUD in the app.
  Values beginning with the reserved capture credential prefix `mcap_` are
  invalid agent keys and fail startup rather than acquiring MCP authority.
- **Capture console:** `GET /capture/console`, interactive OIDC authentication.
  Register `/capture/console/signin-oidc` as the client's callback path at the
  provider. The initial supported provider is Authentik using the standard
  authorization-code flow and `openid` scope. The server derives the audited
  operator identity from the provider's `sub` claim; request parameters, agent
  bearer keys, and capture credentials cannot supply operator identity. The
  console cookie is secure, HTTP-only, and restricted to `/capture/console`.
  It expires after a fixed eight hours and never uses sliding renewal. A
  locally validated session therefore continues during an OIDC outage only
  until that expiration; new sign-ins and renewals fail while the provider is
  unavailable.
  Credentialless enrollment additionally exposes
  `GET /capture/console/pair/{userCode}` and
  `POST /capture/console/pair/{userCode}/approve` under the same operator
  policy. The page identifies the exact pending request by its displayed code,
  shows the runtime-detected machine and Codex installation, and accepts only
  the operator-owned label, allowed repository route patterns, and special
  namespace mappings. Operator JSON inspection/approval and cancellation under
  `/capture/console/api/pairing/{requestId}` use the same OIDC policy. Every
  operator action derives its audit identity from the provider `sub`; no form,
  query, agent key, or capture credential can supply it.
  The server accepts one `X-Forwarded-Proto` hop so Traefik's external HTTPS
  scheme is used in the OIDC callback URI; Traefik remains the TLS owner.
  An unavailable OIDC authority prevents unauthenticated console entry but is
  not consulted by `/capture/v1/observations`, `/mcp`, or `/healthz`.
- **Capture pairing:** `POST /capture/v1/pairing-requests` creates a short-lived
  request from detected machine/installation evidence without an existing
  credential. `GET` and `DELETE /capture/v1/pairing-requests/{requestId}` use
  the returned secret polling bearer capability, not MCP, capture, or operator
  authority. The creation response contains a non-secret
  `/capture/console/pair/{userCode}` URL; it never embeds the polling token.
  Approval creates at most one binding for a Codex installation, including
  concurrent requests. The approved capture credential is held as ephemeral
  server coordination state, returned by one authenticated poll, and cleared
  atomically at delivery. Polling tokens are stored only as hashes. Pairing
  request state and its append-only audit are in PostgreSQL so server restarts
  preserve an in-flight request; expired or cancelled requests cannot be
  approved into authority.
- **Day-1 agent URL:** `http://overmind.faviann.vms:8080/mcp` — DNS name, plain
  HTTP on the LAN. The backend remains plain HTTP; external Traefik/TLS must
  supply the documented forwarded scheme and OIDC callback configuration.

## Per-user Codex catch-up runtime

`Dockerfile.capture-runtime` builds the separately versioned
`ghcr.io/faviann/overmind-codex-capture:<version>` artifact. It contains only
the Codex scanner adapter and talks to the server through the capture HTTP API;
it has no database connection or server role. `compose.capture.yaml` is the
reference Linux-first installation beside local Codex. Its container root is
read-only; the current `~/.codex/sessions` tree, the
`~/.codex/archived_sessions` retry-locator tree, and repository mounts are
read-only; durable state is
the only writable volume. The scanner is the container's only process and
retains ordinary isolated bridge networking. Compose enables its fixed-function
bridge wake adapter with a fixed command-line runtime mode and publishes only
host `127.0.0.1:43191` to container port `43191`. The adapter accepts only the
bridge-host gateway and relays bounded requests to the same process's
loopback-bound listener, so LAN clients and container peers cannot invoke it.
The runtime has no general command surface, Docker socket,
privileged mode, or self-update behavior. Archived files are selected only for an existing
non-empty durable queue, never as historical import. See
`docs/codex-capture-runtime.md` for enrollment and
the machine-owned/server-owned configuration boundary.

With no pre-provisioned `OVERMIND_CAPTURE_CREDENTIAL`, the runtime makes only
outbound HTTP(S) pairing creation/poll requests followed by its normal capture
observation writes. Its writable state volume persists a private installation
identity and the one-time-delivered private capture credential. Supplying the
optional existing environment credential remains compatible and bypasses
pairing; the shipped Compose and example environment do not require it.

## Release verification

Exercise the complete reference Compose lifecycle against a locally available
candidate image tag:

```sh
make smoke-compose IMAGE=ghcr.io/faviann/overmind:<version>
```

The smoke supplies temporary synthetic operator inputs, requires missing
version/admin/runtime values to fail during Compose interpolation, runs
`docker compose up -d --wait`, checks database-backed `/healthz` and
unauthenticated `/mcp`, runs Compose a second time against the same named
volume while rotating the runtime password, and tears the deployment down
cleanly. It asserts application behavior only through the public HTTP surface;
it does not inspect database internals.

From an overmind source checkout, exercise an exact image reference against a
disposable PostgreSQL 18 container:

```sh
make smoke-image IMAGE=ghcr.io/faviann/overmind:1.0.0 PULL=1
```

Published-image mode pulls the tag, prints its registry digest, and runs that
digest before the smoke begins, preventing a stale or subsequently moved local
tag from posing as the CI artifact. The run applies the image's baked-in
migrations through `memctl`, starts the image through its default entrypoint
with a disposable bearer-key file, requires a database-backed `200` from
`/healthz`, requires unauthenticated `/mcp` to reject with `401`, and completes
an authenticated MCP initialization. Application semantics such as migration
idempotency and unhealthy-database behavior remain in the .NET suite; this
command checks only OCI packaging and runtime wiring. It creates no persistent
volume and removes its containers and network on exit.

Tag CI runs this smoke against the locally built release candidate before it
pushes the immutable image tag, then runs published-image mode after the push
to verify the registry artifact by digest. A release is green only when both
adapters pass; downstream deployment may therefore consume the image as a
working implementation of this contract rather than re-testing it.
