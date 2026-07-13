#!/usr/bin/env bash
set -euo pipefail

pull_image=false
if [[ ${1:-} == --pull ]]; then
  pull_image=true
  shift
fi

readonly image=${1:-}
if [[ -z $image || $# -ne 1 ]]; then
  printf 'usage: %s [--pull] <image>\n' "$0" >&2
  exit 2
fi
runtime_image=$image

for command in curl docker; do
  command -v "$command" >/dev/null || {
    printf 'required command not found: %s\n' "$command" >&2
    exit 2
  }
done

readonly suffix="$$-$RANDOM"
readonly network="overmind-release-smoke-$suffix"
readonly postgres="overmind-release-postgres-$suffix"
readonly server="overmind-release-server-$suffix"
readonly scratch=$(mktemp -d)
readonly keys="$scratch/agent-keys.yaml"
readonly headers="$scratch/mcp-headers"
readonly response="$scratch/mcp-response"

cleanup() {
  local status=$?
  if (( status != 0 )) && docker inspect "$server" >/dev/null 2>&1; then
    printf 'release server logs:\n' >&2
    docker logs "$server" >&2 || true
  fi
  docker rm -fv "$server" "$postgres" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf "$scratch"
  exit "$status"
}
trap cleanup EXIT

wait_until() {
  local description=$1
  shift
  for _ in $(seq 1 60); do
    if "$@"; then
      return
    fi
    sleep 1
  done
  printf 'timed out waiting for %s\n' "$description" >&2
  "$@"
}

postgres_ready() {
  docker exec "$postgres" pg_isready -U overmind -d postgres >/dev/null 2>&1
}

http_healthy() {
  curl --fail --silent --show-error "$health" >/dev/null 2>&1
}

run_migration() {
  docker run --rm --network "$network" \
    -e MEMSRV_ADMIN_CONNECTION_STRING="postgres://overmind:overmind_dev@$postgres:5432/memory" \
    --entrypoint memctl \
    "$runtime_image" migrate | grep -Fx 'migrations applied' >/dev/null
}

if $pull_image; then
  docker pull "$image"
  readonly repo_digest=$(docker image inspect --format '{{join .RepoDigests "\n"}}' "$image" | head -n 1)
  if [[ -z $repo_digest ]]; then
    printf 'pulled image has no registry digest: %s\n' "$image" >&2
    exit 1
  fi
  runtime_image=$repo_digest
  printf 'pulled release digest: %s\n' "$repo_digest"
fi

cat >"$keys" <<'YAML'
keys:
  - key: release-smoke-key
    agent_id: release-smoke
    default_namespace: memory-system
    allowed_namespaces: [memory-system]
YAML

docker network create "$network" >/dev/null
docker run -d --name "$postgres" --network "$network" \
  -e POSTGRES_USER=overmind \
  -e POSTGRES_PASSWORD=overmind_dev \
  -e POSTGRES_DB=postgres \
  postgres:18 >/dev/null

wait_until 'PostgreSQL readiness' postgres_ready
docker exec "$postgres" psql -v ON_ERROR_STOP=1 -U overmind -d postgres \
  -c "CREATE ROLE memsrv LOGIN PASSWORD 'memsrv_dev'" \
  -c "CREATE DATABASE memory" >/dev/null

run_migration

docker run -d --name "$server" --network "$network" \
  -p 127.0.0.1::8080 \
  -v "$keys:/run/secrets/agent-keys.yaml:ro" \
  -e MEMSRV_CONNECTION_STRING="postgres://memsrv:memsrv_dev@$postgres:5432/memory" \
  -e MEMSRV_AGENT_KEYS_PATH=/run/secrets/agent-keys.yaml \
  "$runtime_image" >/dev/null

readonly endpoint="http://$(docker port "$server" 8080/tcp | head -n 1)/mcp"
readonly health="${endpoint%/mcp}/healthz"
wait_until 'database-backed health endpoint' http_healthy
[[ $(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$health") == 200 ]]

readonly initialize_request='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"release-smoke","version":"1.0"}}}'
readonly unauthenticated_status=$(curl --silent --show-error \
  --output /dev/null --write-out '%{http_code}' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data "$initialize_request" \
  "$endpoint")
[[ $unauthenticated_status == 401 ]]

curl --fail-with-body --silent --show-error \
  --dump-header "$headers" \
  --output "$response" \
  -H 'Authorization: Bearer release-smoke-key' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data "$initialize_request" \
  "$endpoint"

grep -Eiq '^Mcp-Session-Id:' "$headers"
grep -Fq '"serverInfo"' "$response"

printf 'release image smoke passed: %s\n' "$image"
