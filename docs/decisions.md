# Decisions

## 2026-07-05 — Keep development databases disposable

Development runs against a disposable local PostgreSQL instance, with separate
`memory_dev` and `memory_test` databases. The production `memory` database on the
LXC is never used for development traffic because traces are intentionally
append-only and no table grants DELETE.
