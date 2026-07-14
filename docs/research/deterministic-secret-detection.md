# Deterministic secret detection for raw transcript ingestion

Research for [Evaluate deterministic secret detection for raw transcript ingestion](https://github.com/faviann/overmind/issues/65), checked 2026-07-14.

## Question and method

Does the current non-LLM never-store gate cover raw prompts and tool results adequately, and which deterministic additions improve recall without unacceptable false positives or ingestion cost?

This note audits the binding write policy, current configuration and implementation, and the structural transcript measurements recorded by the capture Wayfinder. It compares that baseline with primary sources: GitHub's secret-scanning documentation, the official Gitleaks and `detect-secrets` repositories, and Microsoft's .NET regular-expression guidance. No live credentials were used or copied.

## Answer in brief

**No. The current rule set is not adequate for raw transcript ingestion.** It catches one AWS access-key-ID shape, a narrow unquoted assignment shape, and a Bearer header, but misses the private keys named by the binding spec, common structured assignments, credential-bearing URLs, Basic authentication, most provider-formatted tokens, and encoded forms. Its generic assignment rule also produces easy false positives and can redact only part of a quoted value.

The suitable ingestion design is a bounded, deterministic, high-confidence pipeline:

1. exact-match operator-provisioned secret values;
2. high-confidence structural rules for private-key blocks, authentication headers, credential-bearing connection URLs, and sensitive structured fields;
3. a small, versioned set of provider-specific token formats selected from observed local tooling, with keyword/prefix prefilters;
4. at most one bounded percent/hex/Base64 decoding pass, and only redact an encoded span when its decoded text matches one of the same high-confidence rules.

Do **not** put blanket entropy scanning, transcript-controlled allowlists, recursive/archive decoding, provider network verification, or an LLM in the pre-append path. On a size, time, rule, or decoding-budget failure, fail closed: preserve event metadata but replace the unscanned field or payload with an explicit marker. An append-only store cannot repair a false negative after insertion.

## Current baseline and gaps

The binding policy requires every write path to scan before insert, rejecting memory writes and redacting trace writes in place. It explicitly names private keys, obvious password/token patterns, and `.env`-style secrets; governance belongs in code, not prompts. See [Phase 1 spec §5](../memory-server-phase1-spec.md#5-write-policy-governance-in-code) and [design rules](../design-rules.md#major-solution-boundaries).

The live [rule configuration](../../config/never_store.yaml) contains only:

- `AKIA[0-9A-Z]{16}`;
- an assignment whose key is exactly `password`, `passwd`, `secret`, `token`, or `api_key`/`api-key`, whose separator is `=`, and whose value ends at whitespace;
- `Bearer` followed by 20 or more characters from a limited alphabet.

Consequences for raw transcripts:

- There is no private-key rule despite the binding requirement.
- `AWS_SECRET_ACCESS_KEY=...` does not match: the configured alternatives do not recognize that compound name. JSON/YAML forms such as `"client_secret": "..."`, command flags, Basic auth, database URLs containing passwords, cookies, and most provider tokens also miss.
- A quoted value containing spaces can be only partly replaced, leaving a suffix behind. Conversely, harmless text such as `token=example` matches because the assignment rule imposes no format, length, or entropy constraint.
- An access-key ID is detected, but an unlabelled AWS secret-access-key value is not. This is the general limit of format-only detection for secrets without a stable prefix.
- A percent-, hex-, or Base64-encoded credential is not examined. Textual escaping and values split across capture records remain evasions unless the adapter supplies decoded leaf values and complete record boundaries.

The [current gate](../../src/MemSrv.Core/NeverStoreGate.cs) has additional safety problems that become important with arbitrary tool output:

- a missing configuration path silently loads zero rules, making the governance boundary fail open;
- rule names and duplicates are not validated;
- each dynamic .NET regex has no timeout and uses the backtracking engine;
- object scanning serializes the whole object, then scans or replaces the entire serialized string once per rule. Cost grows with both rule count and payload size, every replacement allocates another string, and a greedy match can consume JSON syntax rather than only a decoded value span;
- overlap precedence is merely configuration order, and no explicit scan-size, match-count, or total-time budget exists.

The [local transcript-volume investigation](https://github.com/faviann/overmind/issues/67) found a largest stable record of 236,273 bytes in its sampled corpus. It also found that Claude's Base64-like values were opaque signature metadata rather than tool payload. That is direct evidence against blanket entropy findings or decoding every Base64-looking run: both would spend the hot-path budget on common non-secret data and damage trace fidelity.

## What established scanners imply

GitHub classifies private keys and credential-bearing MongoDB, MySQL, and PostgreSQL URLs as **high-precision** regex-detected generic secrets, while Basic and Bearer authentication headers are **medium precision**. It separately maintains hundreds of provider patterns and notes that providers change token formats over time. This supports a small structural core plus a maintained provider-format set, not one generic “secret-looking string” rule. [GitHub supported patterns](https://docs.github.com/en/code-security/reference/secret-security/supported-secret-scanning-patterns#about-secret-scanning-patterns)

GitHub also uses pair matching where two values are required, specifically to reduce false positives, and limits push protection to its lowest-false-positive patterns. Its documented large-input timeout and skip cases show that even a mature detector needs explicit workload ceilings. [GitHub detection scope](https://docs.github.com/en/code-security/reference/secret-security/secret-scanning-scope)

Gitleaks combines regexes with optional keywords, entropy thresholds, allowlists, target-size limits, and command timeouts. Decoding and archive traversal are disabled by default and enabled with explicit depth limits; its decoder currently recognizes percent, hex, and Base64 text. These controls are useful design evidence, but repository-scanner defaults are not safe ingestion defaults: a trace cannot be skipped and later persisted raw merely because it was large. [Gitleaks configuration and CLI](https://github.com/gitleaks/gitleaks#configuration), [decoding](https://github.com/gitleaks/gitleaks#decoding), and [archive scanning](https://github.com/gitleaks/gitleaks#archive-scanning)

`detect-secrets` explicitly separates regex, entropy, and keyword strategies and says entropy and keyword detection require tuning for precision. It provides baselines, filters, and inline allowlisting because false positives are expected, and its own caveats say it is heuristic rather than sure-fire and can miss multiline secrets. Those repository-review affordances do not transfer to untrusted transcript content: an attacker or copied document must not be able to authorize persistence with an inline comment. [`detect-secrets` configuration and caveats](https://github.com/Yelp/detect-secrets#configuration)

For .NET specifically, Microsoft says regex over untrusted input should have a timeout. `RegexOptions.NonBacktracking` guarantees processing linear in input length for supported constructs; Microsoft also recommends limiting input size. The current `Compiled` option improves ordinary runtime but provides none of those bounds. [.NET regex best practices](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-regex) and [backtracking controls](https://learn.microsoft.com/en-us/dotnet/standard/base-types/backtracking-in-regular-expressions#control-backtracking)

## Decision matrix

| Candidate | Recall contribution | False-positive / fidelity cost | Ingestion cost and evasion | Decision |
|---|---|---|---|---|
| Exact matching of operator-provisioned runtime credentials | Excellent for secrets the installation already knows, including low-entropy values | Very high precision; short/common configured values may redact frequently, which is still correct for that installation | Bounded multi-pattern matching is cheap; misses unknown and encoded values unless derived forms are added | **Adopt synchronously**; load through operator-owned secret configuration and never emit values in diagnostics |
| Private-key block detection | Closes a binding-spec gap across common PEM/OpenSSH/PGP markers | High precision; example keys are intentionally redacted | Multiline span handling is required; an unterminated begin marker must redact to the field end | **Adopt synchronously** |
| Basic/Bearer headers and credential-bearing DB/HTTP URLs | Strong coverage for common tool logs and command output | Medium-to-high precision; copied examples may redact | Fixed grammar and keyword prefilters keep cost low; percent-encoded userinfo needs bounded decoding | **Adopt synchronously** |
| Structured sensitive-key/value recognition | Finds low-entropy passwords and unprefixed values in JSON, YAML, TOML, dotenv, CLI flags, and headers | Key vocabulary can overmatch fixtures and prose; operator-only exact allowlists may tune known false positives | Scan parsed leaf strings and field names, not serialized JSON; syntax variants must be tested | **Adopt synchronously**, with a narrow versioned vocabulary and span-only redaction |
| Curated provider-specific formats | High recall for distinctive GitHub, GitLab, OpenAI, npm, PyPI, Slack, Stripe, cloud, and locally observed tokens | Usually high precision for modern prefixed formats; legacy/unprefixed patterns are noisier | Hundreds of rules multiplied by every payload are too costly; token formats drift | **Adopt a small observed set**, keyword/prefix-prefiltered, version-pinned, and reviewed on upgrade |
| Pair/context rules | Improves precision for multipart credentials such as access-key ID plus secret | Can miss halves split across records | Bounded event-local correlation is cheap; never depend on later records to sanitize an earlier insert | **Use as a precision supplement**, never as the only rule for a secret value |
| One-level percent/hex/Base64 decoding | Finds straightforward encoded evasions | Common signatures, hashes, and binary blobs create many candidates; blanket decoding harms fidelity | Limit candidate length/count and total decoded bytes; scan printable decoded text only; redact the original encoded span | **Adopt only as a bounded wrapper around high-confidence rules** |
| Generic entropy over all strings | Finds some unknown unprefixed secrets | High false-positive rate on hashes, signatures, IDs, generated code, compressed data, and model/tool artifacts | Adds a full candidate-extraction and scoring pass and is trivially evaded by low-entropy passwords or splitting | **Do not use as an ingest-blocking rule**; evaluate only on a synthetic/sanitized tuning corpus |
| Recursive decoding or archive extraction | Finds deeply hidden material | Very high fidelity and false-positive cost | Expansion ratios, nesting, and decompression bombs make the critical path unbounded | **Do not run during ingestion** |
| Provider validity checks | Can distinguish live tokens from examples and reduce false positives | Sends or exercises candidate credentials; outage and rate-limit behavior affect capture | Network latency, privacy, side effects, and failure ambiguity violate the local nonblocking boundary | **Do not run during ingestion** |
| LLM/ML classification | May classify unstructured passwords | Nondeterministic or model-dependent misses and difficult auditing | Adds latency, availability, privacy, and version drift | **Do not run during ingestion** |

## Required implementation constraints

These are constraints for the Phase 2 specification, not production implementation in this research ticket.

1. **One pre-append policy point, all paths.** Historical import, live hooks, catch-up, retries, server-generated trace events, and memory writes must call the same detector before database insertion. Retry/idempotency identity must describe the source event, not the post-redaction bytes.
2. **Fail closed and remain auditable.** Missing/invalid/empty rule configuration, regex timeout, decode-budget exhaustion, unsupported structure, or oversized input must never persist an unscanned tail. Record a marker, source identity, rule-set version, reason, and safe byte/count metadata. Do not store a raw unkeyed digest of low-entropy rejected content.
3. **Scan decoded leaf values.** Preserve the original structured envelope while matching individual strings and sensitive field names. Redact only matched spans, or the whole leaf when exact span mapping is impossible. Do not run regex replacement over serialized JSON.
4. **Bound every dimension.** Specify maximum record/leaf bytes, total scan time, per-rule timeout, match count, decoder candidate count/length, total decoded bytes, and decode depth (one). Oversized ordinary text needs an explicit chunk/whole-field fidelity policy; archives are never expanded in the hot path.
5. **Use predictable matchers.** Compile once. Prefer literal/prefix prefilters and `RegexOptions.NonBacktracking`; reject unsupported patterns at configuration load. Retain a timeout and total deadline as defense in depth. Define deterministic longest/highest-priority overlap resolution before replacement.
6. **Treat rules as governed code.** Require unique nonempty rule IDs, explicit categories and priority, a version/fingerprint, positive/negative tests, and atomic reload. Configuration content and errors go to stderr/file diagnostics without candidate values, never stdout.
7. **No content-authorized bypass.** Allowlists are operator-owned, exact or narrowly anchored, reviewable, and applied before deployment. Ignore `gitleaks:allow`, `pragma: allowlist`, or similar text inside captured content.
8. **Preserve provenance without preserving secrets.** The captured event should expose that redaction occurred, rule category/ID, and count, but never the matched value or surrounding unsafe snippet. Encoded hits redact the original encoded span.

## Acceptance evidence the specification should require

- A synthetic corpus of distinctive provider formats, PEM variants, credential URLs, auth headers, sensitive key/value syntaxes, low-entropy configured passwords, and revoked/fake examples. No live credential enters a fixture.
- Transform cases for whitespace, quoting, casing, CRLF, JSON escaping, percent/hex/Base64 encoding, multiple and overlapping hits, incomplete private-key blocks, and record/chunk boundaries.
- Negative cases drawn from observed transcript shapes: hashes, UUIDs, source-map/signature blobs, package locks, model metadata, generated code, and ordinary uses of words such as “token” and “secret”.
- Pathological cases at and beyond the observed 236,273-byte record: long near-matches, many matches, decode-candidate floods, malformed encodings, unsupported structures, and regex timeout. Assert bounded time/memory and fail-closed persistence.
- End-to-end absence assertions across the database, application diagnostics, exception messages, queue/dead-letter state, and API responses. “Redacted in the trace table” is insufficient if the raw queue or stderr retained the value.
- Versioned recall, false-positive, bytes/second, allocations/byte, and p95/p99 latency results. Rule-set expansion is accepted only against these gates.

## Downstream map consumers

- [Choose fidelity policy for oversized, binary, malformed, and unsupported records](https://github.com/faviann/overmind/issues/63) is the direct consumer: it must choose whole-field versus bounded-chunk handling and the auditable fail-closed representation.
- [Define the canonical captured-event envelope](https://github.com/faviann/overmind/issues/68) must carry safe redaction/scan outcome and rule-set provenance without candidate content.
- [Choose the trusted operator import surface and authorization model](https://github.com/faviann/overmind/issues/61) owns the server-side pre-append enforcement boundary for batches and backfill.
- [Prototype idempotent incremental capture and scheduled catch-up](https://github.com/faviann/overmind/issues/62) must show that retries remain idempotent when detection redacts or replaces a payload.
- [Decide capture packaging, installation, health, and recovery behavior](https://github.com/faviann/overmind/issues/64) owns rule distribution, atomic validation/reload, version reporting, and fail-closed health signals.
- [Specify Codex-first and Claude Code-second delivery slices](https://github.com/faviann/overmind/issues/66) should sequence the detector, synthetic corpus, and performance/absence gates before historical backfill.

## Conclusion

The existing three-regex gate is a useful Phase 1 proof of the reject/redact policy, not a safe raw-transcript boundary. The next specification should strengthen that boundary with exact known-secret matching, a compact high-confidence structural and provider rule set, and tightly bounded one-level decoding. Its more important property is operational: every byte is either scanned under a validated, versioned, time-bounded rule set or replaced before append. Broader heuristic discovery can inform future rule tuning, but it cannot be allowed to make the append-only ingestion path slow, evasive, or probabilistic.
