# Disabled synthetic Codex capture slice

Issue #74 adds one deliberately narrow Phase-2 tracer bullet. It proves the
capture ledger and authority boundaries; it is **not a production capture
product**.

## Operator exercise

Create a random capture credential in a mode-`0600` file, then enroll one stable
source binding. Capture credentials use the structurally reserved form
`mcap_` followed by at least 32 URL-safe random characters. Ordinary agent
bearer keys are rejected by capture enrollment/import, and `mcap_` credentials
are rejected by agent-key provisioning. The credential file avoids exposing
the credential in process arguments:

```sh
memctl capture enroll my-codex-fixture \
  --harness codex \
  --agent-id capture:my-codex-fixture \
  --credential-file /run/secrets/codex-capture-key
```

Without `--namespace`, the server routes the binding to
`capture/unscoped` and reports `routeBasis: fallback`. An operator may provide
an existing namespace with `--namespace`; the binding, not the request,
determines the effective namespace and derived capture agent/session identity.

The disabled OCI tracer is built separately from the server image:

```sh
docker build -f Dockerfile.capture-tracer -t overmind-codex-capture-fixture .
docker run --rm \
  -e OVERMIND_CODEX_CAPTURE_ENABLE=synthetic-non-production \
  -e OVERMIND_CAPTURE_URL=http://overmind:8080 \
  -e OVERMIND_CAPTURE_CREDENTIAL \
  overmind-codex-capture-fixture
```

It defaults to the baked synthetic three-record JSONL fixture.
`OVERMIND_CODEX_FIXTURE` may explicitly select another synthetic fixture for
non-production tests. The exact-value enable gate and strict three-record
rollout-schema validation still apply.

The fixture uses representative persisted Codex rollout records shaped as
`{timestamp,type,payload}`: a nested `message`/`input_text`, a
`function_call` with JSON-string arguments, and a `function_call_output`.
Rollout records do not invent a per-record session ID; this disabled adapter
uses one stable synthetic source session, while capture bindings isolate
installations. It sends the three ordered observations to
`POST /capture/v1/observations`.

Each JSONL record has its own numeric source position, verified `byte_range`
locator measured from the actual fixture bytes, idempotency identity, and
receipt. The locator and exact-byte digest cover the JSON content plus its LF
or CRLF separator when present, while JSON parsing excludes the separator.
Imports may instead use a `native_id` locator when the source exposes one. The
source timestamp is retained as its exact raw string plus parsed time; it never
falls back to event occurrence or server capture time. The tool result retains
its source-native `result_for` relationship to the call.
Read any durable operator receipt with:

```sh
memctl capture receipt <observation_uuid>
```

The receipt command writes JSON Lines: one canonical captured-event envelope
per event. Every line repeats the immutable observation, carries exactly one
event, and places that event's source relationships at top level. It does not
emit the HTTP delivery status/route wrapper; the import endpoint retains those
delivery facts alongside its complete immutable observation and event facts.
Receipt reads use already-sanitized durable rows and remain available when the
scanner configuration is unavailable; scanner health gates enrollment/import
writes and checkpoint movement, not reads.

## Explicit limitations

- No live Codex transcript discovery, watch, hook, scheduler, or catch-up.
- No production adapter compatibility or supported capture installation.
- No console/OIDC flow, complete router, queue product, or Claude delivery.
- The existing deterministic never-store gate is applied before append, with a
  one-megabyte observation ceiling. For known credentials, the HTTP boundary
  rejects an oversized request before full JSON deserialization; unknown
  credentials still receive 401 before any body or safety work. The service
  repeats the size check as defense in depth. This slice does not claim the
  complete bounded scanner product described by the capture safety research.
- Source positions begin at zero and must advance exactly one past the
  server-owned contiguous checkpoint. Stream namespace and route basis are
  fixed by the binding policy on first import and reused by later catch-up.
- A `byte_range` import includes the lowercase SHA-256 of those exact trusted
  source bytes. The server validates it and includes it only inside the
  binding-keyed retry signature; the raw digest is neither persisted nor
  returned. Equal-length source rewrites therefore conflict even when they
  parse to the same JSON. LF/CRLF and final-newline changes also change source
  identity and stop the stream. A `native_id` locator has no byte-content
  digest.
- Retry comparison uses a server-owned random per-binding HMAC key. Receipts
  expose canonical scan status, rule-set version, applied rule IDs/categories,
  and aggregate redaction count; raw unsafe input and an unkeyed fingerprint of
  it are not persisted.
- Binding stable names and derived capture agent identities are rejected before
  enrollment if the never-store gate detects a secret. Relationship target
  stream scope remains nullable source evidence: an explicit
  `target.sourceStreamUuid` round-trips unchanged, while an omitted scope stays
  null rather than inheriting the source event's stream.
- The endpoint is versioned and non-MCP. Capture credentials work only there;
  agent bearer keys work only at `/mcp`. There is no captured-content read
  capability.
- The runtime role may insert capture bindings, streams, and ledger rows, but
  has no binding UPDATE authority. Its only stream UPDATE columns are
  `checkpoint_position` and `updated_at`; binding identity/credentials/routes
  and stream identity/routes remain immutable to the server process.
