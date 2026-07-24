# Deployment contract (consumed by homelab-iac)

What the homelab repo may rely on when deploying this application. Anything not
stated here is not a contract. Every section is now **FINAL**: the Session 2
HTTP transport has landed, so the service runtime shape (port, health, bind
address, key file) is defined below rather than deferred.

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
- The disabled capture slice's binding, stream, observation, event, and
  relationship tables exist; immutable capture-ledger triggers and restricted
  grants are present; and `capture/unscoped` exists.

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

Optional:

| Variable | Purpose |
| --- | --- |
| `MEMSRV_TRANSPORT` | `stdio` selects the local stdio transport; unset (default) serves HTTP. `--stdio` on the command line is equivalent. |
| `MEMSRV_HTTP_URL` | Kestrel bind address; defaults to `http://0.0.0.0:8080`. |
| `MEMSRV_AGENT_ID`, `MEMSRV_NAMESPACE`, `MEMSRV_SESSION_ID` | stdio-mode identity/session (defaults are sensible for a single-agent local setup). Ignored in HTTP mode, where identity comes from the bearer key and the session is transport-derived. |
| `MEMSRV_ALLOWED_NAMESPACES` | Comma-separated stdio-mode namespace allowlist. Unset confines the process to its default `MEMSRV_NAMESPACE`. Ignored in HTTP mode. |

No other configuration is required; `config/never_store.yaml` ships in the
image.

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
- **Day-1 agent URL:** `http://overmind.faviann.vms:8080/mcp` — DNS name, plain
  HTTP on the LAN. Traefik/TLS is a later, purely infra-side add-on requiring no
  app or contract change.

## Disabled synthetic capture artifact — NOT A DEPLOYMENT CONTRACT

`Dockerfile.capture-tracer` builds an explicitly disabled, non-production OCI
tracer for the synthetic Codex fixture. It is separate from
`ghcr.io/faviann/overmind:<version>`, is not published by the release workflow,
and does not alter the supported server topology. Its temporary operator
procedure and limitations are documented in
`docs/capture-synthetic-slice.md`.

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
