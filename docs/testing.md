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
- Tests are order-independent: each test creates unique namespace/session,
  workstream, source, and search identifiers; never share mutable fixtures
  across tests. The session database is lifecycle infrastructure, not mutable
  test state. The suite fixture provisions the session database and each
  database-backed class starts from a fresh clone of the current migrated
  template. Tests within a class reuse that schema and isolate their rows
  through unique public identifiers. Expensive immutable scenario data may be
  initialized once per class behind a synchronized fixture. A test which
  deliberately mutates schema or grants must use its own disposable clone and
  restore cluster-wide role state.

## Child processes (memctl, MemSrv.Server)
- All subprocess launches go through `tests/MemSrv.Tests/TestProcessRunner.cs`,
  which executes the built apphosts directly — or, where another component owns
  the process lifecycle (e.g. `StdioClientTransport`), launch the apphost path
  the runner resolves (`TestProcessRunner.ServerPath`). Never launch children with
  `dotnet run`: its per-launch MSBuild evaluation races concurrent launches
  from parallel test classes on `obj/` state and intermittently corrupts
  unrelated tests (issue #30). Direct apphost execution is also ~8x faster.
- Like `--no-build`, tests never build the child projects; the runner fails
  with instructions if an apphost is missing. Configuration/TFM are derived
  from the test assembly's own output path, so Release runs Release apphosts.

## Runtime benchmark

- `make benchmark-test` is the repeatable warm-suite benchmark. It reports
  Docker/PostgreSQL readiness, build/restore, test discovery/host startup,
  template validation/migration, disposable database cloning, isolated memctl
  process startup, the five bounded fail-closed server-child startup cases,
  full test wall time, and TRX command/test-body durations. It also names the
  slowest successful tests and flags any over ten seconds.
- `make test` runs four disjoint test-host shards concurrently. Each shard gets
  the isolated session database and LOGIN role described above; their filters
  partition the suite, so the reported shard totals sum to the unchanged full
  test count. The fourth shard is the mutually exclusive catch-all, ensuring a
  newly added test class runs without requiring an edit to the shard list.
  `make test-one` intentionally remains one filtered host.
- Run it three times against the same warm checkout and report all three test
  phase values plus their median. Do not compare a green run with an aborted or
  skipped run.
- `DisposableCloneRevalidatesTemplateAfterDifferentMigrationSet` can sit at the
  ten-second boundary on a loaded benchmark host. Its cost is intentional: the
  mechanical lifecycle test serially installs migration set A, replaces it
  with B, restores A, validates all three clones, and finally restores the real
  template under the cross-process lock.
- The historical 30-failure profiling run is not a performance baseline for
  completed behavior. The merged #30 direct-apphost lifecycle fix and #33
  per-suite database/template isolation precede this benchmark; the post-merge
  baseline is 92 passing, zero failed, zero skipped in three runs.

### Issue #29 measurements (2026-07-13, same warm workspace)

| Run | Before: `make test` wall | After: `make test` wall | Result |
| --- | ---: | ---: | --- |
| 1 | 201.738s | 58.164s | 92 passed, 0 failed, 0 skipped |
| 2 | 194.541s | 58.123s | 92 passed, 0 failed, 0 skipped |
| 3 | 202.458s | 51.562s | 92 passed, 0 failed, 0 skipped |
| Median | 201.738s | 58.123s | unchanged full test count |

The checked-in benchmark command subsequently reported a 53.686s full test
phase, 92/0/0, and no successful test over ten seconds. The four shard-reported
test durations in the three post-change `make test` runs had maxima of 45s,
45s, and 42s; the command-level wall values above conservatively include
Docker readiness and the warm build as well.

## Anti-patterns (stop and flag to the human if you catch yourself)
- Weakening an assertion to reach green
- Asserting on internal tables to "verify" a tool worked (outside mechanical tests)
- Adding a test-only code path in production code
- Editing an existing test during a refactor step
- Putting a real-looking secret in a fixture
