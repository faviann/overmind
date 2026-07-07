# Decisions

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
