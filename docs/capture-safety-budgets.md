# Legacy capture observation-size and fidelity contract

Status: **frozen — capture-only**. This document exists only to maintain the
legacy capture code that still ships. It authorizes no new capture work and is
not the Phase 1 write-safety contract. General never-store rule loading,
scanning, markers, failure codes, and numeric scan budgets are binding in
[`write-safety.md`](write-safety.md).

## Whole-observation policy

`CaptureFidelityPolicy.ProductionContentBytes` is fixed at 134,217,728 bytes
(128 MiB) per source observation. A positive injected content bound may tighten
but never loosen that ceiling. `CaptureIngestion` uses the same policy. This
limit is not present in `WriteSafetyBudgets` and does not apply to Phase 1
memory or trace records as a whole.

An original observation above the effective content bound is replaced with a
compact, explicit `observation_exceeds_content_limit` representation. The
representation is scanned through `WriteSafetyGate`, persisted, and advances
the capture checkpoint. Required source identity, position, and locator values
are neither truncated nor replaced with unkeyed fingerprints; if those values
cannot fit, capture fails closed.

JSON byte measurement streams into a discarding counter before materialization
and shares the fixed 30-second write-safety deadline. A believed-in-limit value
is materialized and checked again. Mutable input cannot cause an over-limit
representation to escape the policy. Over-limit retry signatures stream the
original representation into the binding-keyed hash without persisting it.

The explicit `binary_content` classifier shares the same absolute deadline and
effective content bound across prepass, rewrite, parsing, and materialization.
If a safe field rewrite exceeds the bound, capture emits the bounded
`unsupported_binary_content` whole-observation representation or fails closed;
it never returns the raw record.

Capture outcome projections remain content-free. Fidelity omissions advance
with health `healthy` and fidelity `degraded`; generalized write-safety
failures are mapped outside the write-safety boundary to health `blocked`.
Counters expose only the closed harness, reason, and size-band vocabulary, not
exact sizes, record identity, credentials, locators, excerpts, or digests.

## Transport policy

`POST /capture/v1/observations` retains its independent 1,000,000-byte request
cap. That cap is a denial-of-service guard, not a write-safety scan budget.
Runtime transport compaction may only advance a verified `byte_range` record,
whose source digest participates in the binding-keyed signature. An over-limit
`native_id` record fails closed before claim because it lacks stable content
identity for changed-content detection.

## Focused evidence

`CaptureObservationSizeTests` owns the real 128 MiB mechanism proof. Other
capture fidelity tests may inject smaller positive content bounds through
`CaptureIngestion` or the documented `CaptureFidelityPolicy` entry points.
Generalized scanner coverage stays in `WriteSafetyTests` and
`WriteSafetyBoundaryTests`; those classes never construct capture values.
