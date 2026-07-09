# Deployment contract (consumed by homelab-iac)

What the homelab repo may rely on when deploying this application. Anything not
stated here is not a contract. Status of each section is explicit: **FINAL**
sections are stable; **DEFERRED** sections are defined by Session 2 (HTTP
transport) and must not be guessed at.

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
  provisioning (Ansible in production, `docker/postgres-init/` in dev/CI).
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

Optional (defaults are sensible for a single-agent local setup):
`MEMSRV_AGENT_ID`, `MEMSRV_NAMESPACE`, `MEMSRV_SESSION_ID`.

No other configuration is required; `config/never_store.yaml` ships in the
image.

## Postgres — FINAL

- Major version pinned to **18** (dev, CI, and production; minor floats).
- Production database `memory`, application role `memsrv` (LOGIN, password
  managed by Ansible). Consumers never receive a connection string; the server
  is the only door.

## Service runtime (port, health, bind address) — DEFERRED to Session 2

The server currently speaks MCP over **stdio only**. The HTTP transport with
static bearer keys is Session 2 of `memory-server-phase1-spec.md` and has not
been built. Until it lands:

- there is **no listen port, no health endpoint, no bind address** to encode in
  a production compose file — do not write the service unit yet;
- the production-useful capability of the image is `memctl migrate` (homelab
  slices: database bootstrap, backups, migration invocation, schema verify);
- when Session 2 lands, this section gets replaced with the final values and a
  new image tag, and the homelab service compose is written against it.
