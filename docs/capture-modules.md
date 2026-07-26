# Capture modules

The capture spine (issue #74, stabilized by #120 and routed by #77) is five
public modules in `MemSrv.Core`, plus the never-store gate (#76) that all of
them cross. No module requires a caller to understand another's rules. This
note records the shape that exists; it decides nothing new.

Source interpretation before this spine is described by the
[harness-neutral capture adapter contract](capture-adapter-contract.md).

## Modules and callers

| Module | Public interface | Caller |
| --- | --- | --- |
| `CaptureEnrollment` | `EnrollAsync(stableName, harness, agentId, credential)` → binding uuid | `memctl capture enroll` |
| `CaptureRoutePolicyStore` | `ReplaceAsync(stableName, policy)` → policy uuid | `memctl capture route-policy` |
| `CaptureAuthority` | `ResolveAsync(credential)` → `CaptureBindingContext?` | `POST /capture/v1/observations` |
| `CaptureIngestion` | `ImportAsync(CaptureBindingContext, CaptureObservationCommand)` → `CaptureImportReceipt` | `POST /capture/v1/observations` |
| `OperatorCaptureReads` | `ReadCapturedEventEnvelopesAsync(observationUuid)` → `IReadOnlyList<CapturedEventEnvelope>` | `memctl capture receipt` |
| `NeverStoreGate` | `Scan`/`Redact`/`AssertAllowed` (free text), `ScanJson`/`RedactJson`/`RedactObject`/`AssertAllowedObject` (structured), `AssertObservationWithinBudget`, `TryReload`, `IsConfigured`/`FailureReason`/`RuleSetVersion`/`Budgets` | `MemoryService`, `CaptureEnrollment`, `CaptureIngestion`, `DisabledCaptureRuntime` |
| `ICaptureRuntimeState` | `ReadAsync`, `ClaimAsync`, `RecordServerReceiptAsync` | `CodexCaptureTracer` |
| `CodexCaptureClaimer` | `ClaimCompletedAsync(adapter, transcriptPath, sourceStream, state, safetyGate)` | `CodexCaptureTracer` |

`CaptureLedger` is internal: the single reader over the durable capture ledger
rows — observations, events, relationships — that ingestion and operator reads
both project into canonical facts.

## Invariants each interface hides

**`NeverStoreGate`** — the single governed policy point every write path
crosses, and the only type that knows rules exist. It hides the rule-set schema
and its load-time validation, compile-once `NonBacktracking` matchers with
per-rule timeouts, literal prefilters, deterministic overlap resolution, exact
span redaction, whole-leaf omission, one bounded percent/hex/Base64 decoding
level around high-confidence rules, operator-provisioned exact literals, and
every numeric scan budget. Callers pass a value and get back a sanitized value
or a refusal. Construction never throws — a broken rule file must not stop the
server from starting and rejecting an unknown credential first — so an unusable
gate is constructible, reports `IsConfigured == false` plus a safe
`FailureReason`, and throws `SafetyConfigurationException` from every governed
call. Free text and structured documents are separate entry points on purpose:
serialized JSON is never regex-rewritten. See
[capture safety budgets](capture-safety-budgets.md).

**`CaptureEnrollment`** — fail-closed safety configuration; never-store
clearance of the stable name, harness, and derived agent id; the `mcap_`
credential form; and a nonempty source-stated harness identity.
Callers pass strings and get a binding uuid. Enrollment records a harness
identity but does not select or ship an adapter.

**`CaptureRoutePolicyStore`** — atomic, binding-scoped prospective policy
replacement through an append-only version row. It canonicalizes raw repository
patterns, remote keys, directory paths, and repository targets for every caller,
requires repository targets to match the binding's allowed patterns, requires
special aliases to resolve to existing non-reserved namespaces, and never edits
an established stream.

**`CaptureAuthority`** — the only place a raw capture credential is compared.
It applies the structural `mcap_` pre-check (a non-capture-form credential is
rejected with no database round trip), hashes the credential, and requires an
active binding. A non-null result *is* the authorization decision and carries
every fact ingestion needs: binding uuid, harness, agent id, the latest
binding-scoped routing policy, and the per-binding content-signature key.
`null` means "reject before reading the body".

**`CaptureIngestion`** — contract version, binding/harness agreement, unique
part keys, relationship shape, the versioned 128 MiB observation ceiling, the
never-store gate, the binding-keyed retry signature (which covers the
`byte_range` source content digest that is signed but never persisted),
evidence-driven route derivation and fixation on first import, contiguous
checkpoint advance, locator idempotency and conflict, and the single
transaction over observation + events + relationships + checkpoint.
It never resolves a credential; authorization arrives already decided.

**`OperatorCaptureReads`** — envelope assembly. It returns complete versioned
`CapturedEventEnvelope` values (contract version, immutable observation, one
canonical event, that event's relationships). `memctl` serializes them and does
nothing else. Reads use already-sanitized durable rows and do not require
scanner configuration.

**`ICaptureRuntimeState`** — one durable local progress boundary. A claim
atomically records the verified transcript prefix, advances `enqueuedThrough`,
and adds one retryable queue item. The item contains the capture source stream,
deterministic transcript/position/byte-range/prefix locator evidence, source
position, and the redacted-safe candidate observation. It never stores the raw
transcript record. Recording a server receipt atomically removes exactly the
earliest matching responsibility only when the status is `new` or
`already_accepted`; every other response leaves it queued. Last known server
receipt is separate stream state rather than queue or locator identity; request
and delivery-batch IDs do not appear. The first conclusive server receipt also
establishes the server-derived canonical source-stream UUID in stream state.
Every later conclusive receipt must return that same UUID, and its top-level and
nested observation UUIDs must agree, before local responsibility can retire.
`FileCaptureRuntimeState` implements the transaction as a flushed complete
snapshot followed by an atomic rename, under a process-shared lock file.

**`CodexCaptureClaimer`** — verifies the previously recorded append-only prefix
against the read-only transcript, defers an unterminated final JSONL record,
adapts a completed record, runs the local safety boundary, and only then calls
the durable claim transaction. Its locator identity binds transcript identity,
source position, byte range and record digest, plus the new verified-prefix
evidence.

**`DisabledCaptureRuntime`** — orders durable responsibility by source
position, revalidates each queued candidate against the source fixture, and
sends it through the ordinary authenticated capture endpoint. It advances to a
later item only after the receipt callback has durably retired the earlier one.
The packaged tracer accepts only `new` and `already_accepted` receipts whose
position and returned byte-range locator match the queued responsibility, whose
top-level and nested observation UUIDs agree, and whose server-derived source
stream UUID matches the durable binding established by the first conclusive
receipt. Outages, malformed or unknown responses, identity mismatches, and lost
success responses therefore leave the item queued for restart.

## Where the gate runs

The Codex claimer crosses the same `NeverStoreGate` before a candidate enters
the local durable queue. The existing disabled delivery runtime scans the
original observation again before it leaves the tracer process, and the server
crosses the gate independently before canonical append. All three use the same
governed rule semantics because they construct the same gate implementation;
there is no second scanner implementation. Local candidate sanitization
evidence is not canonical server scan provenance: the server remains the sole
author of the canonical `scan_*` columns when delivery occurs.

The two sides do different things with the result. The runtime calls exactly two
gate methods — `AssertObservationWithinBudget` and `ScanJson` — and **refuses on
scan failure**: a budget exhaustion, a matcher timeout, an internal scanner
error, or an unusable rule set throws out of `ScanJson`, so it emits nothing,
exits non-zero, and says why on stderr. An omission is not a refusal — a leaf
past its byte limit, a sensitive property name carrying a subtree, or a
redaction-caused name collision is a recorded fidelity outcome that the server
persists *as* an omission, not an unscanned tail, so the runtime still sends
those observations. The runtime discards the scan result and does **not**
rewrite the payload it transmits. If it did, the server would scan
already-sanitized bytes and record `scan_status = "clean"` with no rule ids for
content that was in fact redacted. Imported content supplies evidence only and
cannot assert its own scan provenance, so the server — which sanitizes what it
appends — remains the sole author of the canonical `scan_*` columns.

`AssertAllowed`/`AssertAllowedObject` are **server-side only**: they are the
reject door, and rejecting is something only the side that owns the durable
write can do. `CaptureEnrollment` asserts on the stable name, harness, and agent
id; `CaptureIngestion` asserts on a required identity value whose scan came back
with a redaction or an omission, because such a value cannot be redacted or
omitted and still mean what it claims; `MemoryService` asserts on memory-write
content, which Phase 1 rejects rather than redacts. Everything else the server
persists goes through `Scan`/`ScanJson` and is redacted in place.

## Source locator representation

`CaptureLocator` is a wire DTO: flat, nullable, and able to express an invalid
mix of `nativeId` and byte-range fields, because arbitrary JSON can. The HTTP
seam calls `CaptureObservationCommand.FromRequest`, which parses it through
`CaptureSourceLocator.Parse` into a closed hierarchy — `NativeId(value)` or
`ByteRange(offset, length, sourceContentSha256)`. The private parameterless
constructor rules out accidental or positional derivation, so a mixed locator is
unrepresentable through the parse path rather than merely rejected; the
*protected* copy constructor every record synthesizes remains a deliberate-abuse
escape hatch, which is not defended against because #120 asks only that the
variants cannot be mixed by accident. Parse failures are `ArgumentException` →
`400`. A locator rebuilt from the ledger is a `ByteRange` with a null digest,
because the digest is signed but never stored.

`CaptureSourceLocator` also owns both directions of its persistence projection —
`ToColumns()` and `FromColumns()` — so the four `capture_observations` locator
columns cannot be written one way and read back another.

## One set of canonical facts

`CaptureObservationReceipt`, `CanonicalCapturedEvent`, `CaptureRelationship`,
and `CaptureScanReceipt` are the authoritative facts. Import responses and
operator reads compose the same records: `CaptureImportReceipt` carries the
observation plus `CapturedEventReceipt` (a canonical event with its
relationships, serialized flat), and `CapturedEventEnvelope` carries the
observation plus one canonical event and its relationships. There is no second
receipt model for a caller to translate between.
