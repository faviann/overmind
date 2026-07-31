# Capture safety budgets and rule-set contract

Published defaults for the deterministic never-store detector (issue #76). The
numbers below are versioned runtime constants in
[`SafetyBudgets`](../src/MemSrv.Core/SafetyBudgets.cs), not configuration: an
operator cannot loosen them from a file. Tests inject smaller budgets where the
mechanism, not the number, is what is under test.

Budget set version: **`capture-safety-budgets/2026-07-26.2`**.
Shipped rule set version: **`never-store/2026-07-26.2`** (the runtime version
string appends a SHA-256 prefix of the rule file and, when present, the *count*
of operator literals — never their values).

## Numeric defaults

| Budget | Default | Unit | Exceeded → |
| --- | ---: | --- | --- |
| `MaxObservationBytes` | 134,217,728 (128 MiB) | UTF-8 bytes per source observation | whole observation omitted by `CaptureIngestion`; assertion fails closed for direct gate callers |
| `MaxLeafBytes` | 67,108,864 (64 MiB) | UTF-8 bytes per decoded structured leaf | leaf wholly omitted (fail closed if the value is a required identity) |
| `MaxScanTime` | 30 | seconds per scan call | fail closed |
| `MaxRuleTime` | 5 | seconds per rule matcher | fail closed |
| `MaxMatches` | 10,000 | matches per scan call | fail closed |
| `MaxDecoderCandidates` | 65,536 | encoded runs decoded per scan call | fail closed |
| `MaxDecoderCandidateLength` | 65,536 (64 KiB) | characters | run is not decoded; see the residual risk below |
| `MaxDecodedBytes` | 16,777,216 (16 MiB) | decoded bytes per scan call | fail closed |

An operational "fail closed" outcome means the scan throws
`SafetyScanException`, ingestion persists nothing, and the stream checkpoint
does not advance. The next legitimate record is still accepted at the same
source position.

`MaxObservationBytes` has a separate deterministic fidelity outcome at the
`CaptureIngestion` interface. A positive injected bound may tighten but never
loosen the fixed 128 MiB ceiling. An original observation above the effective bound is replaced
with a compact whole-observation omission, that representation is scanned and
persisted, and the checkpoint advances. `NeverStoreGate` still enforces the
limit through `AssertObservationWithinBudget`: it protects the compact
representation inside ingestion and remains fail closed for direct callers.
The compact form retains only the authenticated harness and the source
identity/position/locator needed for canonical identity and keyed retry
semantics, plus fixed omission provenance. Optional timestamp, descriptive
source/adapter metadata, route evidence, source payload, and original semantic events
cannot keep the omission above the limit. Required identity and locator values
are neither truncated nor replaced by unkeyed content fingerprints.
Operational scanner failures while processing either representation still
persist nothing and do not advance the checkpoint.

Before either fidelity cap is applied, JSON byte measurement streams into a
discarding counter rather than creating a whole-original serialized string.
The counter checks the fixed published `MaxScanTime` deadline as serialization
progresses; deadline exhaustion is an operational fail-closed outcome, records
no invented original byte count, and occurs before claim or append. An original
believed within its effective cap is materialized and its actual UTF-8 size is
checked again. If mutable source state made that representation over-limit,
policy selects the omission using the materialized count or fails closed when
safe identity cannot support omission. An ordinary over-limit observation
materializes only its compact omission, while ingestion streams the original
retry-signature representation directly into the keyed hash. Counting and
hashing share one governed write-only serialization/deadline implementation.

The explicit `binary_content` classifier uses one absolute fixed deadline and
the applicable effective fidelity ceiling for the entire public operation.
Candidate prepass, byte-capped rewrite, `JsonDocument` parsing, and root
cloning/materialization all share that same `MaxScanTime` deadline; processing
another event or entering another phase never resets the clock, and policy
asserts the deadline again after materialization. It walks already-parsed JSON
without constructing a raw string or mutable JSON tree. A record with no valid
binary candidate is returned unchanged; a record with a candidate is streamed
into a byte-capped rewritten representation while its validated byte array is skipped.
If that safe rewrite grows beyond the active ceiling, policy preserves the
recognized-binary outcome and emits the bounded whole-observation
`unsupported_binary_content` omission (or fails closed when the mandatory
identity-bearing omission cannot fit); it never returns the smaller raw
record.
The compact record repeats the trusted source identity and position (plus
locator kind) from the adapter or authenticated ingestion command; it never
depends on optional block-local identity. Root-only Codex reasoning envelope
recognition prevents a nested object from exempting its bytes.
An adapter event receives the corresponding opaque-metadata exemption only
when its reasoning `source` is structurally identical to the recognized root
source payload's reasoning `payload`; the redundant event projection therefore
cannot add byte-bearing evidence beyond what raw-source traversal already
admitted.
If that rewritten representation crosses the effective ceiling, the ordinary
transport/content serializer owns the deterministic whole-observation omission.
Tests exercise a multi-megabyte valid byte array at the documented
`CaptureFidelityPolicy` seam and assert bounded additional allocation and
elapsed time. A mechanical test uses the production absolute-deadline structure
with a controlled `TimeProvider` to prove separate phases cannot restart the
budget; the public policy deadline remains fixed and caller-noninjectionable.

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

### Line-wrapped Base64 is a second accepted residual risk

Candidate extraction matches a *contiguous* run of Base64 characters. PEM-style
output wraps at 64 characters, so a wrapped blob yields **one candidate per
line**, and a credential that straddles a line break decodes to nothing on
either side of it. A 40-character secret inside a wrapped blob is therefore
detected only when it happens to fall wholly within one line.

This is stated, not fixed. Joining wrapped lines before decoding means guessing
which newlines are formatting and which are content — a normalisation heuristic
the research note rules out of the append path, and one that would change what
"the original encoded span" means for redaction. The threat model is the same
as for the candidate-length cap: **accidental leakage, not a determined
evader**. Two things bound the exposure in practice: a wrapped PEM *private
key* is caught whole by `private-key-block`, which does not decode at all, and
the highest-value shapes the decoder exists for — a Base64'd credentials file, a
kubeconfig, a JWT — are emitted unwrapped by the tools that produce them.

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

Per-matcher cost over the same 64 MiB leaf, re-measured after the prefilter
lists were consolidated into covering stems: the sixteen governed rules run
27.4–199.2 ms each (`github-token` 140.9 ms across five prefilters,
`sensitive-field-value` 199.2 ms across seven); Base64 candidate extraction
687.2 ms, hex 380.3 ms, percent 20.1 ms. Each prefilter costs a full
case-insensitive scan of the leaf — about 27 ms at this size — which is why
`sensitive-field-value` fell from 402.3 ms (fourteen prefilters) and
`sensitive-assignment` from 310.6 ms (eleven) to 199.2 ms and 172.4 ms while
matching strictly more spellings.

The operator-literal sweep runs in **two** places, and only the first of them is
what that 26 ms-per-literal figure describes. Over the whole leaf it is one
ordinal `IndexOf` per configured literal — about 26 ms per literal at 64 MiB,
proportional to how many exact values the operator provisioned. It then runs
*again* inside the decoded pass, where every configured literal is tried against
every printable run of every decoded candidate. That second sweep costs
literals × candidates, not literals; with `MaxDecoderCandidates` at 65,536 it is
the phase with the widest cost range in the scanner, and nothing but
`MaxScanTime` bounds it — which is why the scan deadline is checked inside both
loops rather than only around them. The literal sweep is the one phase whose
cost an operator controls, and it is now controlled in two dimensions.

Whole-workload timings on this host vary by up to ±40% between runs (the 64 MiB
warm case was observed at 1,847–2,586 ms across three consecutive Release runs),
so treat the table above as an order-of-magnitude budget justification, not a
regression baseline. Peak working set for the isolated 64 MiB
case was **577 MiB**. That is larger than the leaf because a .NET `string` is
UTF-16: 64 MiB of UTF-8 text is 128 MiB in memory, the benchmark holds a warm
scan's redacted copy alongside the timed one, and the GC has not yet reclaimed
either.

How each default follows from those numbers:

- **`MaxScanTime` = 30 s.** A leaf at the 64 MiB limit costs 2.3 s warm and up
  to 3.8 s cold in Release. Thirty seconds leaves roughly an 8× margin for a
  Debug build, a loaded host, and four concurrent test shards, while still
  bounding the worst case. The same fixed deadline governs streaming fidelity
  byte counting and keyed-signature serialization; unlike scanner budgets, it
  is not caller-injectable through a smaller fidelity cap.
- **`MaxRuleTime` = 5 s.** The slowest single matcher over a limit-sized leaf is
  Base64 candidate extraction, measured at 687–775 ms since the alphabet was
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

The later round that made `sensitive-assignment` decode-eligible, split decoded
text into printable runs, and tried both alignments of an odd-length hex run was
measured **paired** — the previous scanner and the current one, same host, same
generated workloads, four alternating runs each, median of three timed scans per
run. Medians: GUID-dense ~230 KB 201 ms → 148 ms; GUID-dense ~1 MB 274 ms →
272 ms; Base64'd credentials file 0.9 ms → 0.8 ms; 1 MB packed with
maximum-length runs 40 ms → 50 ms; 4 MB packed 226 ms → 228 ms. Every one of
those differences is inside the ±40% run-to-run spread this host already shows
(single observations in the same series ranged 150–324 ms and 205–444 ms for the
two extremes), so the table above is left as measured and no default's
justification changes. The paired harness generates its own workloads and is not
the harness that produced the table, so read it as a *delta* check, not as
replacement absolute numbers. The one structural cost this round does add is six
extra prefilter scans per decoded candidate, because `sensitive-assignment`'s
covering stems now run over decoded text too; `MaxDecodedBytes` is what bounds
it.

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
Transport omission can advance only a `byte_range` observation, because its
source digest participates in the binding-keyed signature without being
persisted. An over-limit `native_id` observation fails closed before claim or
delivery: it has no binding-stable content identity for detecting a changed
same-length original after compaction, and policy introduces neither an unkeyed
fingerprint nor a credential-derived key.

## Rule-set schema

`config/never_store.yaml` carries a `version` and a nonempty `rules` list. Each
rule requires:

| Field | Meaning |
| --- | --- |
| `id` | unique, nonempty; appears in `[REDACTED:<id>]` and in scan provenance |
| `category` | one of `private_key`, `auth_header`, `credential_url`, `provider_token`, `structured_field`, `configured_credential` |
| `priority` | integer; higher wins an overlap |
| `prefilter` | comma-separated literals, any-of, matched case-insensitively before the matcher runs; must **cover** every alternative the pattern can match |
| `matcher` | `regex` (applied to a decoded leaf value) or `sensitive_field` (applied to a structured property name) |
| `pattern` | compiled once with `RegexOptions.NonBacktracking` and `MaxRuleTime` |

Load fails closed — and capture becomes unhealthy — on a missing, unreadable,
empty, or non-YAML file; a missing rule-set version; an empty rule list; a blank
or duplicated id; an unknown category; a missing priority or prefilter; an empty
pattern; an unsupported matcher; or a pattern `NonBacktracking` cannot compile
(including one whose automaton exceeds the engine's node limit). Patterns are
never silently downgraded to the backtracking engine.

A prefilter is an **optimisation, never a filter**. It decides whether the
matcher runs at all, so an alternative no prefilter literal can reach is a
silent false negative rather than a slow path — including the separator-less
spellings a `[_-]?` group allows (`sessionkey` as well as `session_key`).
Prefilters are therefore short covering stems (`key`, `pass`, `connection`)
rather than one literal per spelling, which is both safer and cheaper: each
prefilter costs a full scan of the leaf.
`SafetyGateTests.EveryPatternAlternativeIsReachableThroughItsPrefilter`
enumerates every alternation path through every shipped pattern and fails if
one of them is unreachable.

**Overlap resolution** is deterministic and **merging**. Matches that overlap —
transitively, so a chain of overlaps is one region — collapse into a single
union span covering every byte any of them claimed. The span is attributed to
the highest-`priority` rule among them; ties break by the longest *original*
match — the raw match lengths, before merging — then by rule id ordinal.
Merging rather than discarding is a safety property,
not a cosmetic one: a short high-priority match (an operator literal, priority
`int.MaxValue`) sitting inside a long lower-priority one (a private-key block,
priority 100) must not win the region and leave the rest of the key block in
cleartext. No byte covered by any match survives unredacted.

Because a merged span reports one rule, a scan's `redactionCount` counts union
spans, not raw matches, and a refusal names the highest-priority accepted
match — not whichever rule id happens to sort first. That second choice ranks
the *accepted* spans, so its length tiebreak is the **merged** span length,
while the tiebreak inside a merge is the original match length. Both use the
same `MatchRanking` ordering — priority, then length, then rule id ordinal — so
the two cannot drift apart.

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

Literals are swept over the raw leaf **and** over decoded candidate text, in
the same bounded pass and against the same budgets as the high-confidence
rules. An exact operator-known value is the highest-confidence rule there is, so
a Base64, hex, or percent-encoded copy of one must not be the shape that gets
through; as with any decoded hit, the span redacted is the original encoded run.

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
`[OMITTED:redacted_name_collision]`. This applies only when redaction *caused*
the collision: source keys that were already identical and were left unchanged
are passed through as they arrived. The *original* name still governs
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
either the standard (`+/`) or base64url (`-_`) alphabet — and covers every rule
that reads TEXT plus the operator-provisioned exact values. The one matcher that
does not run there is `sensitive_field`, which reads a structured property NAME:
a decoded blob has no structure to take a name from. Eligibility is decided by
**matcher kind, not category** — `sensitive-assignment` is an ordinary free-text
`NAME=value` regex that merely carries the `structured_field` category, and
gating it on category is what previously left a Base64'd credentials file, the
headline shape this decoder exists for, unscanned. A decoded hit redacts the
original encoded span. Candidate extraction carries the same `MaxRuleTime`
matcher timeout the governed rules do, and a timeout there fails the scan closed
like any other.

An odd-length hex run has two possible byte alignments and nothing in the run
says which is real, so both are decoded; that is two decodings of one candidate,
not two decoding levels, and each charges `MaxDecodedBytes`. Decoded text is
split on control characters into its maximal printable runs and each run is
scanned separately, because a decoded blob routinely carries binary framing
around ordinary plaintext and discarding the whole candidate for one control
byte made every such blob invisible. The split is bounded and deterministic —
no scoring, no recursion, no re-decoding of a run. A credential that straddles a
control byte is not detected, the same accepted residual risk line-wrapped
Base64 sits behind.
