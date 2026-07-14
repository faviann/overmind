# Captured-event retrieval interface options

Design analysis for [Define the canonical captured-event envelope](https://github.com/faviann/overmind/issues/68), recorded 2026-07-14.

## Decision in brief

Phase 2 should expose a canonical-only captured-event contract. It returns the
immutable capture observation, the immutable capture-time event interpretation,
and source-stated relationships. It does not resolve inherited context, native
relationship targets, or cross-stream display order.

This is a YAGNI boundary around behavior and public API, not around evidence.
The ledger must preserve everything a later read model would need. A future
client may earn either a rich snapshot operation or smaller named read
operations without changing the canonical contract.

## Requirements that shaped the choice

- PostgreSQL remains the only datastore and traces remain append-only.
- One immutable capture observation may produce multiple immutable trace
  events; every trace event has exactly one primary observation.
- Capture retains a sanitized source representation, adapter and scanner
  provenance, source ordering, separate timestamps, context-setting records,
  and source-native relationship identities.
- Existing Phase 1 `retrieve_trace` callers must not receive a silently
  changing response contract.
- Namespace authorization must not become an existence oracle through
  relationship resolution.
- This is a personal/homelab system. Query cost is less concerning than client
  correctness and premature infrastructure.
- No current client requires a rich reconstructed event in one call.

## Design 1: canonical receipt only — selected

The canonical response is assembled only from immutable ledger rows:

```text
captured event receipt
    ├── capture observation: safe source evidence and capture provenance
    ├── event: immutable adapter interpretation of one semantic part
    └── relationships: typed source-native references
```

Conceptually:

```json
{
  "contractVersion": 1,
  "observation": {
    "observationUuid": "...",
    "sourceStreamUuid": "...",
    "locator": {},
    "sourceTimestamp": { "raw": null, "parsed": null },
    "capturedAt": "...",
    "adapter": { "name": "...", "version": "..." },
    "scan": {
      "status": "clean",
      "ruleSetVersion": "...",
      "ruleIds": [],
      "redactionCount": 0
    },
    "safeSourcePayload": {}
  },
  "event": {
    "traceUuid": "...",
    "sessionId": "...",
    "partKey": "...",
    "partOrder": 0,
    "kind": "tool_result",
    "actor": "tool",
    "occurredAt": null,
    "payload": {}
  },
  "relationships": [
    {
      "type": "result_for",
      "target": {
        "sourceStreamUuid": "...",
        "nativeId": "call_abc"
      }
    }
  ]
}
```

The response does not gain a resolved trace UUID if `call_abc` is captured
later. It does not inherit a model from an earlier turn-context event or assign
a cross-stream display ordinal. Late capture therefore cannot change the
meaning or shape of a previously retrieved receipt.

### Gains

- Stable client semantics and straightforward caching.
- One contract version; adapter and scanner versions remain event provenance,
  not client-negotiated projection versions.
- Authorization checks only the requested ledger data. A native target does
  not reveal whether a target exists or is readable.
- No resolver registry, materialized projection, response digest, expansion
  language, or historical snapshot machinery.
- The canonical response remains useful as a self-contained provenance receipt.

### Losses

- No one-call active-model context, resolved parent UUID, or merged replay
  order.
- A rich replay client will eventually need another server operation.
- Native relationships remain unresolved in the canonical response even when
  the server could find their targets.
- Returning the shared observation with each derived event can produce larger
  responses than returning only its UUID.

## Design 2: rich evidence-backed snapshot — deferred option

A future operation could return the canonical receipt plus a fixed set of
currently resolved values:

```text
retrieve_captured_event_snapshot(traceUuid)
    → ledger receipt
    → resolved context
    → resolved relationship targets
    → derived display order
```

Every derivation would use a stable result shape rather than exceptions:

```json
{ "state": "resolved", "value": "gpt-5", "evidence": ["..."] }
```

or:

```json
{ "state": "unresolved" }
```

A digest could tell a caching client that late evidence changed the snapshot.
The response contract and resolver rule revision are independent: changing the
resolver need not break the JSON schema, but its revision must be reported so
changed answers remain explainable.

This design provides the best one-call ergonomics. It also immediately requires
authorization-filtered joins, context precedence and ambiguity rules,
relationship resolution, display-order rules, resolver provenance, and cache
semantics. Exact historical reconstruction of a prior digest would require
additional snapshot or watermark infrastructure. No present caller earns those
costs.

Design 1 does not block this option. A snapshot operation can be added over the
same ledger without altering the canonical receipt.

## Design 3: named opt-in read operations — deferred option

Instead of one rich snapshot, future clients could request specific read
models:

```text
resolve_trace_targets(traceUuid)
resolve_trace_context(traceUuid, keys)
retrieve_replay_window(traceUuid, before, after)
```

Each operation would have a fixed, bounded response and ordinary resolution
states such as `resolved`, `unresolved`, `ambiguous`, or `unsupported`. Missing
and authorization-forbidden relationship targets must be indistinguishable to
prevent an existence oracle.

This option keeps changing derived answers away from canonical retrieval and
lets clients pay only for what they use. Its costs are extra round trips,
multiple contracts, and the risk that narrowly named operations proliferate or
duplicate resolution logic.

Phase 2 should not reserve these method names, add a generic `include` array, or
create a resource/projection registry. The architectural seam is the complete
ledger, not speculative public API. A real caller should decide whether Design
2, Design 3, or a combination is eventually preferable.

## Guardrails that preserve both future paths

The canonical ledger must retain:

- stable observation, trace, source-stream, and relationship identities;
- the sanitized source representation and explicit omission markers;
- adapter, harness, and scanner/rule-set provenance;
- native session, turn, item, call, parent, fork, and subagent identifiers when
  the source states them;
- exact within-stream locator order and within-observation part order;
- separate source, occurrence, and capture timestamps;
- context-setting records such as session metadata and turn context;
- opaque safe observations/events for unsupported source types.

The design would be painted into a corner by discarding context records,
keeping only normalized text, replacing native references with internal UUIDs,
storing only a synthesized global order, or flattening inherited context into
canonical event facts.

The selected boundary is therefore:

> Capture and preserve evidence now; postpone resolver behavior and public
> projection APIs until a concrete client demonstrates the required query.
