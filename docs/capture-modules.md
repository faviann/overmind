# Capture modules

The capture spine (issue #74, stabilized by #120 and routed by #77) is six
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
| `CaptureFidelityPolicy` | `SerializeForTransport(CaptureObservationRequest, maxBytes)` / `SerializeForContent(CaptureObservationCommand, maxBytes)` → `BoundedCaptureRepresentation<T>` | `CodexCaptureClaimer`, `DisabledCaptureRuntime`, `CaptureIngestion` |
| `OperatorCaptureReads` | `ReadCapturedEventEnvelopesAsync(observationUuid)` → `IReadOnlyList<CapturedEventEnvelope>`; `ReplaySourceStreamAsync(sourceStreamUuid)` → `CapturedSourceStreamReplay`; `NavigateCapturedSessionAsync(sourceStreamUuid, allowedNamespaces)` → `CapturedSessionNavigation` | `memctl capture receipt`; `memctl capture replay`; `memctl capture navigate` |
| `NeverStoreGate` | `Scan`/`Redact`/`AssertAllowed` (free text), `ScanJson`/`RedactJson`/`RedactObject`/`AssertAllowedObject` (structured), `AssertObservationWithinBudget`, `TryReload`, `IsConfigured`/`FailureReason`/`RuleSetVersion`/`Budgets` | `MemoryService`, `CaptureEnrollment`, `CaptureIngestion`, `DisabledCaptureRuntime` |
| `ICaptureRuntimeState` | `ReadAsync`, `InspectSourceAsync`, `ClaimAsync`, `DeliverAuthorizedAsync`, `RecordServerReceiptAsync` | `CodexCaptureTracer` |
| `CodexCaptureClaimer` | `ClaimCompletedAsync(adapter, transcriptPath, sourceStream, state, safetyGate)` | `CodexCaptureTracer` |
| `CodexTranscriptDiscovery` | `Enumerate(configuredLocation)` → streams with explicit Codex source identity | `CodexCaptureTracer` |
| `CodexTranscriptScanCycle` | `RunAsync(streams, scanStream, reportFailure)` | `CodexCaptureTracer` |
| `CaptureRescanScheduler` | `RunAsync(scanCycle, schedule, jitterSource, delay)` | `CodexCaptureTracer` |
| `CaptureRescanConfiguration` | `Load(readEnvironment)` → `CaptureRescanSchedule` | `CodexCaptureTracer` |

`CaptureLedger` is internal: the single reader over the durable capture ledger
rows — observations, events, relationships — that ingestion and operator reads
both project into canonical facts.

## Invariants each interface hides

**`CaptureFidelityPolicy`** — owns the complete deterministic serialization
boundary: stream the original serialization through a byte-counting discard
sink, retain and materialize it only after proving it fits, recheck the actual
UTF-8 size of that materialized string, or replace it with the versioned
whole-observation omission. The compact result is refused if it does not fit.
Counting uses the published `MaxScanTime` as a fixed deadline and fails closed
without reporting a guessed original byte count if serialization cannot
finish. Callers receive the chosen value and its exact serialized
representation as one outcome; they do not repeat byte counting, reconstruct
policy output, or infer omission from object identity. Fidelity counting and
content-signature hashing share one governed write-only serialization stream;
only their discard and keyed-hash sinks differ. An injected transport bound can
only tighten the fixed 1,000,000-byte production bound, an injected content
bound can only tighten the fixed 128 MiB production bound, and both must be
positive. Transport compaction
first runs the original request through
`CaptureObservationCommand.FromRequest`: conflicting dual identity, a missing
mandatory identity, and an invalid locator are refused before counting,
compaction, claim, or delivery. A supported legacy-only `sourceSessionId`
canonicalizes into top-level `sourceIdentity` in the omission before the legacy
field is cleared, and that same identity is repeated in omission provenance.
Mandatory source identity and locator values are never truncated or
fingerprinted to make either bound fit.

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
part keys, relationship shape, deterministic whole-payload omission above the
versioned 128 MiB observation ceiling, the never-store gate, the binding-keyed
retry signature (which covers the
`byte_range` source content digest that is signed but never persisted),
evidence-driven route derivation and fixation on first import, contiguous
checkpoint advance, locator idempotency and conflict, and the single
transaction over observation + events + relationships + checkpoint.
It never resolves a credential; authorization arrives already decided.
Before fidelity selection it validates only the mandatory authority and
identity facts retained by an omission. It validates optional semantic content
after selection, against a stable command reconstructed from the exact bounded
serialization that passed the effective ceiling. In-limit signatures use that
same stable original snapshot; over-limit signatures continue to stream the
original content while scan and persistence use only the bounded omission.

**`OperatorCaptureReads`** — envelope, operator replay, and captured-session
navigation assembly. Receipt
reads return complete versioned `CapturedEventEnvelope` values (contract
version, immutable observation, one canonical event, that event's
relationships). A one-stream replay wraps those envelopes unchanged, labels
its order basis as durable `capture_observations.source_position` followed by
source-stated `captured_events.part_order`, and adds no global or session-wide
ordinal. Navigation requires at least one explicit allowed namespace, rejects
an unavailable starting stream without distinguishing absent from unauthorized,
and resolves only stored `parent_session`, `spawned_by`, and `forked_from`
session evidence. A native target resolves only when its binding-scoped
captured identity is unique; an explicit target-stream UUID remains exact.
Absent, ambiguous, and unauthorized outgoing targets all retain the immutable
source evidence while exposing a null related session with `unavailable`
status. Incoming edges are returned only when both their source session and
resolved target are authorized. A later target capture can therefore change
only this read answer. Navigation does not add confidence, chronology,
inferred edges, or an order. `memctl` serializes these read models and does
nothing else. Reads use already-sanitized durable rows and do not require
scanner configuration.

**`ICaptureRuntimeState`** — one durable local progress boundary. A claim
atomically records the verified transcript prefix, advances `enqueuedThrough`,
and adds one retryable queue item. It revalidates the current durable prefix
against the claimant's immutable byte snapshot under the same process-shared
lock: a same-history concurrent advance converges without another queue item,
while a changed prefix or transcript identity is durably stopped before the
lock is released. The item contains the capture source stream,
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
Stopping a stream atomically records one finite, content-free reason code and,
only when source evidence identifies one exact record, the affected source
position without changing its verified prefix,
`enqueuedThrough`, queued responsibility, last receipt, or canonical stream
UUID. The first stop is sticky: later scans, claims, deliveries, receipts, and
competing stop attempts cannot advance or replace it. A server response whose
machine reason is `blocked_by_earlier_gap` stops at the earliest attempted
position, so every later queued item remains visibly blocked behind it.
Aggregate verified-prefix digest and transcript-identity conflicts prove that
history changed but not which record changed, so those stop states omit
`sourcePosition`; queued-record revalidation, earlier-gap, and accepted-source
conflicts retain their exact position. Once any of these conflicts is detected,
the local stop write completes independently of caller cancellation, including
while waiting for the process-shared state lock.
Delivery authorization, the bounded HTTP attempt, and conclusive receipt
retirement form one process-shared state transaction. This serializes a durable
stop against a delivery that began from an older queue snapshot: after the stop
commits, that delivery cannot enter its external callback. A failed or
cancelled callback leaves responsibility queued and releases the shared lock;
the runtime's response timeout bounds a stalled endpoint, so authorization
cannot become a fail-open lease or a durable lock.
Source-prefix and transcript-identity inspection likewise run under that
process-shared transaction: a detected conflict is durably stopped before the
lock is released, and the stop write no longer observes caller cancellation.
Delivery owns the content-free mapping from queued-evidence mismatches and the
server's conflict reason values to the same durable stop codes; the packaged
host does not interpret those rules.
`FileCaptureRuntimeState` implements the transaction as a flushed complete
snapshot followed by an atomic rename, under a process-shared lock file.

**`CodexCaptureClaimer`** — verifies the previously recorded append-only prefix
against one immutable transcript byte snapshot, parses and adapts records from
that same snapshot, defers an unterminated final JSONL record,
and accepts that record only after newline completion or an explicit terminal
flag from configured discovery. It adapts a terminal record, runs the local
safety boundary, and only then calls the durable claim transaction. Its locator
identity binds transcript identity, source position, byte range and record
digest, plus the new verified-prefix evidence.

**`CodexTranscriptDiscovery`, `CodexTranscriptScanCycle`,
`CaptureRescanConfiguration`, and `CaptureRescanScheduler`** — enumerate every
synthetic JSONL stream under the configured location at startup and afresh on
each later cycle. Ordinary configured files use their absolute path identity.
Under the Codex-shaped `sessions/` and `archived_sessions/` subtrees, discovery
uses the rollout's unique filename to retain one logical identity when a nested
`sessions/YYYY/MM/DD/<filename>` rollout moves to the flat
`archived_sessions/<filename>` location, and marks only that flat archived
location terminal at EOF. When a rollout contains `session_meta`, its explicit
external-session/optional-child tuple replaces path identity; the filename
behavior remains the compatibility fallback for synthetic or legacy inputs
without metadata. Simultaneous active/archive copies or two active
rollouts with the same filename are rejected as ambiguous rather than silently
conflated. The directory move is stable filesystem evidence; elapsed time and
inactivity never establish terminality. Stream identity remains independent of
enumeration order. A rollout whose identity cannot be resolved — unreadable, or
self-contradictory about its own child classification — carries that failure
instead of an identity, and never a guessed one. The scan cycle isolates such a
stream, and one that disappears or becomes unreadable after enumeration: that
attempt advances nothing, later enumerated streams still run, and the next
scheduled enumeration gets another chance. It does not catch cancellation. Configuration binds the
named interval and maximum-jitter environment inputs to one validated schedule.
Startup enumeration runs immediately; only after a complete cycle does the
scheduler choose a new bounded jitter sample and wait the configured interval
plus that jitter. The scheduler awaits each whole
enumeration/claim/delivery cycle, so a slow cycle cannot overlap another. The
packaged tracer retains per-stream failures for a later cycle rather than
letting one outage cancel responsibility for other configured streams.

The server combines the discovered tuple with the authenticated binding,
persists its components on `capture_source_streams`, and derives a deterministic
stream UUID and canonical trace-session ID for a new stream. Migration recovers
an accepted Codex tuple from its immutable `session_meta` observation and
preserves the trace-session ID already carried by that stream's events; later
positions reuse the durable ID rather than deriving a second identity.
Parent/fork facts are excluded. Resume, retry, archive movement, and historical
rediscovery therefore converge, while distinct child identities under one
external session cannot collide.

Parent, fork, nested spawn, and explicit child-classification facts are adapter
output attached to the session-metadata context event; none participates in
the source identity tuple above. Parent, child, and sibling rollouts therefore
retain separate durable runtime queues and separate server checkpoints. A
filesystem failure, delivery conflict, or earlier gap stops only the affected
stream. The scan cycle continues other streams, and capture stores no merged
cross-stream ordinal; operator replay remains one-stream source position plus
event part order.

**`DisabledCaptureRuntime`** — orders durable responsibility by source
position, revalidates each queued candidate against the source fixture, and
sends it through the ordinary authenticated capture endpoint. It advances to a
later item only after the receipt callback has durably retired the earlier one.
The packaged tracer accepts only `new` and `already_accepted` receipts whose
position and returned byte-range locator match the queued responsibility, whose
top-level and nested observation UUIDs agree, and whose server-derived source
stream UUID matches the durable binding established by the first conclusive
receipt. Outages, malformed or unknown responses, identity mismatches, and lost
success responses therefore leave the item queued for retry. Each HTTP attempt
has a five-second response bound. Expiring that internal bound becomes a
retryable delivery timeout for the scheduler; cancellation from the scheduler
remains `OperationCanceledException` and is never reclassified or swallowed.

The Codex compaction conformance path uses this unchanged runtime for persisted
old/new rollout families. It preserves source-position order across canonical
pre-boundary history, a completion observation, and the
`context_compacted` annotation. Authenticated ingestion returns immutable
observation UUIDs, retries return those same UUIDs as `already_accepted`, and
`memctl capture receipt` exposes the operation phase, boundary, summary,
replacement history, and window evidence. Hook facts use the same authenticated
capture API and operator receipt seam directly: there is no hook claimer or
hook runtime in this slice. The runtime does not interpret a summary as missing
history or replace an earlier queued or canonical record.

## Where the gate runs

The Codex claimer applies the fixed 1,000,000-byte production transport bound
before a candidate enters the local durable queue. Tests may inject only a
tighter positive bound; an injected value above production is clamped and
cannot admit a raw observation production would omit. A runtime observation
above the effective bound becomes a compact whole-observation omission before
durable queueing or transmission only for a verified `byte_range`, whose source
digest is covered by the binding-keyed ingestion signature. An over-limit
`native_id` observation lacks equivalent binding-stable content identity and
fails closed before claim, queue, or transmission; capture adds neither an
unkeyed fingerprint nor a credential-rotation-sensitive key. The fidelity
policy mechanically verifies that the omission itself fits. The omission retains the
authenticated harness, source identity, source position, and locator required
for ingestion and idempotency. Its source timestamp, source/adapter descriptive
metadata, route evidence, and original semantic content are absent; the
policy-owned omission provenance repeats the required source-identity tuple but
contains no source-content digest or excerpt. A mandatory identity or locator
that cannot fit is refused rather than truncated or fingerprinted. The claimer
then scans that bounded representation. An
observation within the bound retains its original payload and is scanned as
such. The existing disabled delivery runtime reconstructs the same bounded
representation, scans it again before it leaves the tracer process, and the
server crosses the gate independently before canonical append. All three use
the same governed rule semantics because they construct the same gate
implementation; there is no second scanner implementation. Local candidate
sanitization evidence is not canonical server scan provenance: whether the wire
carries the in-limit original observation or the compact transport omission,
the server remains the sole author of the canonical `scan_*` columns when
delivery occurs.

The two sides do different things with the result. The runtime calls exactly two
gate methods — `AssertObservationWithinBudget` and `ScanJson` — and **refuses on
operational scan failure**: a budget exhausted while scanning the bounded
representation, a matcher timeout, an internal scanner error, or an unusable
rule set throws, so it emits nothing and says why on stderr. The one-shot
compatibility mode exits non-zero; the scheduled synthetic mode retains
responsibility and retries a later cycle. An omission is not a refusal — a leaf
past its byte limit, a sensitive property name carrying a subtree, or a
redaction-caused name collision is a recorded fidelity outcome that the server
persists *as* an omission, not an unscanned tail, so the runtime still sends
those observations. Apart from the deterministic whole-observation transport
omission, the runtime discards the scan result and does **not** rewrite the
payload it transmits. Replacing an in-limit original payload with its scan
result would make the server scan already-sanitized bytes and record
`scan_status = "clean"` with no rule ids for content that was in fact redacted.
Imported content supplies evidence only and cannot assert its own canonical
scan provenance, so the server — which sanitizes what it appends — remains the
sole author of the canonical `scan_*` columns.

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
receipt model for a caller to translate between. `CapturedSessionNavigation`
is deliberately a derived read model over those facts and persisted stream
identity/scope; it never becomes another canonical receipt.
