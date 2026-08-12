# Codex catch-up runtime

The supported Linux-first baseline is one headless, Codex-only OCI runtime per
user. It scans the explicitly mounted Codex current-session rollout tree immediately at
startup and on a non-overlapping jittered schedule, claims completed records in
durable local state, and converges them through the central capture HTTP API.
If Codex moves an already-queued rollout into its separately mounted archive,
the runtime finds only that durable responsibility there and revalidates the
same transcript and locator evidence before retrying it.
There is no Claude production adapter, hook listener, inbound port, Docker
socket, database credential, privileged mode, or self-update path.

## Enrollment

Until browser enrollment exists, the operator creates a random capture-only
credential in a mode-`0600` file and enrolls it with `memctl`:

```sh
memctl capture enroll my-codex-runtime \
  --harness codex \
  --agent-id capture:my-codex-runtime \
  --credential-file /run/secrets/codex-capture-key
```

Only the resulting `mcap_…` credential and the server URL cross into the
runtime. The credential authorizes capture writes only: it is not an MCP agent
key and provides no captured-content reads or database access.

## Run the immutable image

Copy `.env.capture.example` to an ignored operator-owned environment file,
replace every placeholder, restrict it to the operator before starting Compose,
and use an immutable version or registry digest:

```sh
chmod 0600 .env.capture
docker compose --env-file .env.capture -f compose.capture.yaml up -d
```

The reference runtime has a read-only container filesystem and exactly four
declared mounts: the read-only `~/.codex/sessions` tree, the read-only
`~/.codex/archived_sessions` tree, a read-only repository root, and one writable
durable-state volume. It publishes no ports. Restarting either
side, an outage, or an ambiguous response leaves unresolved responsibility in
the state volume; later cycles retry the same deterministic locator until the
server returns a conclusive `new` or `already_accepted` receipt.

Machine-owned configuration is limited to mount locations, the state volume,
server URL, credential, image digest/version, and scan cadence. Capture routing
and safety policy are server-owned. Future server-issued instructions or policy
must not be copied into this machine configuration surface.

Runtime diagnostics are JSON Lines on stderr. They contain only event names,
the fixed `codex` adapter name, content-free reason codes, and aggregate safety
outcomes. Stdout is reserved for transport and remains empty; transcript text,
safe candidates, hook payloads, credentials, paths/local identifiers, and
complete HTTP requests or responses are never operational logs.

Current-session `rollout-*.jsonl` files beneath the mounted sessions tree are
discovered normally. The archive mount is not a historical import surface: an
archived rollout is selected only when its transcript identity matches an
existing non-empty durable queue. Unrelated archives, the Codex home root, and
root-level `history.jsonl` are not capture inputs; historical import requires
separate future authorization.

Images are published only under explicit release versions and registry
digests. There is no `latest` fallback and the runtime never updates itself.
