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

## Where the gate runs

The disabled runtime (`CaptureAdapters.DisabledCaptureRuntime`) crosses the same
`NeverStoreGate` before an observation leaves the tracer process, and the server
crosses it again independently before canonical append. There is no local
durable queue in this slice, so "scan before durable local persistence" means
"scan before the observation is emitted". Both sides use the same governed rule
semantics because both construct the gate from the same configuration; there is
no second scanner implementation.

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
