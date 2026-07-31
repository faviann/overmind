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

The Codex session-metadata context event retains each relationship statement
independently: top-level `parent_thread_id` is `parent_session`,
`forked_from_id` is `forked_from`, nested
`source.subagent.thread_spawn.parent_thread_id` is `spawned_by`, and explicit
`source` and `thread_source` subagent classifications are respectively
`source_classification` and `thread_source_classification` directed at the
observed child thread. These facts are not deduplicated when, for example, the
top-level parent and nested spawn evidence name the same native session.
Unavailable fields emit no relationship. Targets remain unscoped native
identities, so a missing parent never blocks append and is observably different
from a root with no source-stated parent.

The canonical import receipt and `memctl capture receipt` expose the source
identity loaded from the durable stream. An adapter upgrade may normalize
adapter/source provenance without changing the immutable source record; the
server recognizes prior Codex adapter signatures narrowly where the record's
derived events are unchanged and only adapter/signature identity moved. A
record whose derived tool events changed in v7, whose relationship facts
changed in v8, whose binary safe representation changed in v9, a changed
terminal-malformed representation in v10, a changed locator, or changed
source content remains a conflict. Unchanged pre-v10 records may converge under
v10 compatibility; a command carrying the v9 binary-fidelity representation or
either v10 terminal-malformed representation is never offered a prior-adapter
compatibility signature.

## Tolerant parsing

Adapters are versioned tolerant tagged unions:

- known discriminators map to canonical message, tool call/result, error,
  compaction, lifecycle, and other earned event kinds;
- an unterminated final JSONL record in an active Codex rollout remains
  `Incomplete`, even when its current bytes are malformed JSON or invalid
  UTF-8. Newline termination, archive placement, or equivalent configured
  terminal evidence makes that exact byte range terminal; capture never treats
  a readable or decodable prefix as the complete record;
- terminal malformed JSON whose complete bytes decode as strict UTF-8 becomes
  one `opaque` event with canonical `unknown` actor because the source states no
  actor. Its scanned `opaqueText` is the complete record
  text, and `parseError` retains the fixed `json_parse_error` reason, fidelity
  policy version, trusted external session/optional child, source position,
  and `byte_range` locator kind. Capture does not repair it or guess a known
  tagged-union variant;
- terminal malformed JSON that cannot be decoded as strict UTF-8 becomes one
  content-free opaque omission, likewise with canonical `unknown` actor. The
  omission retains only the fixed
  `source_record_uninspectable` reason, original record-content byte count,
  `invalid_utf8` content policy, fidelity policy version, and the same trusted
  source identity/position/locator-kind provenance. It retains no decoded
  prefix, replacement-character text, or raw bytes. Both deterministic
  terminal outcomes advance normally after the universal safety gate;
- unsupported record or content variants become `opaque` events and retain
  their complete scanned source representation, including discriminators,
  additive fields, and safety-redacted sensitive evidence after ingestion;
- the adapter-owned unsupported-byte tagged union is an object with exact
  `type: "binary_content"`, one closed `category` value (`attachment`,
  `archive`, `executable`, `image`, or `audio`), and an integer
  `byte_payload` array whose members are all in the byte range. Before the
  local durable queue, capture removes only `byte_payload` and adds the
  content-free `unsupported_binary_content` fidelity omission under the
  non-colliding policy-owned `capture_fidelity_omission` field (or its lowest
  numeric suffix) with the exact array length, category, policy version, the
  trusted external session, optional child, source position and locator kind,
  plus available source-stated media type, local path, local identity, and
  no duplicated capture provenance. Source-stated capture provenance remains
  ordinary replayable sibling evidence after the same governed recursive
  rewrite, so nested valid `binary_content` cannot bypass the policy.
  Safe sibling metadata, a source-stated `omission`, and model-visible `text`
  likewise remain ordinary replayable evidence. If the safe field-level
  rewrite itself exceeds the active fidelity ceiling, recognition is retained
  and capture selects a content-free whole-observation
  `unsupported_binary_content` omission; the smaller raw representation never
  regains eligibility. Untagged values and malformed byte representations are
  not classified by this union. Only direct `signature`
  and `encrypted_content` children reached from the root known Codex reasoning
  payload or the root adapter-owned opaque envelope remain ordinary metadata.
  These are separate closed traversal contexts: a raw source record can earn
  only the root Codex `response_item` → `payload.type=reasoning` exception,
  while an adapter event can earn only the adapter-produced root
  `recordType=response_item`, `payloadType=reasoning`,
  `source.type=reasoning` exception when that `source` is structurally identical
  to the recognized root source payload's `payload`. This redundant projection
  lets the event replay only opaque metadata already admitted at the raw-source
  boundary; it cannot introduce additional bytes. Raw root fields cannot
  self-assert the adapter-event context, nested objects cannot self-assert
  either wrapper, and the same names in arbitrary objects do not exempt a
  nested valid `binary_content`;
  a local adapter that selects this binary omission for a `native_id` source
  fails closed before durable claim or queue with a content-free reason. Omitted
  same-count bytes cannot supply binding-stable change identity without retaining
  forbidden raw state or a fingerprint. A verified `byte_range` remains
  admissible because its source digest is binding-keyed downstream. This local
  rule does not reject direct authenticated API `native_id` commands: ingestion
  sees and signs their original raw command before applying canonical omission;
  there is no extension, entropy, or generic string classifier;
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
  `web_search_call`, and `image_generation_call` response items retain
  `call_id` (falling back to an explicitly stated item `id`) in both the event
  payload and deterministic part key. Persisted exec-command and patch-apply
  `event_msg` begin/end records remain evidence-bearing annotations rather than
  duplicate canonical calls or results.
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
- `fixtures/adapter-conformance/codex-cli-0.146.binary-media.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.77.parent-only.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.90.fork-only.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.120.parent-fork.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.nested-child.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-cli-0.144.absent-relationship.synthetic.jsonl`
- `fixtures/adapter-conformance/codex-terminal-malformed-readable.synthetic.txt`
- `fixtures/adapter-conformance/codex-terminal-invalid-utf8.synthetic.hex`
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
records. The 0.146 binary/media family covers all five closed unsupported-byte
categories, mandatory trusted source identity, safe metadata and model-visible
text retention, content-free original-byte-count omissions, root opaque
signature/encrypted negative controls, and a spoofed nested-wrapper regression.
The terminal-malformed fixtures cover complete readable parse-error evidence
including local redaction and content-free invalid-UTF-8 omission without
storing a replacement-decoded prefix.
The built `CodexCaptureTracer` consumes this family through scheduled transcript
discovery, authenticated capture, deterministic retry, and `memctl` operator
receipt reads; the legacy three-record compatibility fixture remains unchanged.

The five version-labelled relationship families cover parent-only, fork-only,
combined parent/fork, nested spawn, and no-parent shapes. They assert the
explicit stable child identity separately from every typed relationship and
round-trip dangling native targets without resolution or cross-stream order.

`CodexJsonlAdapter` is the only adapter referenced by the separately built
disabled tracer image. `DisposableClaudeJsonlAdapter` is defined in the test
assembly only. The test invokes the same `DisabledCaptureRuntime` and
authenticated `/capture/v1/observations` API used by the Codex tracer, then
reads the canonical result through `memctl`. No release project references the
Claude spike, and enrollment records a harness identity without selecting an
adapter.
