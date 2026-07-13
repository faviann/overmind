# Testing conventions (load before writing or changing tests)

## What a good test is here
- Calls the public surface: an MCP tool (`log_trace`, `save_note`, `search_memory`,
  `get_by_id`, ...) or a `memctl` command. Asserts on the response or on subsequent
  public reads.
- Named for the behavior: `shared_write_cannot_be_born_approved`, not
  `MemoryRepositoryTest_Update`.
- Runs against a real local session database on Postgres (major pinned to **18**,
  matching production; `make db-up` provisions it). No mocking of the database
  or internal services — the DB constraints are part of the behavior under test.

## The only DB-level tests allowed (mechanical tests)
Where the database mechanism IS the spec'd behavior, tests connect directly and
assert that mechanism directly:
- trace mutation blocked by the `forbid_mutation` trigger AND by grants —
  **verify the grants, not just the trigger** (connect as `memsrv`, attempt
  UPDATE/DELETE, expect permission denied)
- no DELETE granted on any table
- never-store gate blocks a seeded synthetic secret (fake `AKIA...` pattern)
- namespace isolation holds across agents
- private memories invisible to other agent credentials
- migration-keyed test-template lifecycle: changing the migration fingerprint
  rebuilds the template, and each atomic clone exposes exactly the schema for
  the migration set that requested it

## Database lifecycle
- The xUnit host owns one database per suite run. With no caller configuration,
  it generates `memory_test_<runid>`; `MEMSRV_TEST_DATABASE` pins the name for
  an IDE or harness that needs a predictable database. The host clones the
  database from `memory_test_template` and drops it on clean disposal. Never use
  a test database as an interactive playground; that's `memory_dev`.
- Each host also provisions a unique LOGIN role that inherits `memsrv` grants.
  This keeps cluster-level role verification isolated between concurrent suites;
  production still has only the canonical `memsrv` runtime role.
- `memory_test_template` is refreshed automatically when the migration-file
  fingerprint changes. Disposable schema-verifier databases clone it rather
  than running migrations again. Template validation and each clone happen
  under one cross-process lock, so a different worktree cannot replace the
  template between the fingerprint check and `CREATE DATABASE ... TEMPLATE`.
- The template name and migration-fingerprint contract intentionally have two
  implementations: C# owns bare `dotnet test` lifecycle, while
  `tools/test-db.sh` owns Make/operator lifecycle. This keeps both entry points
  independently usable. Any change to that contract must update both
  implementations and validate their compatibility.
- `make test-db-reset` recreates `${MEMSRV_TEST_DATABASE:-memory_test}` from the
  current template. `make test-db-sweep` removes databases leaked for more than
  six hours by crashed runs, but never the template or a database with an active
  connection. `make db-up` runs the same conservative sweep.
- Tests are order-independent: each test creates its own namespace/session ids;
  never share mutable fixtures across tests. The session database is lifecycle
  infrastructure, not mutable test state: database-backed classes still reset
  and migrate their own schema as required by their existing test seam.

## Anti-patterns (stop and flag to the human if you catch yourself)
- Weakening an assertion to reach green
- Asserting on internal tables to "verify" a tool worked (outside mechanical tests)
- Adding a test-only code path in production code
- Editing an existing test during a refactor step
- Putting a real-looking secret in a fixture
