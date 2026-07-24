# Disabled synthetic Codex capture slice

Issue #74 adds one deliberately narrow Phase-2 tracer bullet. It proves the
capture ledger and authority boundaries; it is **not a production capture
product**.

## Operator exercise

Create a random credential in a mode-`0600` file, then enroll one stable source
binding. The credential file avoids exposing the credential in process
arguments:

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

It reads only the baked synthetic three-record JSONL fixture and sends one
message/tool-call/tool-result observation to
`POST /capture/v1/observations`. Read the durable operator receipt with:

```sh
memctl capture receipt <observation_uuid>
```

## Explicit limitations

- No live Codex transcript discovery, watch, hook, scheduler, or catch-up.
- No production adapter compatibility or supported capture installation.
- No console/OIDC flow, complete router, queue product, or Claude delivery.
- The existing deterministic never-store gate is applied before append, with a
  one-megabyte observation ceiling. This slice does not claim the complete
  bounded scanner product described by the capture safety research.
- The endpoint is versioned and non-MCP. Capture credentials work only there;
  agent bearer keys work only at `/mcp`. There is no captured-content read
  capability.
