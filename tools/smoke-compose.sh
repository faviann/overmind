#!/usr/bin/env bash
set -euo pipefail

readonly root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly image=${1:-}

if [[ -z $image || $# -ne 1 ]]; then
  printf 'usage: %s ghcr.io/faviann/overmind:<version>\n' "$0" >&2
  exit 2
fi

readonly repository=ghcr.io/faviann/overmind
if [[ $image != "$repository:"* || $image == "$repository:latest" ]]; then
  printf 'image must be an explicit non-latest %s tag: %s\n' "$repository" "$image" >&2
  exit 2
fi
readonly version=${image#"$repository:"}

for command in curl docker; do
  command -v "$command" >/dev/null || {
    printf 'required command not found: %s\n' "$command" >&2
    exit 2
  }
done

readonly suffix="$$-$RANDOM"
readonly project="overmind-compose-smoke-$suffix"
readonly scratch=$(mktemp -d)
readonly environment="$scratch/operator.env"
readonly keys="$scratch/agent-keys.yaml"
readonly compose_file="$root/compose.yaml"
readonly compose=(docker compose --file "$compose_file" --project-name "$project" --env-file "$environment")

cleanup() {
  local status=$?
  if (( status != 0 )) && [[ -f $environment ]]; then
    "${compose[@]}" logs --no-color >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$scratch"
  exit "$status"
}
trap cleanup EXIT

assert_required() {
  local missing=$1 entry name
  local -a supplied=()
  for entry in \
    "OVERMIND_VERSION=$version" \
    'POSTGRES_ADMIN_PASSWORD=synthetic-compose-admin' \
    'MEMSRV_PASSWORD=synthetic-compose-runtime'; do
    name=${entry%%=*}
    [[ $name == "$missing" ]] || supplied+=("$entry")
  done

  if env -u "$missing" "${supplied[@]}" docker compose --file "$compose_file" \
      --env-file /dev/null config >/dev/null 2>&1; then
    printf 'Compose accepted missing required value: %s\n' "$missing" >&2
    return 1
  fi
}

assert_required OVERMIND_VERSION
assert_required POSTGRES_ADMIN_PASSWORD
assert_required MEMSRV_PASSWORD

write_environment() {
  local runtime_password=$1
  printf '%s\n' \
    "OVERMIND_VERSION=$version" \
    'POSTGRES_ADMIN_PASSWORD=synthetic-compose-admin' \
    "MEMSRV_PASSWORD=$runtime_password" \
    'OVERMIND_HTTP_BIND=127.0.0.1' \
    'OVERMIND_HTTP_PORT=0' \
    "OVERMIND_AGENT_KEYS_FILE=$keys" \
    >"$environment"
}

write_environment synthetic-compose-runtime

printf '%s\n' \
  'keys:' \
  '  - key: synthetic-compose-bearer-key' \
  '    agent_id: compose-smoke' \
  '    default_namespace: memory-system' \
  '    allowed_namespaces: [memory-system]' \
  >"$keys"

"${compose[@]}" up -d --wait

readonly published_address=$("${compose[@]}" port server 8080 | head -n 1)
readonly endpoint="http://$published_address/mcp"
readonly health="http://$published_address/healthz"

[[ $(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$health") == 200 ]]

readonly initialize_request='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"compose-smoke","version":"1.0"}}}'
readonly unauthenticated_status=$(curl --silent --show-error \
  --output /dev/null --write-out '%{http_code}' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data "$initialize_request" \
  "$endpoint")
[[ $unauthenticated_status == 401 ]]

write_environment synthetic-compose-runtime-rotated
"${compose[@]}" up -d --wait

readonly rotated_published_address=$("${compose[@]}" port server 8080 | head -n 1)
readonly rotated_health="http://$rotated_published_address/healthz"
[[ $(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$rotated_health") == 200 ]]

printf 'reference Compose smoke passed: %s\n' "$image"
