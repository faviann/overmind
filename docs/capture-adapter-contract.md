# Harness-neutral capture adapter contract

Issue #75 introduces one adapter seam in `src/CaptureAdapters`. It interprets
trusted local source material; authentication, routing, deterministic safety
scanning, persistence, and checkpoint advancement remain owned by the capture
server.

## Input and outcome

`TrustedSourceObservation` identifies one source record or hook fact with:

- an explicit source identity tuple and numeric source position;
- a native-id or verified byte-range locator;
- `persisted_record` or `hook_fact` material provenance;
- the source payload;
- explicit terminality evidence.

`ICaptureSourceAdapter.Adapt` returns exactly one
`CaptureSourcePositionOutcome`:

- `Incomplete` carries the position and reason, emits no observation, and must
  not advance the stream;
- `Terminal` carries a complete `CaptureObservationRequest` for the canonical
  authenticated HTTP API.

The terminal request preserves source identity, locator, raw-and-parsed source
timestamp, harness/version/record discriminator, material kind, explicitly
observed model and provider, adapter identity, the source payload, deterministic
semantic parts, source-stated relationships, and optional route evidence
(`workingDirectory` plus the complete named remote list). The server then
applies its independent fail-closed safety gate and returns the safe canonical
receipt. Route evidence is provenance, not a namespace claim.

Unavailable model, provider, timestamp, actor, outcome, and relationship facts
are never filled from adjacent records or harness stereotypes. Nullable
provenance stays null, unavailable actors use the canonical `unknown` role,
unavailable tool outcomes use `unknown`, and absent relationships remain an
empty list.

`CaptureSourceIdentity` contains the harness-native external session identity
and an optional observed child identity. It deliberately excludes parent and
fork facts. The server combines this tuple with the authenticated capture
binding to derive the canonical capture stream and trace session; imported
content never supplies the represented agent identity.

The HTTP compatibility field `sourceSessionId` may be omitted when
`sourceIdentity` is present. When both are present and nonblank,
`sourceSessionId` must equal `sourceIdentity.externalSessionId`; contradictory
identity claims are rejected before ingestion.

For Codex rollouts, discovery reads `session_meta.payload.session_id` (falling
back to the legacy `id` compatibility shape) as the external session identity.
`payload.id` becomes `childId` only when tagged `source` and/or
`thread_source: "subagent"` explicitly classifies the rollout as a child.
`parent_thread_id` and `forked_from_id` remain independent source provenance
and never classify or mint identity. Contradictory explicit classifiers stop
discovery rather than guessing.

The canonical import receipt and `memctl capture receipt` expose the source
identity loaded from the durable stream. An adapter upgrade may normalize
adapter/source provenance without changing the immutable source record; the
server recognizes the Codex v2→v3 signature transition narrowly, while a
changed locator or changed source content remains a conflict.

## Tolerant parsing

Adapters are versioned tolerant tagged unions:

- known discriminators map to canonical message, tool call/result, error,
  compaction, lifecycle, and other earned event kinds;
- unsupported record or content variants become `opaque` events and retain
  their scanned source representation;
- unknown additive fields remain in `sourcePayload`;
- content and output accept string or array forms;
- message content objects become canonical parts only when a known text-part
  discriminator carries an explicit string `text`; nominal text parts without
  that field remain opaque evidence rather than becoming serialized JSON text;
- empty message content arrays, missing content, and unsupported non-array
  shapes remain deterministic opaque source evidence rather than inferred
  canonical messages;
- known migrating fields accept scalar or object forms;
- tool arguments accept structured values or JSON-encoded strings.

Part keys come from a stable source path or native part identity and are
deterministic for the same immutable observation. Source timestamps are never
promoted to event occurrence timestamps.

## Conformance fixtures and disposable Claude spike

The synthetic, version-labelled families are:

- `fixtures/adapter-conformance/codex-cli-0.144.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.messages.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.77.parent-only.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.90.fork-only.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.120.parent-fork.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.nested-child.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.absent-relationship.synthetic.jsonl`
- `fixtures/adapter-conformance/claude-code-2.1.201.synthetic.jsonl`

The Codex and Claude general families pass through the same conformance
assertions for messages, successful and failed tools, turn failures,
compaction, subagents, unknown records, drift shapes, provenance,
relationships, and deterministic part identities. The Codex message family
also covers model-facing user, assistant, developer, and system-stated
messages, deterministic content-part fan-out, and duplicate UI-view
annotations.

`CodexJsonlAdapter` is the only adapter referenced by the separately built
disabled tracer image. `DisposableClaudeJsonlAdapter` is defined in the test
assembly only. The test invokes the same `DisabledCaptureRuntime` and
authenticated `/capture/v1/observations` API used by the Codex tracer, then
reads the canonical result through `memctl`. No release project references the
Claude spike, and enrollment records a harness identity without selecting an
adapter.
