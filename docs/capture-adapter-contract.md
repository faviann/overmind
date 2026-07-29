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
and never classify or mint identity. Contradictory explicit classifiers never
produce a guessed identity: that rollout carries its identity failure into its
own scan, where it is reported and skipped, and every other enumerated stream
still runs.

The canonical import receipt and `memctl capture receipt` expose the source
identity loaded from the durable stream. An adapter upgrade may normalize
adapter/source provenance without changing the immutable source record; the
server recognizes Codex v3/v4/v5/v6→v7 signatures narrowly where the record's
derived events are unchanged and only adapter/signature identity moved. A
record whose derived tool events changed in v7, a changed locator, or changed
source content remains a conflict.

## Tolerant parsing

Adapters are versioned tolerant tagged unions:

- known discriminators map to canonical message, tool call/result, error,
  compaction, lifecycle, and other earned event kinds;
- unsupported record or content variants become `opaque` events and retain
  their complete scanned source representation, including discriminators,
  additive fields, and safety-redacted sensitive evidence after ingestion;
- Codex response items explicitly tagged `reasoning` preserve explicitly tagged
  `summary_text` and `reasoning_text` blocks as canonical `reasoning`; encrypted
  content and signatures remain source evidence and are never interpreted as
  reasoning. Event kind and actor are independent: the reasoning discriminator
  earns kind `reasoning`; a recognized explicit source-stated `user`,
  `assistant`, `developer`, or `system` role supplies the actor for canonical
  reasoning parts and opaque section evidence, while an absent or unrecognized
  role remains `unknown`;
- a present non-array Codex reasoning `summary` or `content` section remains a
  deterministic `opaque` event with its complete source shape and discriminator,
  even when the other section yields canonical reasoning; a missing section
  yields no event;
- Codex `event_msg/context_compacted` is the duplicate lifecycle boundary view
  paired with one canonical `compacted` summary/history record, so it remains
  one evidence-bearing `annotation` with actor `harness`, not a second
  compaction event or an opaque record; both observations preserve their
  complete scanned source evidence;
- unknown additive fields remain in `sourcePayload`;
- content and output accept string or array forms;
- Codex `function_call`/`function_call_output`,
  `custom_tool_call`/`custom_tool_call_output`, specialized model-facing
  `local_shell_call`, `tool_search_call`/`tool_search_output`,
  `web_search_call`, and `image_generation_call` response items, plus the
  persisted exec-command and patch-apply `event_msg` terminal result records
  retain `call_id` (falling back to an explicitly stated item `id`) in both the
  event payload and deterministic part key. Duplicate begin/lifecycle views
  remain annotations rather than second canonical calls.
  Results carry a `result_for` relationship to that same native identity, so
  parallel and out-of-order observations never pair by adjacency. Arguments
  and input accept structured values or JSON-encoded strings and normalize to
  structured canonical arguments, while `sourcePayload` retains the native
  source form. Tool output/content retains its source string versus structured
  JSON type, and absent names remain null;
- tool outcomes are limited to `succeeded`, `failed`, `denied`, `interrupted`,
  or `unknown`. A canonical source `outcome`, a recognized terminal source
  `status`, or an explicit boolean `success` is evidence; unrecognized or
  absent evidence remains `unknown`, and a call without a captured result
  remains only a `tool_call`;
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

Codex `session_meta` and `turn_context` records are canonical `context` events
with actor `harness`. Their payload is
`{ scope, scopeId, values, instructionEvidence }`: `values` is the complete
source payload clone, scope is `session` or `turn`, and `scopeId` is populated
only from the source-stated session/turn id. `instructionEvidence.base` is
`exposed` only when the record contains non-null `base_instructions`;
`builtIn` and `loaded` remain explicitly `unavailable`. Model, provider, and
harness version remain facts of the individual observation: they are neither
propagated between records nor inferred from each other.

The top-level Codex rollout timestamp remains the observation
`sourceTimestamp` and is never used as an event-occurrence fallback.
Separately, a parseable `session_meta.payload.timestamp` is explicit
event-occurrence evidence and supplies that context event's `occurredAt`;
missing or invalid payload timestamps remain null, and `turn_context` has no
occurrence time. Neither clock falls back to the other or to server-authored
capture time.

Codex compaction uses separate immutable operation phases. A `PreCompact`
hook fact is a `compaction` event with `phase: request`; `PostCompact` and
persisted rollout `compacted` records are `phase: completion`, and every
completion payload sets `contextBoundary: true`. Only source-stated facts are
populated: current Codex hooks contribute their trigger but no success outcome
or summary, while rollout completion contributes `message` summary,
`replacement_history`, and available window-chain evidence. Summary evidence is
preserved in its source shape and never flattened to text: a `message` summary
is preferred, an older `summary` array is emitted unchanged when no `message` is
present, and both keep their JSON type. Older numeric and newer string window
IDs likewise retain their JSON type. Missing outcome remains
`unknown`; missing evidence remains null. The separate rollout
`event_msg/context_compacted` lifecycle signal is an `annotation` carrying its
source payload, not another compaction or conversation event. Summary and
replacement history remain derived evidence attached to the completion
observation; adapters never reconstruct, replace, or mutate an earlier event.

## Conformance fixtures and disposable Claude spike

The synthetic, version-labelled families are:

- `fixtures/adapter-conformance/codex-cli-0.77.compaction.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.compaction.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.compaction-hooks.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.messages.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.context.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.145.reasoning.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.145.opaque.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.145.annotations.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.145.tools.synthetic.jsonl`
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
annotations. The Codex context family covers session/turn scope, complete
additive setting preservation, explicitly exposed base-instruction evidence,
observation-local model/provider and CLI-version provenance, and the three
non-fallback clocks. The Codex compaction families cover old numeric and new
string window identities, the newer complete window chain, exact summary and
replacement-history evidence, hook request/completion phases, and explicit
completion boundaries. The Codex 0.145 additive families cover source-exposed
reasoning, mixed supported and present-but-non-array reasoning sections,
complete opaque signature/encrypted/additive metadata, complete unsupported
record and content evidence, and evidence-bearing duplicate lifecycle/reasoning
views retained as annotations, including the `event_msg/context_compacted`
boundary view paired with canonical `compacted` summary/history evidence. The
0.145 tool family covers function, custom, and specialized call/result records,
parallel and out-of-order native identities, missing names, string and
structured values, explicit canonical outcomes, and visible turn abort/error
records.

`CodexJsonlAdapter` is the only adapter referenced by the separately built
disabled tracer image. `DisposableClaudeJsonlAdapter` is defined in the test
assembly only. The test invokes the same `DisabledCaptureRuntime` and
authenticated `/capture/v1/observations` API used by the Codex tracer, then
reads the canonical result through `memctl`. No release project references the
Claude spike, and enrollment records a harness identity without selecting an
adapter.
