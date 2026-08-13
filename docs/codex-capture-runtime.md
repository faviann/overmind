# Codex catch-up runtime

The supported Linux-first baseline is one headless, Codex-only OCI runtime per
user. It scans the explicitly mounted Codex current-session rollout tree immediately at
startup and on a non-overlapping jittered schedule, claims completed records in
durable local state, and converges them through the central capture HTTP API.
If Codex moves an already-queued rollout into its separately mounted archive,
the runtime finds only that durable responsibility there and revalidates the
same transcript and locator evidence before retrying it.
There is no Claude production adapter, arbitrary listener, Docker socket,
database credential, privileged mode, or self-update path. The only inbound
surface is the fixed `POST /wake` endpoint on host loopback `127.0.0.1:43191`.
The reference Linux Compose topology keeps the scanner on ordinary isolated
container networking. A fixed-function sidecar from the same immutable image
shares the scanner's network namespace and publishes only host
`127.0.0.1:43191`. It accepts connections only from the bridge's host gateway
and relays one bounded request to the scanner's still-loopback-only listener;
container peers cannot invoke that listener or adapter. The sidecar has no
mounts, credentials, configurable destination, or general proxy surface.

## Install the version-pinned Codex hooks

The package at `packages/codex-capture-hooks/0.147.0` supports Codex CLI
0.147.0 exactly. After reviewing it, an operator installs it explicitly with
`./install.sh`; `./upgrade.sh` is the corresponding explicit same-version
replacement. Installation refuses an existing unrelated `hooks.json` so the
operator can merge it deliberately. The runtime never edits Codex configuration.

The package registers session start/end, prompt submission, pre/permission/post
tool use, pre/post compaction, subagent start/stop, and stop. Each native hook
runs synchronously with a one-second handler bound. Its command discards
stdin, sends an empty `POST` only to `http://127.0.0.1:43191/wake`, suppresses
all output, applies a 250 ms client timeout, and exits successfully even when
the runtime is absent. Disabled or skipped/untrusted hooks affect latency only:
startup and periodic transcript scans remain the source of completeness.

## Enrollment

The default credentialless startup uses browser-mediated pairing. When no
`OVERMIND_CAPTURE_CREDENTIAL` is configured and the durable state volume has no
previously delivered credential, the runtime:

1. creates or reuses a mode-`0600` Codex installation identity in
   `OVERMIND_CAPTURE_STATE_DIR`;
2. sends that detected identity and the detected machine name to
   `POST /capture/v1/pairing-requests`;
3. writes the returned non-secret verification URL and user code to stderr;
4. polls `GET /capture/v1/pairing-requests/{requestId}` with the secret polling
   capability until an operator approves or the request expires; and
5. stores the capture credential exactly once in the state volume as the
   mode-`0600` `capture-credential` file.

The operator opens `/capture/console/pair/{userCode}`, signs in through the
configured OIDC provider, verifies the detected machine and installation, and
chooses the label, allowed repository route patterns, and special namespace
mappings, plus any directory routes that select those mappings. The server
derives the operator identity from the OIDC `sub` claim.
It derives the capture `agent_id` and binding identity itself; neither is a
pairing input. A Codex installation can acquire only one binding, including
when multiple requests or approvals race.

The user code and verification URL are safe to display. The polling token and
delivered `mcap_…` credential are secrets: neither is put in the URL or logs.
Only the polling-token hash is persisted long-term. Credential plaintext exists
in transient delivery state only between approval and its first authenticated
poll, which atomically returns it once and clears that plaintext. Expiry removes
authority from a still-pending request. Once approval commits before expiry,
delivery remains available exactly once across expiry or an outage; cancellation
cannot revoke that already-created binding.

Pre-provisioned capture credentials remain supported for unattended rollout or
break-glass operation. When `OVERMIND_CAPTURE_CREDENTIAL` is present, the
runtime uses it directly and does not pair. An operator can create such a
capture-only credential in a mode-`0600` file and enroll it with `memctl`:

```sh
memctl capture enroll my-codex-runtime \
  --harness codex \
  --agent-id capture:my-codex-runtime \
  --credential-file /run/secrets/codex-capture-key
```

In either enrollment mode, the `mcap_…` credential authorizes capture writes
only: it is not an MCP agent key and provides no console access,
captured-content reads, or database access.

## Run the immutable image

Copy `.env.capture.example` to an ignored operator-owned environment file,
replace every required placeholder, optionally uncomment a pre-provisioned
credential (otherwise browser pairing runs), restrict it to the operator before
starting Compose, and use an immutable version or registry digest:

```sh
chmod 0600 .env.capture
docker compose --env-file .env.capture -f compose.capture.yaml up -d
```

The reference scanner runtime has a read-only container filesystem and exactly
four declared mounts: the read-only `~/.codex/sessions` tree, the read-only
`~/.codex/archived_sessions` tree, a read-only repository root, and one writable
durable-state volume. The mountless wake sidecar publishes only
`127.0.0.1:43191` from the host into their ordinary bridge namespace; its
source gate rejects container peers before reaching the scanner's loopback-bound
wake endpoint. Restarting either
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
