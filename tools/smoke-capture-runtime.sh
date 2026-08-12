#!/usr/bin/env bash
set -euo pipefail

readonly server_image=${1:-}
readonly capture_image=${2:-}
if [[ -z $server_image || -z $capture_image || $# -ne 2 ]]; then
  printf 'usage: %s <server-image> <capture-runtime-image>\n' "$0" >&2
  exit 2
fi
for command in docker jq; do
  command -v "$command" >/dev/null || {
    printf 'required command not found: %s\n' "$command" >&2
    exit 2
  }
done

readonly suffix="$$-$RANDOM"
readonly network="overmind-capture-smoke-$suffix"
readonly postgres="capture-postgres-$suffix"
readonly server="capture-server-$suffix"
readonly runtime="capture-runtime-$suffix"
readonly scratch=$(mktemp -d)
readonly sessions="$scratch/sessions"
readonly archived_sessions="$scratch/archived_sessions"
readonly repository="$scratch/repository"
readonly state="$scratch/state"
readonly credential_file="$scratch/capture-key"
readonly agent_keys="$scratch/agent-keys.yaml"
readonly credential='mcap_capture_runtime_packaging_smoke_00000001'
mkdir -p "$sessions/2026/08/12" "$archived_sessions" "$repository" "$state"
chmod 0777 "$state"
printf '%s\n' "$credential" >"$credential_file"
chmod 0600 "$credential_file"
printf '%s\n' 'keys: []' >"$agent_keys"

cleanup() {
  local status=$?
  if (( status != 0 )); then
    docker logs "$server" >&2 2>/dev/null || true
    docker logs "$runtime" >&2 2>/dev/null || true
  fi
  docker rm -fv "$runtime" "$server" "$postgres" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf "$scratch"
  exit "$status"
}
trap cleanup EXIT

wait_until() {
  local description=$1
  shift
  for _ in $(seq 1 90); do
    if "$@"; then return; fi
    sleep 1
  done
  printf 'timed out waiting for %s\n' "$description" >&2
  "$@"
}

postgres_ready() {
  docker exec "$postgres" pg_isready -U overmind -d postgres >/dev/null 2>&1
}
server_ready() {
  docker exec "$server" curl --fail --silent http://127.0.0.1:8080/healthz >/dev/null 2>&1
}
all_capture_converged() {
  [[ -f $state/capture-state.json ]] &&
    jq -e '.streams | (length > 0 and all(.queue | length == 0))' \
      "$state/capture-state.json" >/dev/null
}
stream_count_is() {
  [[ -f $state/capture-state.json ]] &&
    jq -e --argjson expected "$1" \
      '.streams | length == $expected and all(.queue | length == 0)' \
      "$state/capture-state.json" >/dev/null
}
stream_count_at_least() {
  [[ -f $state/capture-state.json ]] &&
    jq -e --argjson expected "$1" '.streams | length >= $expected' \
      "$state/capture-state.json" >/dev/null
}
outstanding_stream_is_queued() {
  [[ -f $state/capture-state.json ]] &&
    jq -e --arg stream "$1" \
      '.streams[] | select(.sourceStream == $stream) |
       (.queue | length == 2) and .enqueuedThrough == 1' \
      "$state/capture-state.json" >/dev/null
}
outstanding_stream_converged() {
  [[ -f $state/capture-state.json ]] &&
    jq -e --arg stream "$1" \
      '.streams[] | select(.sourceStream == $stream) |
       (.queue | length == 0) and .enqueuedThrough == 1 and
       (.canonicalSourceStreamUuid != null)' \
      "$state/capture-state.json" >/dev/null
}
write_stream() {
  local path=$1
  local session_id=$2
  local expected_text=$3
  jq -cn --arg id "$session_id" \
    '{timestamp:"2026-08-12T12:00:00Z",type:"session_meta",payload:{id:$id,session_id:$id,cli_version:"smoke"}}' \
    >"$path"
  jq -cn --arg text "$expected_text" \
    '{timestamp:"2026-08-12T12:00:01Z",type:"response_item",payload:{type:"message",role:"user",content:[{type:"input_text",text:$text}]}}' \
    >>"$path"
}
run_memctl() {
  docker run --rm --network "$network" \
    -e MEMSRV_CONNECTION_STRING="postgres://memsrv:memsrv_dev@$postgres:5432/memory" \
    --entrypoint memctl "$server_image" "$@"
}
start_server() {
  docker run -d --name "$server" --network "$network" \
    -v "$agent_keys:/run/secrets/agent-keys.yaml:ro" \
    -e MEMSRV_CONNECTION_STRING="postgres://memsrv:memsrv_dev@$postgres:5432/memory" \
    -e MEMSRV_AGENT_KEYS_PATH=/run/secrets/agent-keys.yaml \
    -e MEMSRV_HTTP_URL=http://0.0.0.0:8080 \
    "$server_image" >/dev/null
  wait_until 'server health' server_ready
}
start_runtime() {
  docker run -d --name "$runtime" --network "$network" --read-only \
    --cap-drop ALL --security-opt no-new-privileges \
    -v "$sessions:/capture/sessions:ro" \
    -v "$archived_sessions:/capture/archived_sessions:ro" \
    -v "$repository:/capture/repository:ro" \
    -v "$state:/state" \
    -e OVERMIND_CAPTURE_URL="http://$server:8080" \
    -e OVERMIND_CAPTURE_CREDENTIAL="$credential" \
    -e OVERMIND_CAPTURE_SCAN_INTERVAL_MS=100 \
    -e OVERMIND_CAPTURE_SCAN_JITTER_MS=0 \
    "$capture_image" >/dev/null
}

verify_packaging_contract() {
  local rendered="$scratch/compose-rendered.json"
  OVERMIND_CAPTURE_IMAGE="$capture_image" \
  OVERMIND_CAPTURE_URL=http://capture.invalid \
  OVERMIND_CAPTURE_CREDENTIAL="$credential" \
  OVERMIND_CODEX_SESSIONS_ROOT="$sessions" \
  OVERMIND_CODEX_ARCHIVE_ROOT="$archived_sessions" \
  OVERMIND_REPOSITORY_ROOT="$repository" \
    docker compose -f compose.capture.yaml config --format json >"$rendered"
  jq -e '
    .services["codex-capture"] as $service |
    $service.read_only == true and
    $service.privileged != true and
    (($service.ports // []) | length == 0) and
    (($service.cap_drop // []) | index("ALL") != null) and
    (($service.security_opt // []) | index("no-new-privileges:true") != null) and
    ($service.image | endswith(":latest") | not) and
    (($service.environment | keys | map(contains("CONNECTION_STRING")) | any) | not) and
    ($service.volumes | length == 4) and
    ([ $service.volumes[] | select(.type == "bind" and .read_only == true) | .target ] |
      sort == ["/capture/archived_sessions", "/capture/repository", "/capture/sessions"]) and
    ([ $service.volumes[] | select(.type == "volume" and .read_only != true) | .target ] == ["/state"]) and
    ([ $service.volumes[].source | contains("docker.sock") ] | any | not)
  ' "$rendered" >/dev/null

  docker image inspect "$capture_image" | jq -e '
    .[0].Config.User == "65532:65532" and
    .[0].Config.Entrypoint == ["dotnet", "/app/CodexCaptureTracer.dll"] and
    ([.[0].Config.Env[] | select(startswith("OVERMIND_CODEX_ARCHIVE_ROOT="))] | length == 1)
  ' >/dev/null
}

assert_replay() {
  local source_stream_uuid=$1
  local expected_session_id=$2
  local expected_text=$3
  local replay
  replay=$(run_memctl capture replay "$source_stream_uuid")
  jq -e --arg session "$expected_session_id" --arg text "$expected_text" '
    .contractVersion == 1 and
    (.events | length >= 2) and
    any(.events[];
      .envelope.observation.sourceIdentity.externalSessionId == $session and
      .envelope.observation.sourceStreamUuid == $source_stream_uuid) and
    any(.events[];
      (.envelope.event.sessionId | startswith("capture:v1:")) and
      ([.envelope.event.payload | .. | strings] | index($text)) != null)
  ' --arg source_stream_uuid "$source_stream_uuid" <<<"$replay" >/dev/null
}

docker network create "$network" >/dev/null
verify_packaging_contract
docker run -d --name "$postgres" --network "$network" \
  -e POSTGRES_USER=overmind -e POSTGRES_PASSWORD=overmind_dev \
  -e POSTGRES_DB=postgres postgres:18 >/dev/null
wait_until 'PostgreSQL 18 readiness' postgres_ready
docker exec "$postgres" psql -v ON_ERROR_STOP=1 -U overmind -d postgres \
  -c "CREATE ROLE memsrv LOGIN PASSWORD 'memsrv_dev'" \
  -c "CREATE DATABASE memory" >/dev/null
docker run --rm --network "$network" \
  -e MEMSRV_ADMIN_CONNECTION_STRING="postgres://overmind:overmind_dev@$postgres:5432/memory" \
  --entrypoint memctl "$server_image" migrate >/dev/null
docker run --rm --network "$network" \
  -v "$credential_file:/run/secrets/capture-key:ro" \
  -e MEMSRV_CONNECTION_STRING="postgres://memsrv:memsrv_dev@$postgres:5432/memory" \
  --entrypoint memctl "$server_image" capture enroll capture-runtime-smoke \
    --harness codex --agent-id capture:capture-runtime-smoke \
    --credential-file /run/secrets/capture-key >/dev/null

readonly startup_session='01980000-0000-7000-8000-000000000001'
readonly scheduled_session='01980000-0000-7000-8000-000000000002'
readonly outstanding_session='01980000-0000-7000-8000-000000000003'
readonly unrelated_archive_session='01980000-0000-7000-8000-000000000004'
write_stream "$archived_sessions/rollout-unrelated.jsonl" \
  "$unrelated_archive_session" 'unrelated historical archive event'
write_stream "$sessions/2026/08/12/rollout-startup.jsonl" \
  "$startup_session" 'startup stream event'

start_server
start_runtime
wait_until 'startup-present Codex stream convergence' stream_count_is 1
readonly startup_uuid=$(jq -r '.streams[0].canonicalSourceStreamUuid' \
  "$state/capture-state.json")
assert_replay "$startup_uuid" "$startup_session" 'startup stream event'

write_stream "$sessions/2026/08/12/rollout-scheduled.jsonl" \
  "$scheduled_session" 'scheduled discovery event'
wait_until 'post-start scheduled Codex stream convergence' stream_count_is 2
readonly scheduled_uuid=$(jq -r \
  --arg startup "$startup_uuid" \
  '.streams[] | select(.canonicalSourceStreamUuid != $startup) | .canonicalSourceStreamUuid' \
  "$state/capture-state.json")
assert_replay "$scheduled_uuid" "$scheduled_session" 'scheduled discovery event'

readonly known_streams=$(jq -c '[.streams[].sourceStream]' "$state/capture-state.json")
docker rm -f "$server" >/dev/null
write_stream "$sessions/2026/08/12/rollout-outstanding.jsonl" \
  "$outstanding_session" 'durable responsibility event'
wait_until 'durable queued responsibility while server unavailable' \
  stream_count_at_least 3
readonly outstanding_stream=$(jq -r --argjson known "$known_streams" \
  '.streams[] | select((.sourceStream as $stream | $known | index($stream)) == null) |
   .sourceStream' "$state/capture-state.json")
[[ -n $outstanding_stream && $outstanding_stream != null ]]
wait_until 'specific outstanding stream position claim' \
  outstanding_stream_is_queued "$outstanding_stream"

docker rm -f "$runtime" >/dev/null
mv "$sessions/2026/08/12/rollout-outstanding.jsonl" \
  "$archived_sessions/rollout-outstanding.jsonl"
start_server
start_runtime
wait_until 'specific outstanding stream restart convergence' \
  outstanding_stream_converged "$outstanding_stream"
docker inspect -f '{{.State.Running}}' "$runtime" | grep -qx true
docker logs "$runtime" 2>&1 | jq -e -s \
  'any(.[]; .event == "capture_delivery_accepted")' >/dev/null
all_capture_converged
readonly outstanding_uuid=$(jq -r --arg stream "$outstanding_stream" \
  '.streams[] | select(.sourceStream == $stream) | .canonicalSourceStreamUuid' \
  "$state/capture-state.json")
assert_replay "$outstanding_uuid" "$outstanding_session" 'durable responsibility event'
stream_count_is 3

printf 'capture runtime packaging smoke passed: %s + %s\n' \
  "$server_image" "$capture_image"
