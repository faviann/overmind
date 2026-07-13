# Deployment contract (consumed by homelab-iac)

What the homelab repo may rely on when deploying this application. Anything not
stated here is not a contract. Every section is now **FINAL**: the Session 2
HTTP transport has landed, so the service runtime shape (port, health, bind
address, key file) is defined below rather than deferred.

## Image — FINAL

- `ghcr.io/faviann/overmind:<version>` — immutable tags, published by CI on git
  tags `v*` (tag `v0.3.0` → image `0.3.0`).
- Pre-Session-2 tags are `0.x`: the migration contract below is stable, but the
  service runtime shape will change when Session 2 lands. Do not treat `0.x` as
  a compatibility promise beyond this document. No `latest` tag is published.
- The image contains the memory server runtime (`MemSrv.Server`, MCP over
  stdio) and the operator/migration CLI (`memctl`, on `PATH`), with migrations
  baked in at `/app/migrations`.

## Migration contract — FINAL

One-shot container, non-interactive, admin credentials via environment only
(never written to disk):

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
  provisioning (Ansible in production, the invariant Compose bootstrap in dev/CI).
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
| `MEMSRV_AGENT_KEYS_PATH` | Path to the Ansible-provisioned bearer-key YAML, mounted into the container. Required in HTTP mode; the server fails fast at startup if it is missing. |

Optional:

| Variable | Purpose |
| --- | --- |
| `MEMSRV_TRANSPORT` | `stdio` selects the local stdio transport; unset (default) serves HTTP. `--stdio` on the command line is equivalent. |
| `MEMSRV_HTTP_URL` | Kestrel bind address; defaults to `http://0.0.0.0:8080`. |
| `MEMSRV_AGENT_ID`, `MEMSRV_NAMESPACE`, `MEMSRV_SESSION_ID` | stdio-mode identity/session (defaults are sensible for a single-agent local setup). Ignored in HTTP mode, where identity comes from the bearer key and the session is transport-derived. |

No other configuration is required; `config/never_store.yaml` ships in the
image.

## Postgres — FINAL

- Major version pinned to **18** (dev, CI, and production; minor floats).
- Production database `memory`, application role `memsrv` (LOGIN, password
  managed by Ansible). Consumers never receive a connection string; the server
  is the only door.

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
- **Bearer keys:** an Ansible-provisioned YAML file mounted into the container,
  path via `MEMSRV_AGENT_KEYS_PATH`. Plaintext entries under a top-level `keys:`
  list, each `{key, agent_id, default_namespace, allowed_namespaces[]}`.
  Rotation is a redeploy; there is no key CRUD in the app.
- **Day-1 agent URL:** `http://overmind.faviann.vms:8080/mcp` — DNS name, plain
  HTTP on the LAN. Traefik/TLS is a later, purely infra-side add-on requiring no
  app or contract change.
