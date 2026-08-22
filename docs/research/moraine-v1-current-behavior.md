# Moraine V1 current behavior for Codex evidence capture

Research for [Adopt Moraine for Codex V1 evidence capture and historical backfill](https://github.com/faviann/overmind/issues/206), checked 2026-08-22.

## Question and method

What is the smallest proof path for using current Moraine as the external Codex evidence/capture substrate, and where does its behavior differ from the milestone's settled requirements?

This note uses only first-party upstream evidence from `eric-tramel/moraine`: default branch `main` at commit [`2fb4aec7a59e9859b8b215eca0810ae9da2ec076`](https://github.com/eric-tramel/moraine/commit/2fb4aec7a59e9859b8b215eca0810ae9da2ec076), the latest stable release [`v0.7.3`](https://github.com/eric-tramel/moraine/releases/tag/v0.7.3), and upstream issue [Stable v0.7.3 CLI installs incompatible mainline plugins](https://github.com/eric-tramel/moraine/issues/669). Confirmed behavior below means it is stated in those docs or implemented/tested in source. Items marked inference are conclusions from those sources or from the operator-supplied local inventory.

The operator reports Codex 0.149.0, 2,558 active rollout files (about 1.697 GB, 66,802 lines), eight archived rollout files (about 8.2 MB, 4,364 lines), and a largest observed JSONL line of about 2.19 MB. The host is Linux x86-64 with `uv`, Docker/Compose, 32 GiB RAM, and 109 GiB free; Moraine and Cargo are not currently installed. I did not ingest or alter that corpus.

## Answer in brief

- **No evidence-infrastructure redesign is needed for the V1 proof.** Moraine already supplies a local ClickHouse-backed ingest service, startup backfill, filesystem watches plus periodic reconciliation, raw source-shaped JSON rows with provenance, tolerant Codex `unknown` events, default pre-persistence secret redaction, replay-stable normalized event IDs, a monitor, and read-only MCP search/open tools.
- **Archived Codex rollouts are not covered by Moraine's default source.** Add a second ordinary `codex` harness source for `~/.codex/archived_sessions/**/*.jsonl`; Moraine supports multiple `[[ingest.sources]]`, and its own defaults use multiple sources for one harness where needed. This is configuration work, not a custom harness.
- **The smallest install path should avoid `moraine setup`'s stable/plugin combination for MCP.** Upstream issue 669 confirms that stable v0.7.3 setup fetches incompatible plugin content from `main`. Install the stable CLI for the stack, create/edit the config, and manually register `moraine run mcp` with Codex for dogfooding; alternatively build/install the exact main commit after provisioning Rust. The bug affects guided plugin/MCP registration, not Codex ingestion, ClickHouse, or the direct stdio MCP server.
- **There is one real fidelity boundary to verify, not redesign:** Moraine stores every valid JSON object only while its source line and resulting serialized row fit hard limits. Oversized, malformed, or non-object lines advance the checkpoint and produce `ingest_errors`, but do **not** get a `raw_events` row. The reported 2.19 MB maximum is below the default 8 MiB source-line cap; the proof must still assert zero post-serialization size, parse, and normalization omissions from `ingest_errors` rather than assume corpus completeness.
- **Ordinary restart/reconcile is idempotent at the file cursor, while raw storage is intentionally append-only.** An unchanged file at the committed size returns without emitting; append resumes at byte offset. A path move creates a new checkpoint/raw identity. A crash after a raw insert but before the later checkpoint insert can also re-append raw rows on retry because ClickHouse inserts are staged, not transactional across tables. Neither caveat requires an Overmind deduper for this milestone.

## Smallest proof topology

Current Moraine describes itself as a local trace stack that indexes harness files into ClickHouse and exposes a monitor plus MCP retrieval ([README](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/README.md#L7-L17)). `moraine up` starts three logical pieces on the same host: managed ClickHouse, the ingest service, and one unified backend serving the monitor, HTTP API, Streamable HTTP MCP, and a private Unix socket for stdio compatibility ([quickstart](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/quickstart.md#L68-L96), [shared backend](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/install.md#L178-L199)). The default listeners are loopback (`127.0.0.1:8123` for ClickHouse and `127.0.0.1:8080` for the backend), so Docker and a separate PostgreSQL integration are unnecessary.

Supported install hosts are Linux and macOS; release bundles cover x86-64 and arm64. Managed ClickHouse refuses hosts with less than 2 GiB detectable memory and auto-installs the pinned native ClickHouse build when configured to do so ([platform and memory checks](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/apps/moraine/src/managed_clickhouse.rs#L68-L87), [managed install behavior](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/apps/moraine/src/managed_clickhouse.rs#L748-L803)). This host clears those prerequisites. Moraine does not install a login service, so “ongoing automatic” means while `moraine up` is running; after reboot it must be started again ([quickstart](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/quickstart.md#L76-L80)). That is an operational note for V1, not a reason to design permanent deployment infrastructure.

### Version/install choice

As of the research date, the latest published release is v0.7.3, while `main` is nine commits ahead. Upstream issue 669 confirms a coordinated-release bug: v0.7.3's setup installs plugin assets from current `main`; those plugins expect the newer HTTP registration flow, while the stable setup removes the old registration without adding the new one. The issue's confirmed workaround is to pin matching plugin assets, and it also confirms that direct `moraine run mcp` works.

The relevant Codex ingest, normalization, redaction, checkpoint, size-limit, and replay-stable-identity files are byte-identical between tag v0.7.3 and commit `2fb4aec` (verified by local Git blob comparison). Therefore the smallest no-toolchain proof is:

1. `uv tool install moraine-cli` (v0.7.3).
2. Create the runtime config without installing Codex/Claude plugins, then add the two sources below.
3. Start the stack with `moraine up`.
4. For dogfood retrieval, use the stable stdio path directly, e.g. `codex mcp add moraine -- moraine run mcp`. Current Moraine deliberately retains this compatibility path ([shared stdio compatibility](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/install.md#L178-L199), [manual Codex registration](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/install.md#L236-L258)).

Building exact `main` instead is valid but currently requires adding a Rust toolchain; upstream's source path is `cargo build --workspace --locked` followed by `MORAINE_SOURCE_TREE_MODE=1 cargo run -p moraine -- up` ([source install](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/quickstart.md#L217-L235)). That adds setup cost without changing the capture behaviors under test.

## Exact Codex source configuration

The shipped source covers only `~/.codex/sessions/**/*.jsonl` ([default config](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/config/moraine.toml#L54-L68)). Use two enabled sources with distinct names and the same built-in `codex` adapter:

```toml
[ingest]
backfill_on_start = true

[[ingest.sources]]
name = "codex"
harness = "codex"
enabled = true
glob = "~/.codex/sessions/**/*.jsonl"
watch_root = "~/.codex/sessions"
format = "jsonl"

[[ingest.sources]]
name = "codex-archived"
harness = "codex"
enabled = true
glob = "~/.codex/archived_sessions/**/*.jsonl"
watch_root = "~/.codex/archived_sessions"
format = "jsonl"
```

This is supported by the repeatable `[[ingest.sources]]` model ([configuration](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/configuration.md#L48-L85)); Moraine's own Pi/OMP and Prime defaults demonstrate multiple named sources feeding one harness. Source names matter operationally: the durable checkpoint key is `(source_name, source_file)` ([checkpoint key](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/checkpoint.rs#L4-L29)), while MCP search can filter by the configured `source` separately from `harness` ([MCP source filters](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/interface.md#L93-L115)). Current setup reconciliation matches only the setup-owned source name and preserves differently named custom sources, so later targeted setup need not delete `codex-archived` ([setup matching](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/apps/moraine/src/commands/setup.rs#L1082-L1144)).

## Backfill, watching, and restart semantics

With `backfill_on_start = true`, ingest loads durable ClickHouse checkpoints, enumerates every enabled source's glob, freezes file/byte targets for progress, starts reconciliation and watcher threads, and queues each startup file ([startup pipeline](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/lib.rs#L390-L470)). New and changed paths are observed recursively by native filesystem notification with a two-second polling watcher fallback; relevant create/data-modify/rename events enqueue the matching file ([watcher](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/watch.rs#L220-L382)). Independently, reconciliation re-enumerates each glob every configured interval (30 seconds by default, clamped to at least five), recovering missed notifications ([reconcile](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/reconcile.rs#L11-L55), [defaults](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/configuration.md#L863-L892)).

For file-backed JSONL, the cursor records inode, source generation, byte offset, and line number. Unchanged files return immediately. Appends seek to the committed offset. Inode replacement or truncation increments the generation and rereads from zero ([file resume logic](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L1068-L1143)). The final checkpoint advances after scanning, including past errors and quarantined lines ([final checkpoint](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L1530-L1560)).

Confirmed implication: an ordinary restart/reconcile does not multiply unchanged active history. Caveat: sink writes `raw_events`, then normalized tables, then checkpoints as separate ClickHouse inserts ([flush order](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sink.rs#L1256-L1304)). A process/database failure after a successful raw insert but before the checkpoint can reappend raw rows. `raw_events` is a plain `MergeTree`, not a replacing table ([schema](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/sql/001_schema.sql#L3-L23)). This is a failure-window limitation, not continuous multiplication during ordinary operation.

## Raw evidence, provenance, and native session identity

For each valid JSON object, Moraine parses the line and stores a compact reserialization of the entire object in `raw_json`; it is source-shaped and semantically complete but not byte-for-byte source text. The row also includes `source_name`, normalized harness, inferred provider, cwd, source path, inode, generation, line number, byte offset, record timestamp, source discriminator (`top_type`), session ID, a hash, and a raw UID ([normalization envelope](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/normalize.rs#L78-L122), [table schema](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/sql/001_schema.sql#L3-L23)). `author` is also stamped when configured; local-only installs may leave it empty ([identity configuration](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/configuration.md#L87-L107)).

For Codex, `session_meta.payload.id` is the native session ID. Later records inherit it; if the header is unavailable, the adapter falls back to the trailing UUID in the rollout filename ([Codex adapter](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sources/codex.rs#L12-L45), [filename fallback](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sources/shared.rs#L746-L756)). This preserves the native session identity across a `sessions` to `archived_sessions` move.

The raw UID intentionally includes `source_file`, generation, line, offset, content fingerprint, and suffix ([raw/provisional UID](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sources/shared.rs#L930-L950)). Thus the same source record observed at its active and archive paths becomes two durable raw observations. That exactly matches the milestone's accepted V1 limitation.

## Unknown records and hard fidelity limits

Unknown **valid Codex objects are retained**. The raw row is built before adapter routing. An unfamiliar top-level `type` emits an `unknown` normalized event carrying the compact full record, and an unfamiliar `response_item.payload.type` emits an `unknown` event carrying its full payload ([routing](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sources/codex.rs#L127-L138), [unknown response item](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sources/codex.rs#L405-L424), [unknown top-level](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/sources/codex.rs#L645-L659)). This satisfies the settled “unknown semantics must not discard otherwise valid raw evidence” requirement.

Three inputs do not receive a raw row:

1. A source line larger than `min(ingest.max_batch_bytes, 8 MiB)` is skipped before parsing; only compact size/provenance metadata enters `ingest_errors` ([source-line guard](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L336-L371), [processing path](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L1328-L1372)).
2. Malformed JSON or a valid non-object produces `json_parse_error` with at most 20,000 characters of fragment and then advances ([parse path](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L1375-L1416)).
3. If any derived `raw_events`, `events`, link, or tool row serializes above ClickHouse's 10 MiB object limit, Moraine drops the whole source line's normalized output—including its raw row—and records only compact size metadata ([serialized-row guard](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L380-L413), [processing path](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/dispatch.rs#L1450-L1488)).

The reported 2.19 MB largest source line is safely below the 8 MiB source guard. Inference: even a roughly doubled JSONEachRow representation remains below 10 MiB, so current corpus size does not reveal a blocker. The proof must still group `ingest_errors.error_kind` and require zero `jsonl_source_line_too_large`, `jsonl_normalized_row_too_large`, `json_parse_error`, and `normalize_error` rows for the selected sources before claiming complete historical coverage.

## Default pre-persistence redaction

Secret redaction defaults to the built-in ruleset; only the home config can honor the explicitly dangerous skip flag, and mirror egress is always redacted ([default config](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/config/moraine.toml#L37-L47)). The tee router applies redaction before handing batches to the default sink ([pre-persistence gate](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/tee.rs#L550-L592)). It scans `raw_json`, normalized text/payload, tool input/output, and error fragments; when raw text changes, it recomputes `raw_json_hash` ([redacted fields](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/redaction.rs#L108-L131), [raw/event mutation](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/redaction.rs#L247-L283)). Built-ins cover common vendor tokens, private keys, JWTs, and entropy-gated generic credential assignments ([rule list](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/crates/moraine-ingest-core/src/redaction.rs#L487-L602)).

Confirmed limitation: this is pattern-based best-effort redaction, not a claim that arbitrary secrets are impossible. That is the accepted default safety boundary for this milestone; leave the skip flag absent and do not redesign it.

## Normalized identity and archive moves

The raw UID is transport-coordinate-sensitive, but the final normalized event UID is semantic. Migration 033 explicitly removes file, generation, line, and offset from canonical replacement identity while retaining them as provenance ([migration intent](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/sql/033_replay_stable_events.sql#L1-L7)). The identity includes author, harness, inference provider, native session ID, event classification/content/tool fields, and a canonical payload that excludes timestamps, cwd, project paths, source references, and other replay coordinates; notably it does not include `source_name` or `source_file` ([identity fields](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/sql/033_replay_stable_events.sql#L27-L118)). The canonical events table is a `ReplacingMergeTree(event_version)` keyed only by `(session_id, event_uid)` ([replacement key](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/sql/033_replay_stable_events.sql#L284-L288)).

Therefore, with one stable/empty `identity.author`, replaying the same rollout at the archive path yields the same normalized UID and one logical event after replacement, while `raw_events` retains both observations. The surviving normalized row's source provenance can change to whichever replacement version wins; raw provenance is the durable place to inspect both observations. Changing `identity.author` between the two ingests would change normalized identity and defeat this collapse, so keep it stable for the proof.

## Search, replay, and raw-evidence dogfood surfaces

The read-only MCP server exposes five tools: `search_sessions`, `open`, `list_sessions`, `file_attention`, and `get_ingest_status` ([interface](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/interface.md#L16-L33)). The useful V1 dogfood sequence is:

1. `get_ingest_status {}` until health, finite coverage, and freshness are true; it reports frozen startup file/byte progress per source without leaking source paths ([status contract](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/interface.md#L35-L50)).
2. `list_sessions` over a broad historical window with `harness: "codex"`, then repeat with `source: "codex-archived"` to prove archived visibility.
3. `search_sessions` for known rare keywords, using explicit tool/reasoning event types when needed; search is BM25 over normalized events, not semantic search ([search contract](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/interface.md#L76-L115)).
4. `open` an event, then its turn/session, following bounded cursors to replay the normalized conversation context ([open contract](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/agent-mcp-search/interface.md#L147-L180)).
5. Inspect `raw_events` and `ingest_errors` directly through ClickHouse or the monitor's bounded table endpoint for source-level assertions. The analytics CLI cannot export raw events—V1 export is normalized `events` only ([export limitation](https://github.com/eric-tramel/moraine/blob/2fb4aec7a59e9859b8b215eca0810ae9da2ec076/docs/export.md#L1-L18)).

MCP is a normalized search/navigation surface, not the proof of raw-evidence fidelity by itself. The raw acceptance checks need SQL/table inspection keyed by `source_name`, `source_file`, `source_line_no`, and native `session_id`.

## Concrete mismatches, blockers, and proof checks

| Finding | Classification | Smallest response |
| --- | --- | --- |
| Default Codex source excludes `archived_sessions`. | Confirmed gap, not blocker. | Add the second `codex-archived` source above. |
| Stable CLI setup installs incompatible current plugins (issue 669). | Confirmed tooling blocker to the guided plugin path only. | Avoid guided plugin install; use stable stack plus manual stdio MCP, or provision Rust and install matching main. |
| No OS login service. | Confirmed operational limitation. | Keep `moraine up` running for the proof and record restart-after-reboot behavior; do not build deployment automation in this milestone. |
| Invalid/non-object, over-8-MiB source lines, and over-10-MiB derived rows are not present in `raw_events`. | Confirmed evidence-fidelity boundary. | Query `ingest_errors`; a nonzero count is a concrete blocker/gap to report, not a reason to predesign storage. |
| Reported current largest line is ~2.19 MB. | Operator observation; no blocker inferred. | Verify post-backfill error counts rather than changing limits preemptively. |
| Plain `raw_events` plus cross-table flush/checkpoint sequence permits duplicate raw rows in failure windows. | Confirmed limitation. | Test ordinary restart (expected stable); document raw duplicate counts if a failure occurs. No Overmind deduper. |
| Archive move duplicates raw path observations but normalized identity is replay-stable. | Confirmed and already accepted. | Move/copy one representative file, wait for reconcile, assert two raw path observations and one `(session_id,event_uid)` normalized event. |
| MCP/export expose normalized projections, not bulk raw replay. | Confirmed surface limitation, not blocker. | Dogfood MCP for inspectability; use ClickHouse/monitor table reads for raw fidelity checks. |

## Recommended next implementation-sized proof

One bounded execution ticket can now install the stable local stack, create the two-source config, run backfill, and record a coverage/dogfood report. It should stop and rescope only if `ingest_errors` reveals actual omitted source records, the managed ClickHouse cannot complete backfill within available resources, or the manual MCP path fails independently of issue 669. Nothing found here requires reopening the evidence/knowledge boundary, adding Overmind-owned capture, or designing permanent provenance identifiers.
