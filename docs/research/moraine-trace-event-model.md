# Moraine's trace event model as prior art

Research for [Define the canonical captured-event envelope](https://github.com/faviann/overmind/issues/68), checked 2026-07-14.

## Question and method

How does Moraine represent captured agent activity, and which parts should
Overmind adopt, adapt, or reject for an immutable canonical captured-event
envelope?

The intended project is unambiguous: this repository's own bibliography links
to `eric-tramel/moraine`, whose official description calls it a local trace
stack that indexes Codex, Claude Code, and other harness sessions into
ClickHouse. This note inspects Moraine at commit
[`84ca132a78727d825303278b276793119cf3759e`](https://github.com/eric-tramel/moraine/tree/84ca132a78727d825303278b276793119cf3759e),
using only its official repository, documentation, source, and SQL schema.
Moraine explicitly warns that its schemas can change across minor releases, so
the commit pin matters. [Moraine README](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/README.md#L7-L16)

Statements labelled **Fact** are directly established by those sources.
Statements labelled **Inference** are conclusions for Overmind rather than
claims made by Moraine.

## Answer in brief

Moraine is strong prior art for separating source capture from normalized
query shapes. It stores a raw source-record row, then emits zero or more
normalized events, relationship rows, tool-I/O rows, and ingest errors. That
architecture validates several useful ideas: retain the redacted source
record, normalize calls and results separately, carry native relationship
identifiers, preserve unknown records, and distinguish harness, provider, and
model.

It is not a direct storage blueprint for Overmind. Moraine is an analytics and
retrieval index that deliberately tolerates versioned replacement, eventual
deduplication, separately committed projections, synthetic total ordering, and
automatic re-import after some source changes. Overmind's PostgreSQL trace
ledger is an immutable provenance record and cannot repair a bad append later.

The most important refinement is cardinality:

- each Overmind trace has exactly one primary captured observation;
- one captured observation may legitimately yield multiple traces;
- therefore capture identity belongs on an immutable
  `capture_observations` parent, while each derived trace uses a deterministic
  part key unique within that observation.

This preserves the decision already made about one primary observation per
trace without pretending that a mixed-content Claude message must be flattened
into one trace.

## What Moraine actually persists

**Fact.** Moraine has no single physical event envelope. Its generic wrapper
creates one `raw_events` row for each kept source record, then delegates to a
harness adapter that may emit normalized `events`, `event_links`, and
`tool_io`; parse and normalization failures go to `ingest_errors`. The raw row
and normalized event rows repeat source identity and coordinates rather than
sharing a foreign-key parent. [Generic normalization](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/normalize.rs#L76-L175),
[physical schema](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/001_schema.sql#L3-L208)

The normalized event shape is wide. It includes event/session identity,
source coordinates, source and parsed timestamps, event and payload kinds,
actor, request/trace/item/tool/origin IDs, substream and coordination fields,
model and token metadata, normalized text, a JSON payload, cwd, and project
fields. Relationships live separately and distinguish a normalized
`linked_event_uid` from a source-native `linked_external_id`. Tool I/O is also
a separate projection with request/response phase, native call IDs, error,
input/output, previews, sizes, and a hash. [Event and link columns](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/001_schema.sql#L25-L146),
[tool-I/O columns](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/001_schema.sql#L148-L176)

**Fact.** Source-record-to-event cardinality is not one-to-one. A Claude
message whose `content` is an array produces one normalized event per content
block. The block's array index participates in a source-local UID suffix, and
thinking, tool-use, tool-result, and text blocks become different normalized
event families. [Claude block dispatch](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L181-L229),
[Claude tool blocks](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L252-L325)
Codex compaction records similarly retain one raw compaction event and expand
`replacement_history` items into linked child events. [Codex compaction normalization](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/codex.rs#L528-L605)

**Inference.** This fan-out is not required for faithful capture; it is a
useful normalized read shape. Overmind can preserve the source record once and
still expose each message segment or tool operation as a source-addressable
trace. The suitable relationship is:

```text
capture_observation (trusted stream + locator + redacted source record)
    ├── trace (part_key = "message")
    ├── trace (part_key = "content/0:text")
    └── trace (part_key = "content/1:tool_use")
```

The database should enforce uniqueness on the trusted observation locator and
on `(capture_observation_id, part_key)`. Every trace points to exactly one
observation; a single observation can have several immutable interpretations.
Observation, traces, and their links should be inserted in one PostgreSQL
transaction. This is a refinement of the previously proposed one-to-one
companion record, not a retreat from single-source provenance.

## Identity, checkpoints, and idempotency

**Fact.** Moraine's event UID is SHA-256 over source file path, source
generation, line number, byte offset, record content, and a source-local
suffix. Native message and tool IDs are retained as fields and links, but are
not the primary event identity. [UID implementation](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L839-L855)

**Fact.** File checkpoints are keyed by configured source name and source file
path. If the inode changes or the current file becomes shorter than the saved
offset, Moraine increments `source_generation`, resets offset and line to zero,
and reimports the file. It does not verify that the already-read prefix is
unchanged; an in-place rewrite that preserves inode and does not shrink below
the offset is not caught by this test. [JSONL checkpoint behavior](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/dispatch.rs#L704-L752)
The watcher source explicitly notes that changing a file-backed source path
orphans the existing checkpoint and reingests history under new UIDs. [Path identity comment](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/watch.rs#L156-L174)

**Fact.** `events`, `event_links`, and `tool_io` use
`ReplacingMergeTree(event_version)`, not a uniqueness constraint. Duplicate
normalized rows can remain visible until a merge or a `FINAL` query collapses
them. Moraine added a migration specifically because transient reingest
duplicates inflated turn ordinals and made handles nondeterministic.
`raw_events` uses plain `MergeTree`, so this replacement behavior does not
deduplicate raw rows. [Table engines](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/001_schema.sql#L21-L23),
[normalized engines](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/001_schema.sql#L123-L176),
[deduplication migration](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/019_dedup_conversation_trace_final.sql#L1-L21)

### Why Moraine contains these mechanisms

The official source documents only part of the rationale:

- **Generation and reimport — partly documented.** The code defines an inode
  change or truncation as a new generation and starts again. The path-handling
  comment explains the concern about orphaning checkpoints and duplicating
  history, but the project does not claim that such changes are common or
  document why automatic replay is preferable to stopping. The likelihood and
  recovery preference are therefore not established facts. **Inference:** a
  search index can favor availability and later collapse duplicate normalized
  rows; an immutable ledger cannot safely make that trade.
- **ReplacingMergeTree — documented.** Moraine's ingest-author guide says
  mutable SQLite source records deliberately re-emit stable logical UIDs with
  newer versions so `ReplacingMergeTree` coalesces them. Migration 019 explains
  why queries must use `FINAL` even for file reingest duplicates.
  [Ingest-source rationale](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/docs/development/ingest-sources.md#L137-L174)
  This is intentional current-state indexing, not append-only history.
- **Timestamp and total-order fallbacks — behavior documented, rationale not
  found.** Missing or invalid source timestamps become Unix epoch in the
  physical event row and produce an ingest error. The conversation view then
  uses parsed source time or ingestion time and breaks ties with path,
  generation, position, and UID. The source does not claim that this order is
  causal or explain why an unknown time should become epoch.
  [Timestamp fallback](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L737-L743),
  [conversation view](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/019_dedup_conversation_trace_final.sql#L59-L103)
  **Inference:** it is a deterministic display and analytics order.
- **Provider inference — documented semantic goal, not evidence quality.** A
  migration separates the harness that wrote the trace from the inference
  backend and says the normalizer populates Codex as `openai` and Claude Code
  as `anthropic`, while reserving strings such as `azure/openai` and
  `bedrock/anthropic`. [Provider migration](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/012_add_inference_provider_and_rename_claude.sql#L1-L18)
  The adapters do in fact hard-code those defaults. [Source interface and default metadata](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/mod.rs#L25-L40),
  [Codex default](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/codex.rs#L12-L19),
  [Claude Code default](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L12-L19)
  No official rationale says that inferred provider is equivalent to observed
  provenance.
- **No per-event adapter/redaction provenance — no documented rationale
  found.** The event schema carries harness and content provenance, while
  service version and aggregate redaction counts are heartbeat concerns. The
  inspected official sources do not state that omitting adapter version,
  rule-set version, or per-observation scan outcome was a deliberate contract.
  It should be treated as an absence, not a design argument for omission.

**Inference.** Overmind should not copy Moraine's primary identity or recovery
semantics. Prefer a trusted stream identity plus a native ID when available,
otherwise a verified append-only source position. A prefix/checkpoint
fingerprint is a health tripwire, not causal truth. On truncation or rewrite,
stop that stream and report a capture-health error; do not create a new
generation and automatically replay ambiguous observations. Payload bytes and
source paths should not be the durable identity, especially because the
payload may contain secrets before redaction.

## Ordering and time

**Fact.** Moraine stores database receipt time (`ingested_at`), the original
timestamp string (`record_ts`), and a parsed millisecond timestamp
(`event_ts`). A missing or unparsable timestamp is not nullable: it becomes
1970-01-01 and emits a timestamp parse error. [Normalizer timestamp handling](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/normalize.rs#L76-L138)

**Fact.** Its conversation view synthesizes a total `event_order` within each
session from source timestamp or ingestion time, followed by source file,
generation, byte offset, line, and UID. It also derives turn sequence by
counting user messages where a source turn is unavailable. [Conversation and turn ordering](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/019_dedup_conversation_trace_final.sql#L59-L103)

**Inference.** These are useful read-model techniques, not safe canonical
facts. Overmind should keep source time nullable, retain captured time
separately, preserve native causal IDs and source positions, and avoid a
canonical total order across streams. A UI may derive a deterministic display
order as long as it is labelled as such. For a mixed-content observation,
`part_key` or an explicit source segment index preserves the order that the
source record itself proves.

## Sessions, parents, subagents, and causal relationships

**Fact.** Moraine's relationship vocabulary includes parent event,
compaction parent, parent UUID, tool-use ID, source-tool-assistant, and
subagent parent. A link can point either to a normalized event UID or to an
external source ID, avoiding false equivalence between the two. [Link schema](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/001_schema.sql#L127-L146),
[link builders](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L1229-L1284)

**Fact.** The Claude adapter preserves `parentUuid`, request ID, agent ID and
name, team name, sidechain status, model, item UUID, parent tool call, source
assistant UUID, and source tool-use ID. It emits external links for the parent
UUID, tool-use ID, and source assistant UUID. [Claude provenance fields](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L129-L178),
[Claude links](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L419-L465)
Codex maps a top-level `parent_id` to an external parent-event link; other
source fields remain available in payload JSON even when not promoted.
[Codex parent link](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/codex.rs#L677-L700)

**Fact.** Moraine's Kimi adapter makes each subagent stream a standalone
session linked to its parent and leaves the duplicate parent-stream envelope
raw-only, preventing double counting. [Subagent-stream design](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/docs/development/ingest-sources.md#L25-L42)

**Inference.** Overmind should adopt typed edges and the distinction between
internal event identity and source-native external identity. Parent and
subagent streams should remain separate when the harness supplies separate
streams. Missing causal edges stay unknown; timestamp proximity must never
manufacture them.

## Tool calls and results

**Fact.** Moraine models tool calls and results as separate normalized events
joined by native `tool_call_id`. It also builds `tool_io` request/response
projections. Claude preserves structured input, structured/text output,
`is_error`, parent call ID, and tool name on the request. [Claude tool handlers](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L252-L325)
Codex likewise emits distinct call and output events, but its function-output
handler writes error `0` without explicit failure evidence and leaves the
result's tool name empty. [Codex function-call handlers](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/codex.rs#L249-L294)

**Inference.** Preserve separate immutable call/result observations and join
them by the source's call ID; do not synthesize a mutable combined execution.
Normalize calls toward `{tool, params}` and results toward
`{tool, ok, summary, bytes}` only where the source proves each field. Unknown
tool name or status must remain unknown rather than becoming empty-name or
successful-by-default. Preserve the redacted source representation alongside
the convenience fields.

## Provenance, model, and provider

**Fact.** Moraine usefully separates source name, harness, inference provider,
model, source path and coordinates, session, author, cwd, and project/worktree
context. [Base normalized event](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L1063-L1226)
It also carries model forward as an adapter hint for later Codex records, and
its generic model canonicalizer maps the literal model `codex` to a specific
model name. [Model hint resolution](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L500-L539),
[Codex model fallback](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/codex.rs#L655-L674)

**Inference.** Adopt the field separation but qualify the evidence. Overmind
should distinguish `observed`, `inherited`, `configured`, and `inferred`
provenance or simply leave unavailable values null. It should never silently
turn a harness name into a model, assume a provider from the harness, or carry
a model beyond the scope for which the source established it. The capture
observation should also record adapter name/version and extraction-policy
version, which Moraine's event rows do not.

## Raw, unknown, malformed, and oversized records

**Fact.** Every kept record from a registered harness gets a raw JSON row
before source-specific normalization. Unknown top-level Codex records and
unknown Claude operational records become canonical `unknown` events while
their JSON remains in payload/raw storage. [Raw wrapper and registered-source gate](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/normalize.rs#L57-L119),
[Codex unknown handling](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/codex.rs#L638-L653),
[Claude unknown handling](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/claude_code.rs#L374-L406)

**Fact.** Fidelity is bounded. Normalized text and tool fields are truncated to
200,000 characters. Oversized source lines or normalized rows are skipped and
represented by ingest errors; malformed JSON stores only a bounded fragment.
[Shared limits and tool builder](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L10-L12),
[tool truncation](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sources/shared.rs#L1286-L1327),
[bounded JSONL handling](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/dispatch.rs#L755-L850)

**Inference.** Adopt the principle that an understood source record is not
discarded merely because its subtype is new. Represent it as an opaque,
redaction-safe observation with the original discriminator and an explicit
adapter outcome. For malformed, binary, unsupported, or oversized input, the
canonical envelope must say what was omitted and why; it must not imply
verbatim fidelity when only a fragment survived.

## Redaction and scan provenance

**Fact.** Moraine redacts secret-bearing strings in raw events, normalized
events, tool I/O, and error fragments before sink insertion. Replacements are
typed placeholders, and it recomputes dependent previews, sizes, and hashes
after changes. [Redaction pipeline](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/redaction.rs#L110-L156),
[derived-field recomputation](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/redaction.rs#L247-L345)
Redaction is default-on; only user-owned home configuration can disable it for
the local backend, while mirror egress remains redacted. Redaction counts are
aggregate heartbeat data rather than per-event scan results. [Redaction configuration](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/docs/configuration.md#L273-L300)

**Inference.** Adopt deterministic pre-append redaction and recomputation of
derived fields. Improve on Moraine by recording per-observation scan status,
rule-set/version, redaction count or rule IDs, and any fail-closed omission.
“Raw” must mean the source representation after declared redaction, not
byte-identical secret-bearing input. Identity should be established from the
trusted locator before sanitization but should not expose a reusable unkeyed
hash of secret-bearing payload bytes.

## Atomicity and immutability

**Fact.** Moraine inserts raw events, normalized events, links, tool rows,
errors, and checkpoints in separate ClickHouse operations. There is no
transaction spanning those tables. A failure after an earlier insert can leave
a partial batch to be completed or duplicated on retry. [Sink flush sequence](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/crates/moraine-ingest-core/src/sink.rs#L917-L989)

**Fact.** Normalized rows are explicitly replaceable by `event_version`, and
official migrations issue historical `ALTER TABLE ... UPDATE` operations.
Moraine therefore does not claim or enforce append-only immutability in
Overmind's sense. [Provider backfill and updates](https://github.com/eric-tramel/moraine/blob/84ca132a78727d825303278b276793119cf3759e/sql/012_add_inference_provider_and_rename_claude.sql#L35-L104)

**Inference.** Keep Overmind's stronger boundary: insert the immutable capture
observation, all derived traces, and their relationship rows atomically;
enforce source-locator and part-key uniqueness in PostgreSQL; grant no update
or delete path for traces or capture observations. Later adapter improvements
must append a new interpretation linked to the old one or rebuild a disposable
read model, never replace ledger history.

## Transfer decisions for Overmind

| Moraine idea | Decision | Overmind adaptation |
|---|---|---|
| Raw source record plus normalized rows | **Adopt, refine** | One immutable `capture_observations` parent may own many immutable traces through deterministic part keys. |
| Wide normalized analytics event | **Adapt** | Keep the trace envelope smaller; move capture mechanics to the observation and event-specific data to tagged content/read models. |
| Separate native external links | **Adopt** | Typed edges retain source IDs without pretending they are Overmind trace UUIDs. |
| Separate tool call and result | **Adopt** | Join by native call ID; preserve unknown status/name and redacted source payload. |
| Source path/generation/position/content UID | **Reject as canonical identity** | Use trusted stream identity plus native ID or verified append-only locator; part key identifies fan-out. |
| Automatic generation reset and replay | **Reject** | Block and report when the append-only prefix changes. |
| `ReplacingMergeTree` eventual dedup | **Reject** | PostgreSQL uniqueness and one atomic append; no replace/update semantics. |
| Epoch/ingest fallback and synthesized total order | **Reject as canonical fact** | Nullable occurred time, separate captured time, source-qualified positions, derived display order only. |
| Harness/provider/model separation | **Adopt, qualify** | Record evidence origin; do not hard-code or silently inherit unavailable facts. |
| Unknown-event raw preservation | **Adopt** | Opaque redaction-safe payload plus explicit adapter/scan outcome. |
| Default-on typed redaction | **Adopt, strengthen** | Persist rule-set and per-observation outcome; fail closed on unscanned content. |
| Separate multi-table sink writes | **Reject** | Observation, traces, and links commit in one database transaction. |

## Recommended envelope consequence

Moraine answers the immediate design question by showing that mixed source
records genuinely exist and are useful to fan out, but it does not require
Moraine's replaceable analytics semantics. The canonical capture model should
be:

1. one immutable `capture_observations` row per trusted source locator,
   containing harness/adapter provenance, source time, captured time, safe raw
   representation, and scan/redaction outcome;
2. one or more immutable trace rows, each referencing exactly that one primary
   observation and carrying a deterministic `part_key` plus the canonical
   event kind/content;
3. typed immutable relationship rows for native parent, tool, compaction, and
   subagent evidence;
4. uniqueness on the observation locator and `(observation_id, part_key)`, all
   appended atomically.

That structure takes Moraine's best lesson—separate capture truth from useful
normalization—while preserving Overmind's stronger promises: no invented
provenance, no silent ordering claims, no eventual deduplication, and no
mutation of the trace ledger.
