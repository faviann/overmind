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

## The only direct-DB access allowed
MCP tools and `memctl` remain the preferred test seams. Use them for agent and
operator behavior, and assert on their responses or subsequent public reads.
Direct database access is limited to the two categories below; it is not a
general license to inspect internal tables.

### Binding acceptance queries
Spec §10 requires these queries to work now, but Phase 1 has no corresponding
MCP tool or `memctl` command. The acceptance suite may therefore execute their
specified SQL directly:
- §10.2: given a `source_id`, list every memory derived from it using the
  indexed `memories.source_id` lookup
- §10.4: given a session and claim, run FTS over the memories and traces that
  session actually consumed, including proof that unconsumed records do not
  satisfy the query

These are binding acceptance queries, not mechanical database checks. Keep
them at the exact query seams required by §10; do not generalize them into
arbitrary table assertions.

### Mechanical database checks
Where the database mechanism IS the spec'd behavior, tests connect directly and
assert that mechanism directly:
- trace mutation blocked by the `forbid_mutation` trigger AND by grants —
  **verify the grants, not just the trigger** (connect as `memsrv`, attempt
  UPDATE/DELETE, expect permission denied)
- no DELETE granted on any table
- never-store persistence absence for a seeded synthetic secret (fake
  `AKIA...` pattern)
- every memory row's `content_hash` is the valid server-computed SHA-256 of its
  content
- migration-keyed test-template lifecycle: changing the migration fingerprint
  rebuilds the template, and each atomic clone exposes exactly the schema for
  the migration set that requested it
- capture-ledger transaction atomicity, locator/event-part uniqueness, immutable
  observation/event/relationship triggers, and the corresponding restricted
  grants. These are mechanical checks of the narrow disabled capture slice;
  routing, receipts, authorization, and retry behavior stay at the HTTP/memctl
  public seams.
- The disabled capture slice has no operator command for changing binding route
  policy. A routing test may therefore update only that binding's route columns
  as narrow mechanical setup, then must prove stream-fixed routing solely from
  later public HTTP receipts. Direct inspection of the resulting route is not
  an assertion seam.

Namespace isolation and private-memory invisibility are binding acceptance
behaviors, but their seam is keyed MCP agents. Verify them through public
searches and reads, not direct table assertions.

## Database lifecycle
- `make test` supports two provisioning environments. With
  `MEMSRV_TEST_ADMIN_CONNECTION_STRING` unset, `make db-up` provisions the
  existing PostgreSQL 18 service through `compose.dev.yaml`; this remains the
  default local workflow. When that variable is set, `make db-up` treats it as
  an explicit external-mode selection, invokes no Docker/Compose command, and
  uses the already-running PostgreSQL instance instead. There is no fallback
  between modes.
- External mode accepts a PostgreSQL connection URL to an existing maintenance
  database (normally `postgres`). The URL is supplied per invocation and must
  authenticate as a PostgreSQL superuser; the suite creates/drops databases and
  roles and temporarily changes `memsrv` while verifying the role contract.
  The surrounding environment must provision the canonical `memsrv` role;
  preflight never creates or alters it. The `psql` client must be on `PATH`.
  Preflight checks URL shape, connectivity, PostgreSQL major 18, authority, and
  that `memsrv` is a restricted LOGIN/INHERIT role with a password, no elevated
  flags or inherited memberships, and default connection/configuration limits.
  A missing or incompatible role fails before build or test discovery. Use a
  dedicated disposable test cluster, never production:

  ```sh
  MEMSRV_TEST_ADMIN_CONNECTION_STRING='postgres://test_admin:<password>@db:5432/postgres' \
    make test
  ```

  Do not put this value in a tracked file. PostgreSQL URL-encode reserved
  characters in the username/password. The same variable works with
  `make test-one T=...`, `make test-db-template`, `make test-db-reset`, and
  `make test-db-sweep`.
- The xUnit host owns one lazily created database per suite run. With no caller
  configuration, it generates `memory_test_<runid>`; `MEMSRV_TEST_DATABASE`
  pins the name for an IDE or harness that needs a predictable database. The
  first database-backed class clones it from `memory_test_template`, later
  classes replace it with a fresh clone, and the host drops it on clean
  disposal. Never use a test database as an interactive playground; that's
  `memory_dev`.
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
- Bare `dotnet test` retains its existing contract: without external
  configuration it expects the Compose development PostgreSQL instance to
  already be running (normally via `make db-up`). Supplying
  `MEMSRV_TEST_ADMIN_CONNECTION_STRING` makes its C# lifecycle use that external
  cluster, but does not run the Make preflight; canonical external execution is
  `make test`.
- `make test-db-reset` recreates `${MEMSRV_TEST_DATABASE:-memory_test}` from the
  current template. `make test-db-sweep` removes databases leaked for more than
  six hours by crashed runs, but never the template or a database with an active
  connection. `make db-up` runs the same conservative sweep.
- Tests are order-independent: each test creates unique namespace/session,
  workstream, source, and search identifiers; never share mutable fixtures
  across tests. The session database is lifecycle infrastructure, not mutable
  test state. The suite fixture reserves the session database identity and
  provisions its LOGIN role; each database-backed class materializes or replaces
  that database with a fresh clone of the current migrated template. Tests
  within a class reuse that schema and isolate their rows through unique public
  identifiers. Expensive immutable scenario data may be initialized once per
  class behind a synchronized fixture. A test which deliberately mutates schema
  or grants must use its own disposable clone and restore cluster-wide role
  state.

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
  no-command apphost startup, direct server-apphost startup through the public
  HTTP health endpoint, full test wall time, and separate TRX command/test-body
  durations. It also names the slowest successful tests and flags any over ten
  seconds.
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

The checked-in benchmark command subsequently reported a 50.339s full test
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
