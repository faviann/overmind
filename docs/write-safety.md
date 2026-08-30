# Write-safety contract

Status: **binding**. Phase 1 spec §5 requires every Overmind memory and trace
write to cross this boundary. This contract is independent of conversation
capture; temporary capture callers consume it but do not define it.

The boundary is implemented by `WriteSafetyGate`, `WriteSafetyBudgets`, the
compiled secret rule set, and the deterministic secret scanner. A caller passes
free text or structured JSON and receives a sanitized value or a closed
refusal. Candidate content never appears in failure messages.

## Write policy

- Memory writes reject a detected value with `WriteSafetyRejectedException`,
  naming the highest-ranked accepted rule.
- Trace writes redact exact spans before append and retain the event.
- Missing or unusable rule configuration throws
  `WriteSafetyConfigurationException`; incomplete scanning throws
  `WriteSafetyScanException`. Every governed write fails closed.
- Structured documents are rebuilt from decoded leaves. Serialized JSON is
  never regex-rewritten.

## Numeric budgets

Budget set version: **`write-safety-budgets/2026-08-30.1`**. Defaults are
runtime constants and cannot be loosened by operator configuration.

| Budget | Default | Exceeded |
| --- | ---: | --- |
| `MaxLeafBytes` | 67,108,864 UTF-8 bytes | whole leaf omitted; required values fail closed |
| `MaxScanTime` | 30 seconds per scan | fail closed |
| `MaxRuleTime` | 5 seconds per matcher | fail closed |
| `MaxMatches` | 10,000 per scan | fail closed |
| `MaxDecoderCandidates` | 65,536 per scan | fail closed |
| `MaxDecoderCandidateLength` | 65,536 characters | candidate is not decoded |
| `MaxDecodedBytes` | 16,777,216 per scan | fail closed |

`MaxDecoderCandidateLength` is deliberately a qualification bound rather than
a fail-closed budget. A longer encoded run is crossed by the raw rules but is
not decoded, so a credential hidden only inside it is not detected. This is an
accepted residual risk for accidental leakage, not a determined evader.
Line-wrapped Base64 has the same stated residual risk when a credential spans
two encoded lines. Decoding remains exactly one level deep.

The closed operational failure codes are `matcher_timeout`,
`scan_budget_exhausted`, `required_inspection_incomplete`,
`scanner_internal_failure`, and `scanner_policy_unavailable`. The generalized
boundary exposes only these codes and safe human wording. A consumer-specific
adapter may map them into its own health vocabulary outside this module.

## Rule-set contract

`config/never_store.yaml` contains a version and a nonempty rules list. Each
rule requires:

| Field | Meaning |
| --- | --- |
| `id` | unique nonblank id used in redaction markers and provenance |
| `category` | `private_key`, `auth_header`, `credential_url`, `provider_token`, `structured_field`, or `configured_credential` |
| `priority` | integer; higher wins an overlap |
| `prefilter` | comma-separated case-insensitive covering stems |
| `matcher` | `regex` for text or `sensitive_field` for a property name |
| `pattern` | compiled once with `RegexOptions.NonBacktracking` and `MaxRuleTime` |

Loading fails closed for a missing, unreadable, empty, or non-YAML file; a
missing version or empty rule list; a blank or duplicate id; an unknown
category; a missing priority or prefilter; an empty pattern; an unsupported
matcher; or any pattern the non-backtracking engine cannot compile. Matchers
are never downgraded to the backtracking engine.

A prefilter is an optimization, never a semantic filter. It must cover every
alternative the pattern can match, including separator-less alternatives.

`WriteSafetyGate.TryReload` replaces the complete compiled rule set in one
reference assignment. A failed reload leaves the prior usable set in force.

## Operator-provisioned literals

Exact installation-owned values live outside the tracked rules file at
`MEMSRV_NEVER_STORE_LITERALS_PATH` (`MemSrv:NeverStoreLiteralsPath`), one value
per line with blank lines and `#` comments ignored. Values must be at least
eight characters. An absent or empty literal file is valid; an invalid one
makes the rule configuration unusable.

Literals scan raw text and decoded candidate text under the same budgets. They
use rule id `operator-literal`, category `configured_credential`, and maximum
priority. Literal values never enter logs, diagnostics, exceptions, or the
rule-set version. The version records only the literal count.

## Deterministic matching and decoding

Overlapping matches merge transitively into one union span so no byte covered
by any match survives. Attribution chooses highest priority, then longest
original match, then rule id ordinal. A scan refusal ranks accepted union spans
by the same priority/length/id order. Redaction count is the number of union
spans, not raw matches.

The scanner examines raw text and at most one percent, hex, Base64, or Base64URL
decoding level. Operator literals participate in both passes. An odd-length hex
run is tried under both byte alignments; each decoding charges the decoded-byte
budget. Decoded text is split into maximal printable runs and is never fed back
into candidate decoding.

## Markers and structured values

`SafetyMarkers` is the only marker constructor:

- `[REDACTED:<rule-id>]` replaces an exact span.
- `[OMITTED:<reason>]` replaces a value that cannot be safely span-mapped.

The closed omission reasons are `leaf_exceeds_limit`,
`sensitive_field_scalar`, `sensitive_field_subtree`, and
`redacted_name_collision`.

Property names cross the same rules as string values. The original property
name continues to govern whether its value is sensitive. If redaction causes
two sibling names to collapse to one key, the whole object is omitted rather
than emitting duplicate JSON keys. Existing duplicate keys that were not
changed by safety processing remain source behavior.

Blanket entropy scoring, recursive/archive decoding, provider verification,
source-controlled allowlists, and probabilistic classification are deliberately
absent from the write path.
