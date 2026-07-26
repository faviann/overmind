# Harness-neutral capture adapter contract

Issue #75 introduces one adapter seam in `src/CaptureAdapters`. It interprets
trusted local source material; authentication, routing, deterministic safety
scanning, persistence, and checkpoint advancement remain owned by the capture
server.

## Input and outcome

`TrustedSourceObservation` identifies one source record or hook fact with:

- the source session and numeric source position;
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

## Tolerant parsing

Adapters are versioned tolerant tagged unions:

- known discriminators map to canonical message, tool call/result, error,
  compaction, lifecycle, and other earned event kinds;
- unsupported record or content variants become `opaque` events and retain
  their scanned source representation;
- unknown additive fields remain in `sourcePayload`;
- content and output accept string or array forms;
- known migrating fields accept scalar or object forms;
- tool arguments accept structured values or JSON-encoded strings.

Part keys come from a stable source path or native part identity and are
deterministic for the same immutable observation. Source timestamps are never
promoted to event occurrence timestamps.

## Conformance fixtures and disposable Claude spike

The synthetic, version-labelled families are:

- `fixtures/adapter-conformance/codex-cli-0.144.synthetic.jsonl`
- `fixtures/adapter-conformance/claude-code-2.1.201.synthetic.jsonl`

Both pass through the same conformance assertions for messages, successful and
failed tools, turn failures, compaction, subagents, unknown records, drift
shapes, provenance, relationships, and deterministic part identities.

`CodexJsonlAdapter` is the only adapter referenced by the separately built
disabled tracer image. `DisposableClaudeJsonlAdapter` is defined in the test
assembly only. The test invokes the same `DisabledCaptureRuntime` and
authenticated `/capture/v1/observations` API used by the Codex tracer, then
reads the canonical result through `memctl`. No release project references the
Claude spike, and enrollment records a harness identity without selecting an
adapter.
