# Canonical captured-event envelope

Resolution for [Define the canonical captured-event envelope](https://github.com/faviann/overmind/issues/68), recorded 2026-07-14.

## Answer in brief

The Phase 2 canonical capture contract is a stable provenance receipt assembled
from immutable ledger rows:

```text
captured-event envelope
    ├── observation: sanitized source evidence and capture provenance
    ├── event: one immutable capture-time semantic interpretation
    └── relationships: immutable source-stated native references
```

One capture observation may yield multiple events. Every event has exactly one
primary observation and a deterministic part key. Observation, events, and
relationships append atomically, with database uniqueness at the observation
locator and event-part boundaries.

The envelope contains no read-time resolution. Inherited context, resolved
relationship targets, cross-stream display order, and current-state analytics
are future projections. The analysis and non-blocking seams for those future
interfaces are recorded in
[Captured-event retrieval interface options](captured-event-retrieval-interface-options.md).

## Logical storage shape

This ticket fixes the logical contract, not migration names or exact SQL types.
The Phase 2 specification should preserve these three normalized concepts:

1. A **capture source stream** is one trusted append-only harness session or
   subagent stream.
2. A **capture observation** is one accepted persisted harness record or
   hook-only source fact within that stream.
3. A **captured event** is one semantic part interpreted from exactly one
   observation.

Typed source relationships are stored independently so their targets may be
captured before, after, or never. A self-contained wire receipt joins only
these immutable rows; storage does not copy the observation into every event.

The insert transaction must enforce:

- one observation per trusted `(source stream, source locator)`;
- one event per `(observation, part key)`;
- no event without its primary observation;
- no relationship without its source event;
- no checkpoint advance unless the whole observation batch commits.

Retries are no-ops or return the existing identity. A changed verified source
prefix stops that stream visibly; capture does not invent a generation and
reimport immutable history.

## Canonical receipt

Property names below are the conceptual camelCase wire shape. Exact endpoint
selection belongs to the delivery-slice ticket; this decision does not change
the binding Phase 1 `retrieve_trace` response or reserve a projection API.

```json
{
  "contractVersion": 1,
  "observation": {
    "observationUuid": "...",
    "sourceStreamUuid": "...",
    "source": {
      "harness": "claude-code",
      "harnessVersion": "2.1.201",
      "recordType": "assistant"
    },
    "locator": {
      "kind": "native_id",
      "nativeId": "message-uuid"
    },
    "sourceTimestamp": {
      "raw": "2026-07-14T12:00:00.000Z",
      "parsed": "2026-07-14T12:00:00.000Z"
    },
    "capturedAt": "2026-07-14T12:00:00.250Z",
    "adapter": {
      "name": "claude-code-jsonl",
      "version": "1"
    },
    "scan": {
      "status": "redacted",
      "ruleSetVersion": "...",
      "ruleIds": ["bearer-token"],
      "redactionCount": 1
    },
    "safeSourcePayload": {}
  },
  "event": {
    "traceUuid": "...",
    "sessionId": "...",
    "agentId": "...",
    "namespace": "...",
    "partKey": "content/2:tool_result",
    "partOrder": 2,
    "kind": "tool_result",
    "actor": "tool",
    "occurredAt": null,
    "payloadVersion": 1,
    "payload": {
      "callId": "call_abc",
      "outcome": "failed",
      "output": "connection timed out"
    }
  },
  "relationships": [
    {
      "type": "result_for",
      "target": {
        "sourceStreamUuid": "...",
        "nativeId": "call_abc",
        "kind": "tool_call"
      }
    }
  ]
}
```

Unavailable optional facts remain `null`, omitted, or an explicit `unknown`
domain value as the payload contract requires. They are never inferred merely
to fill a stable-shaped response.

## Observation contract

An observation owns facts shared by every semantic part derived from the same
source record:

- immutable observation and trusted source-stream identities;
- harness identity and observed harness version, when stated;
- source record discriminator;
- a trusted native record ID when available, otherwise a verified append-only
  byte locator; diagnostic line numbers are not the sole identity;
- the source record's timestamp in raw and parsed form, when stated;
- required server capture time;
- capture adapter name and version;
- scan status, governed rule-set version, safe rule IDs/categories, and count;
- the safe source representation after deterministic redaction or fail-closed
  omission.

`safeSourcePayload` is deliberately not called raw. Every persisted field has
already crossed the bounded security boundary. Detected secrets are replaced
deterministically. If scanning, structure, or size budgets fail, metadata is
retained while the unsafe field or payload becomes an explicit omission marker.
The ledger never stores an unscanned tail.

For a verified byte locator, delivery also supplies an exact source-byte
SHA-256 to make retry comparison sensitive to byte-preserving semantic
rewrites such as JSON whitespace changes. That transport proof is validated
and folded only into a binding-keyed content signature. It is not canonical
observation provenance, is not persisted or returned as a raw unkeyed digest,
and is absent for native-ID locators.

## Event contract

The relational event header contains stable, frequently queried invariants:

- existing trace, session, agent, and namespace identity;
- primary observation reference;
- deterministic part key and source-stated part order;
- event kind and actor role as separate dimensions;
- nullable event-specific occurrence time;
- tagged-payload version and JSONB payload.

`partKey` prefers a source-native block/item identity. Otherwise it uses a
deterministic source path such as `content/2:tool_result`. `partOrder` records
order within one immutable observation; it is not a session-wide ordinal.

Initial canonical event kinds are open text with these earned conventions:

- `message`
- `reasoning`
- `context`
- `tool_call`
- `tool_result`
- `compaction`
- `lifecycle`
- `error`
- `annotation`
- `opaque`

Initial actor roles are:

- `user`
- `assistant`
- `system`
- `developer`
- `tool`
- `harness`
- `operator`
- `unknown`

Kind answers what happened; actor answers who or what the source says produced
it. This avoids multiplying message types for every role. Legacy Phase 1
`user_msg` and `assistant_msg` remain compatibility mappings rather than the
canonical captured-event shape.

## Tagged payload rules

The payload is kind-specific instead of a wide analytics row. Fields promoted
to the relational header are load-bearing ledger invariants, not speculative
future dashboard dimensions. The safe observation retains unpromoted source
fields so future projections are not painted into a corner.

### Messages, instructions, and context

User, assistant, system, and developer messages share `kind: message`; actor
preserves the exposed role. Source-stated session or turn settings use
`kind: context` with their explicit scope and values. Instruction-load metadata
without instruction content remains metadata; capture never pretends the full
system or instruction text was exposed.

Model, provider, repository, branch, cwd, and similar context is stored only
where the source states it. A tool event that does not state a model does not
gain one in the canonical envelope. A future evidence-qualified context read
may join applicable context observations without mutating the event.

### Tool calls and results

Tool calls and results are separate events joined by the source-native call ID,
never by adjacency. A tool-result payload carries a source-backed `outcome`:

- `succeeded`
- `failed`
- `denied`
- `interrupted`
- `unknown`

Explicit tool failures are terminal `tool_result` events, not a parallel
`tool_failure` family. Absence of a captured result leaves the call incomplete;
it never becomes an inferred failure. Multiple source results, missing names,
and unavailable statuses remain visible rather than merged or repaired.

### Compaction

Compaction is a first-class harness operation, not a tool. It may produce
separate request and completion events containing source-stated trigger,
instructions, input context, outcome, summary, and metrics. Request and
completion are linked only when the source provides reliable correlation.

Completion establishes a conversational context boundary. It never replaces,
updates, or deletes earlier trace events. A generated summary is a
harness-derived artifact; when earlier history was never captured, the summary
does not masquerade as verbatim history. Duplicate hook or lifecycle views are
annotations unless they carry otherwise unavailable facts.

### Reasoning

Source-exposed reasoning or thinking is optional additional replay fidelity.
When present and safe, it uses `kind: reasoning`. Its absence does not make core
conversation capture fail. Once observed, it is not silently discarded.

Capture never infers, decrypts, or reconstructs hidden reasoning. Opaque
signatures remain safe source material, not reasoning text. Durable decisions
and audit claims rely on observable outputs, rationale summaries, and source
references rather than private chain-of-thought.

### Unknown and duplicate records

An understood conversation or tool record drives replay. Duplicate UI, hook,
or lifecycle views become `annotation` events linked by source evidence when
possible. A hook-only fact still owns its own event.

Unsupported record or content-block types become `opaque` events with their
source discriminator and safe payload. Capture does not discard them or invent
semantics. Partial normalization leaves unknown fields in the observation and
does not claim verbatim fidelity for omitted content.

## Relationships

Relationships are typed, directed source evidence. Initial earned relation
names include:

- `responds_to`
- `result_for`
- `completion_of`
- `spawned_by`
- `parent_session`
- `forked_from`
- `compacts`
- `annotates`

The canonical target is a sanitized source-native identity, optionally scoped
by source stream and target kind. It is not an Overmind trace UUID invented or
required at capture time. This lets a child, result, or annotation arrive
before its target.

A source-stated relationship whose target never arrives is a dangling
relationship and remains useful evidence of incomplete capture. A record with
no source-stated parent is a legitimate root. The canonical receipt does not
resolve target UUIDs later and therefore remains stable. Authorization-aware
resolution is deferred to a future read model.

## Time and ordering

The contract keeps three clocks separate:

- `sourceTimestamp`: optional raw and parsed time on the observation;
- `occurredAt`: optional event-specific time stated for the semantic part;
- `capturedAt`: required server acceptance time.

No clock silently falls back to another. Read models may derive a display time
but must label the basis.

Canonical order is partial:

- verified source locators order observations within one source stream;
- part order sequences semantic events within one observation;
- explicit source relationships add cross-stream causal evidence;
- no global parent/subagent session ordinal is canonical.

Merged replay order is necessarily a derived presentation when the source does
not prove exact cross-stream interleaving.

## Explicitly deferred

Phase 2 envelope work does not build or reserve:

- inherited/resolved context in the canonical response;
- resolved internal targets for native relationships;
- a cross-stream replay ordinal;
- a rich snapshot response, response digest, or historical `asOf` mechanism;
- named projection/resource methods;
- a projection registry, materialization worker, or second datastore;
- wide analytical columns not earned by real PostgreSQL queries.

The complete immutable ledger is the seam. A future client may earn a rich
snapshot (Design 2), named reads (Design 3), or both without changing this
canonical receipt.

## Consequences for downstream Wayfinder tickets

- Fidelity policy must define bounded safe-payload and explicit omission
  representations without changing observation identity.
- Import authorization must make the server the only writer of observation,
  event, relationship, and external session/agent identities.
- The incremental-capture prototype must prove locator uniqueness, atomic
  observation fan-out, partial-final-record recovery, and prefix-change stop.
- Packaging must expose adapter, scanner, and stream health, including dangling
  relationships and fail-closed omissions.
- Delivery slices should implement the canonical receipt before any resolved
  context, target, replay, or analytics surface.
