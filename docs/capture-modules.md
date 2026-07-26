# Capture modules

The capture spine (issue #74, stabilized by #120) is four public modules in
`MemSrv.Core`. No module requires a caller to understand another's rules. This
note records the shape that exists; it decides nothing new.

## Modules and callers

| Module | Public interface | Caller |
| --- | --- | --- |
| `CaptureEnrollment` | `EnrollAsync(stableName, harness, agentId, credential, routeNamespace)` → binding uuid | `memctl capture enroll` |
| `CaptureAuthority` | `ResolveAsync(credential)` → `CaptureBindingContext?` | `POST /capture/v1/observations` |
| `CaptureIngestion` | `ImportAsync(CaptureBindingContext, CaptureObservationCommand)` → `CaptureImportReceipt` | `POST /capture/v1/observations` |
| `OperatorCaptureReads` | `ReadCapturedEventEnvelopesAsync(observationUuid)` → `IReadOnlyList<CapturedEventEnvelope>` | `memctl capture receipt` |

`CaptureLedger` is internal: the single reader that projects durable rows into
canonical facts, shared by ingestion and operator reads.

## Invariants each interface hides

**`CaptureEnrollment`** — fail-closed safety configuration; never-store
clearance of the stable name and derived agent id; the `mcap_` credential form;
the codex-only restriction of this disabled slice; existence of the route
namespace. Callers pass strings and get a binding uuid.

**`CaptureAuthority`** — the only place a raw capture credential is compared.
It applies the structural `mcap_` pre-check (a non-capture-form credential is
rejected with no database round trip), hashes the credential, and requires an
active binding. A non-null result *is* the authorization decision and carries
every fact ingestion needs: binding uuid, stable name, harness, agent id, route
namespace, allowed namespaces, and the per-binding content-signature key.
`null` means "reject before reading the body".

**`CaptureIngestion`** — contract version, binding/harness agreement, unique
part keys, relationship shape, the observation size ceiling, the never-store
gate, the binding-keyed retry signature (which covers the `byte_range` source
content digest that is signed but never persisted), route fixation on first
import, contiguous checkpoint advance, locator idempotency and conflict, and
the single transaction over observation + events + relationships + checkpoint.
It never resolves a credential; authorization arrives already decided.

**`OperatorCaptureReads`** — envelope assembly. It returns complete versioned
`CapturedEventEnvelope` values (contract version, immutable observation, one
canonical event, that event's relationships). `memctl` serializes them and does
nothing else. Reads use already-sanitized durable rows and do not require
scanner configuration.

## Source locator representation

`CaptureLocator` is a wire DTO: flat, nullable, and able to express an invalid
mix of `nativeId` and byte-range fields, because arbitrary JSON can. The HTTP
seam calls `CaptureObservationCommand.FromRequest`, which parses it through
`CaptureSourceLocator.Parse` into a closed hierarchy — `NativeId(value)` or
`ByteRange(offset, length, sourceContentSha256)`. The base constructor is
private, so those two variants are the whole world and a mixed locator is
unrepresentable past the seam rather than merely rejected. Parse failures are
`ArgumentException` → `400`. A locator rebuilt from the ledger is a `ByteRange`
with a null digest, because the digest is signed but never stored.

## One set of canonical facts

`CaptureObservationReceipt`, `CanonicalCapturedEvent`, `CaptureRelationship`,
and `CaptureScanReceipt` are the authoritative facts. Import responses and
operator reads compose the same records: `CaptureImportReceipt` carries the
observation plus `CapturedEventReceipt` (a canonical event with its
relationships, serialized flat), and `CapturedEventEnvelope` carries the
observation plus one canonical event and its relationships. There is no second
receipt model for a caller to translate between.
