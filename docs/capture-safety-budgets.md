# Capture safety budgets and rule-set contract

Published defaults for the deterministic never-store detector (issue #76). The
numbers below are versioned runtime constants in
[`SafetyBudgets`](../src/MemSrv.Core/SafetyBudgets.cs), not configuration: an
operator cannot loosen them from a file. Tests inject smaller budgets where the
mechanism, not the number, is what is under test.

Budget set version: **`capture-safety-budgets/2026-07-26.1`**.
Shipped rule set version: **`never-store/2026-07-26.1`** (the runtime version
string appends a SHA-256 prefix of the rule file and, when present, the *count*
of operator literals — never their values).

## Numeric defaults

| Budget | Default | Unit | Exceeded → |
| --- | ---: | --- | --- |
| `MaxObservationBytes` | 134,217,728 (128 MiB) | UTF-8 bytes per source observation | fail closed |
| `MaxLeafBytes` | 67,108,864 (64 MiB) | UTF-8 bytes per decoded structured leaf | leaf wholly omitted (fail closed if the value is a required identity) |
| `MaxScanTime` | 30 | seconds per scan call | fail closed |
| `MaxRuleTime` | 5 | seconds per rule matcher | fail closed |
| `MaxMatches` | 10,000 | matches per scan call | fail closed |
| `MaxDecoderCandidates` | 65,536 | encoded runs decoded per scan call | fail closed |
| `MaxDecoderCandidateLength` | 4,096 | characters | run does not qualify as a decode candidate (see below) |
| `MaxDecodedBytes` | 16,777,216 (16 MiB) | decoded bytes per scan call | fail closed |

"Fail closed" means the scan throws `SafetyScanException`, ingestion persists
nothing, and the stream checkpoint does not advance. The next legitimate record
is still accepted at the same source position.

`MaxDecoderCandidateLength` is deliberately **not** a fail-closed budget. A run
longer than the cap is not decoded, but it was already scanned in full, in its
undecoded form, by every rule — no tail is left uninspected. Failing an entire
capture closed because a transcript contained one long Base64 blob would trade
a real availability loss for no safety gain.

## Measured evidence

Measured on the development host against the shipped rule set, Release build,
`RegexOptions.NonBacktracking`, warm process, one leaf at a time.

| Workload | Bytes | Decode candidates | Scan time | Throughput |
| --- | ---: | ---: | ---: | ---: |
| Prose, no encoded runs | 235,690 | 0 | 8.1 ms | 27.8 MiB/s |
| Prose, no encoded runs | 2,088,890 | 0 | 149.5 ms | 13.3 MiB/s |
| GUID-dense log output | 229,890 | 8,400 | 294.0 ms | 0.7 MiB/s |
| GUID-dense log output | 996,890 | 36,000 | 419.5 ms | 2.3 MiB/s |
| GUID-dense log output | 4,020,890 | 144,000 | 2,245.4 ms | 1.7 MiB/s |
| 64 MiB opaque run (one over-length candidate) | 67,108,864 | 1 | 1,918.7 ms | 33.4 MiB/s |
| 64 MiB leaf, one credential at its final bytes | 67,108,864 | 2 | 2,868–3,478 ms | ~19 MiB/s |

Per-rule matcher cost over the same 64 MiB leaf: `aws-access-key-id` 288.5 ms;
Base64 candidate extraction 692.0 ms; hex 190.0 ms; percent 19.3 ms; a
sixteen-rule literal prefilter sweep 424.4 ms. Peak working set for the 64 MiB
case was 234 MiB (input plus the rebuilt redacted copy).

How each default follows from those numbers:

- **`MaxScanTime` = 30 s.** A leaf at the 64 MiB limit costs ~3.5 s warm in
  Release. Thirty seconds leaves roughly an 8× margin for a Debug build, a
  loaded host, and four concurrent test shards, while still bounding the
  worst case.
- **`MaxRuleTime` = 5 s.** The slowest single matcher over a limit-sized leaf
  measured 692 ms; five seconds is a ~7× margin. A shorter timeout — 250 ms was
  the first candidate — fails a legitimate limit-sized leaf closed.
- **`MaxDecoderCandidates` = 65,536.** The observed transcript-volume ceiling is
  a 236,273-byte record (issue #67); GUID-dense output of that size produced
  8,400 candidates. 65,536 admits roughly eight such records' worth of density
  while capping decode work near one second at the measured 1.7–2.3 MiB/s.
- **`MaxDecodedBytes` = 16 MiB.** 65,536 candidates at the 4,096-character cap
  could in principle decode far more; 16 MiB is the real backstop and is ~70×
  the observed maximum record.
- **`MaxMatches` = 10,000.** No legitimate 236 KB record carries ten thousand
  distinct credentials; a flood at that scale is pathological input.
- **`MaxDecoderCandidateLength` = 4,096.** Longer than any provider token format
  in the rule set, short enough that decoding cost stays linear in practice.

Re-run the measurement whenever a rule is added or a pattern changes, and bump
the budget-set version if a default moves.

### Cost of exercising the real numbers in `make test`

`SafetyBoundaryTests` is the only place the production 128 MiB and 64 MiB
numbers are materialized. All three of its tests live in one class so xUnit runs
them sequentially, and each releases its value before the next allocates.
Measured on the same host, Debug build: **5 s** wall for the class, **571 MiB**
peak test-host RSS against a **207 MiB** control (a same-shard run of
`SafetyGateTests`) — roughly 364 MiB attributable to the boundary values. All
four `make test` shards together stay under ~1.2 GiB.

Redacted output is written with `string.Create` at its exact final length rather
than through a `StringBuilder`, which is what keeps a limit-sized leaf to one
extra copy instead of three.

## Why the transport cap is below the scanner limit

`POST /capture/v1/observations` rejects a body over 1,000,000 bytes with `413`
before authentication-independent parsing. That cap is a **denial-of-service
guard for the disabled tracer**, not the safety limit. It is deliberately three
orders of magnitude below `MaxObservationBytes` because:

- the observed maximum source record is 236,273 bytes, so 1 MB is already ample
  headroom for the only producer that exists;
- a small transport cap means an unauthenticated or hostile client cannot make
  the server allocate a 128 MiB buffer;
- the scanner limit must be sized for the *content* policy (what a future
  importer may legitimately carry), not for what today's HTTP route accepts.

A consequence worth stating plainly: with the 1 MB cap in place, the
observation-size, decoder-candidate, scan-time, and matcher-timeout budgets are
not reachable through the HTTP route. They are exercised at the
`NeverStoreGate` and `CaptureIngestion` module surfaces instead, with injected
budgets, and the match-count budget is exercised end-to-end over HTTP.

## Rule-set schema

`config/never_store.yaml` carries a `version` and a nonempty `rules` list. Each
rule requires:

| Field | Meaning |
| --- | --- |
| `id` | unique, nonempty; appears in `[REDACTED:<id>]` and in scan provenance |
| `category` | one of `private_key`, `auth_header`, `credential_url`, `provider_token`, `structured_field`, `configured_credential` |
| `priority` | integer; higher wins an overlap |
| `prefilter` | comma-separated literals, any-of, matched case-insensitively before the matcher runs |
| `matcher` | `regex` (applied to a decoded leaf value) or `sensitive_field` (applied to a structured property name) |
| `pattern` | compiled once with `RegexOptions.NonBacktracking` and `MaxRuleTime` |

Load fails closed — and capture becomes unhealthy — on a missing, unreadable,
empty, or non-YAML file; a missing rule-set version; an empty rule list; a blank
or duplicated id; an unknown category; a missing priority or prefilter; an empty
pattern; an unsupported matcher; or a pattern `NonBacktracking` cannot compile
(including one whose automaton exceeds the engine's node limit). Patterns are
never silently downgraded to the backtracking engine.

**Overlap resolution** is deterministic: highest `priority` wins; ties break by
longest match, then by rule id ordinal, then by earliest start. A match
overlapping an already-accepted span is discarded.

**Atomic reload**: `NeverStoreGate.TryReload` rebuilds the whole rule set and
swaps it in one reference assignment. A failed reload leaves the previously
loaded set in force and returns a safe reason. There is no file watcher or
scheduler in this slice.

## Operator-provisioned literal credentials

Exact values an installation already knows live **outside** the tracked rule
file, in the operator-owned file named by `MEMSRV_NEVER_STORE_LITERALS_PATH`
(`MemSrv:NeverStoreLiteralsPath`): one value per line, `#` comments and blank
lines ignored, minimum eight characters. They match at the highest priority
under the rule id `operator-literal` and category `configured_credential`.

An absent or empty literals file is valid and is **not** a fail-closed
condition — only the rule file must load. A literal shorter than the minimum
fails closed with a reason that names the line number and never the value.

Literal values never enter logs, diagnostics, exception messages, or the
rule-set version. The version input mixes the rule file's bytes and the *count*
of literals only: an unkeyed digest of a low-entropy configured credential
would itself be a disclosure.

## Marker vocabulary

Two distinct markers, so a reader can tell a surgical redaction from a dropped
value:

- `[REDACTED:<rule-id>]` — an exact span was replaced. Spec §5 fixes this form.
- `[OMITTED:<reason>]` — the whole leaf was dropped because no exact span could
  be mapped.

Omission reasons, a closed vocabulary:

| Reason | Meaning |
| --- | --- |
| `leaf_exceeds_limit` | the leaf is larger than `MaxLeafBytes` |
| `sensitive_field_scalar` | a governed sensitive property name carried a number or boolean |
| `sensitive_field_subtree` | a governed sensitive property name carried an object or array |

Scan provenance records rule ids, categories, a redaction count, and — as
`omission:<reason>` entries alongside the rule ids — which omissions occurred.
`scan_status` is `clean`, `redacted`, or `omitted`. Provenance never contains
the matched value, an unsafe excerpt, or a reversible content fingerprint.

## What is deliberately absent from the append path

Blanket entropy scoring, recursive or archive decoding, provider network
verification, transcript-controlled allowlists, and probabilistic/ML
classification. Decoding is exactly one level deep — percent, hex, or Base64 —
and only around high-confidence rules; a decoded hit redacts the original
encoded span.
