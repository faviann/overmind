# Capture safety budgets and rule-set contract

Published defaults for the deterministic never-store detector (issue #76). The
numbers below are versioned runtime constants in
[`SafetyBudgets`](../src/MemSrv.Core/SafetyBudgets.cs), not configuration: an
operator cannot loosen them from a file. Tests inject smaller budgets where the
mechanism, not the number, is what is under test.

Budget set version: **`capture-safety-budgets/2026-07-26.2`**.
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
| `MaxDecoderCandidateLength` | 65,536 (64 KiB) | characters | run is not decoded; see the residual risk below |
| `MaxDecodedBytes` | 16,777,216 (16 MiB) | decoded bytes per scan call | fail closed |

"Fail closed" means the scan throws `SafetyScanException`, ingestion persists
nothing, and the stream checkpoint does not advance. The next legitimate record
is still accepted at the same source position.

### `MaxDecoderCandidateLength` is an accepted residual risk

`MaxDecoderCandidateLength` is deliberately **not** a fail-closed budget, and it
is the one place this detector knowingly does not look. State it plainly: an
encoded run longer than 65,536 characters **is not decoded, so a credential
inside it is not detected**. The undecoded bytes still cross every rule, but a
Base64 blob does not resemble its plaintext, so that is not a detection
argument — it does not make the run inspected.

The cap is set at 64 KiB precisely so the realistic shapes are covered: a
Base64'd credentials file, a kubeconfig, or a JWT all decode well inside it. The
skip beyond that exists for genuinely huge opaque blobs, where failing an entire
capture closed would trade real availability for very little. The threat model
here is **accidental leakage — a developer pasting an encoded config — not a
determined evader**, who can defeat any one-level bounded decoder trivially by
encoding twice. Raising the cap further is a cost decision, not a security
boundary: at 65,536 characters, `MaxDecodedBytes` becomes reachable after ~341
maximum-length runs, which no transcript under the 1 MB transport cap can hold.

## Measured evidence

Measured on the development host against the shipped rule set, Release build,
`RegexOptions.NonBacktracking`, warm process, one leaf at a time. Values are the
median of three runs; the benchmark is re-run whenever a default or a pattern
moves, and these numbers were re-measured for budget set
`capture-safety-budgets/2026-07-26.2` (the raised candidate-length cap and the
widened base64url candidate alphabet both cost decode time).

| Workload | Bytes | Decode candidates | Scan time | Throughput |
| --- | ---: | ---: | ---: | ---: |
| Prose, no encoded runs | 235,690 | 0 | 23.6 ms | 9.5 MiB/s |
| Prose, no encoded runs | 2,088,890 | 0 | 267.7 ms | 7.4 MiB/s |
| GUID-dense log output | 229,890 | 8,400 | 222.1 ms | 1.0 MiB/s |
| GUID-dense log output | 996,890 | 36,000 | 434.9 ms | 2.2 MiB/s |
| GUID-dense log output | 4,020,890 | 144,000 | 1,840.0 ms | 2.1 MiB/s |
| Base64'd credentials file, one 57,924-char candidate (credential found) | 57,933 | 1 | 2.5 ms | 22.1 MiB/s |
| 1 MB packed with 65,336-char decodable runs | 1,045,391 | 16 | 52.9 ms | 18.8 MiB/s |
| 4 MB packed with 65,336-char decodable runs | 4,181,567 | 64 | 210.9 ms | 18.9 MiB/s |
| 64 MiB opaque run (one over-length candidate) | 67,108,864 | 1 | 2,131.3 ms | 30.0 MiB/s |
| 64 MiB leaf, one credential at its final bytes | 67,108,864 | 2 | 2,296.7 ms | 27.9 MiB/s |
| 64 MiB leaf, measured alone in a cold process | 67,108,864 | 2 | 3,151–3,753 ms | ~19 MiB/s |

Per-matcher cost over the same 64 MiB leaf: the sixteen governed rules run
26.5–389.7 ms each (`aws-access-key-id` 331.3 ms, `sensitive-field-value`
389.7 ms); Base64 candidate extraction 775.2 ms, hex 202.7 ms, percent 20.8 ms;
the literal prefilter sweep 1,328.7 ms. Peak working set for the isolated 64 MiB
case was **577 MiB**. That is larger than the leaf because a .NET `string` is
UTF-16: 64 MiB of UTF-8 text is 128 MiB in memory, the benchmark holds a warm
scan's redacted copy alongside the timed one, and the GC has not yet reclaimed
either.

How each default follows from those numbers:

- **`MaxScanTime` = 30 s.** A leaf at the 64 MiB limit costs 2.3 s warm and up
  to 3.8 s cold in Release. Thirty seconds leaves roughly an 8× margin for a
  Debug build, a loaded host, and four concurrent test shards, while still
  bounding the worst case.
- **`MaxRuleTime` = 5 s.** The slowest single matcher over a limit-sized leaf is
  Base64 candidate extraction at 775 ms — up from 692 ms before the alphabet was
  widened to base64url. Five seconds is still a ~6× margin. A shorter timeout —
  250 ms was the first candidate — fails a legitimate limit-sized leaf closed.
- **`MaxDecoderCandidates` = 65,536.** The observed transcript-volume ceiling is
  a 236,273-byte record (issue #67); GUID-dense output of that size produced
  8,400 candidates. 65,536 admits roughly eight such records' worth of density
  while capping decode work near one second at the measured 2.1–2.2 MiB/s.
- **`MaxDecodedBytes` = 16 MiB.** This is the real backstop on decode work and
  the reason raising the candidate-length cap does not need another budget to
  move: 65,536 candidates at the 65,536-character cap could in principle decode
  three orders of magnitude more, and 16 MiB is ~70× the observed maximum
  record. A 4 MB leaf packed with maximum-length runs still decodes in 211 ms.
- **`MaxMatches` = 10,000.** No legitimate 236 KB record carries ten thousand
  distinct credentials; a flood at that scale is pathological input.
- **`MaxDecoderCandidateLength` = 65,536.** Large enough that a Base64'd
  credentials file, kubeconfig, or JWT is always decoded; bounded so a single
  huge opaque blob does not drag decode cost with it. The measured cost of the
  raise is small — the credentials-file workload above decodes in 2.5 ms, and
  packing a whole 4 MB leaf with maximum-length runs costs 211 ms.

No published default had to move as a result of this re-measurement.

Re-run the measurement whenever a rule is added or a pattern changes, and bump
the budget-set version if a default moves.

### Cost of exercising the real numbers in `make test`

`SafetyBoundaryTests` is the only place the production 128 MiB and 64 MiB
numbers are materialized. All three of its tests live in one class so xUnit runs
them sequentially, and each releases its value before the next allocates.
Measured on the same host, Debug build: **4–5 s** wall for the class, **743 MiB**
peak test-host RSS against a **242 MiB** control (a same-shard run of
`SafetyGateTests`) — roughly 500 MiB attributable to the boundary values, which
is what UTF-16 storage of a 64 MiB and a 128 MiB value costs.

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

**Overlap resolution** is deterministic and **merging**. Matches that overlap —
transitively, so a chain of overlaps is one region — collapse into a single
union span covering every byte any of them claimed. The span is attributed to
the highest-`priority` rule among them; ties break by longest original match,
then by rule id ordinal. Merging rather than discarding is a safety property,
not a cosmetic one: a short high-priority match (an operator literal, priority
`int.MaxValue`) sitting inside a long lower-priority one (a private-key block,
priority 100) must not win the region and leave the rest of the key block in
cleartext. No byte covered by any match survives unredacted.

Because a merged span reports one rule, a scan's `redactionCount` counts union
spans, not raw matches, and a refusal names the highest-priority accepted
match — not whichever rule id happens to sort first.

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
| `redacted_name_collision` | two sibling property names became the same text after redaction |

Both markers are constructed in exactly one place, `SafetyMarkers`; no caller
rebuilds either shape by hand.

## Property names are scanned like values

A JSON **property name** crosses the same rule set as a value. An environment
dump keyed by its own value, a map keyed by a credential, or a
credential-bearing URL used as a key would otherwise be written verbatim into
`safe_source_payload` and persist forever. A redacted name is written as the
key, so the document stays parseable and its structure survives.

Redaction is not injective, so two distinct sibling names can collapse to one
key. Emitting both would produce a duplicate JSON key and silently lose a value
on re-parse, so the **whole object** is dropped instead, as
`[OMITTED:redacted_name_collision]`. The *original* name still governs
sensitive-field recognition for the value beneath it: redacting a name must not
change what its value means.

Scan provenance records rule ids, categories, a redaction count, and — as
`omission:<reason>` entries alongside the rule ids — which omissions occurred.
`scan_status` is `clean`, `redacted`, or `omitted`. Provenance never contains
the matched value, an unsafe excerpt, or a reversible content fingerprint.

## What is deliberately absent from the append path

Blanket entropy scoring, recursive or archive decoding, provider network
verification, transcript-controlled allowlists, and probabilistic/ML
classification. Decoding is exactly one level deep — percent, hex, or Base64 in
either the standard (`+/`) or base64url (`-_`) alphabet — and only around
high-confidence rules; a decoded hit redacts the original encoded span. Candidate
extraction carries the same `MaxRuleTime` matcher timeout the governed rules do,
and a timeout there fails the scan closed like any other.
