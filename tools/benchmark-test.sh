#!/usr/bin/env bash
set -euo pipefail

readonly root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly scratch_database="memory_test_benchmark_$$"
readonly benchmark_dir=$(mktemp -d "${TMPDIR:-/tmp}/memsrv-benchmark.XXXXXX")
readonly results_dir="$benchmark_dir/results"
readonly keys_path="$benchmark_dir/keys.yaml"
readonly server_stdout="$benchmark_dir/server.stdout"
readonly server_stderr="$benchmark_dir/server.stderr"
readonly compose=(docker compose --file "$root/compose.dev.yaml")
server_pid=

cd "$root"
mkdir -p "$results_dir"
rm -f "$results_dir"/*.trx

seconds_since() {
  local started=$1 finished
  finished=$(date +%s%N)
  awk -v start="$started" -v finish="$finished" 'BEGIN { printf "%.3f", (finish-start)/1000000000 }'
}

measure() {
  local label=$1
  shift
  local started
  started=$(date +%s%N)
  "$@"
  printf '%-38s %8ss\n' "$label" "$(seconds_since "$started")"
}

measure_memctl_no_command_startup() {
  local status=0
  src/MemCtl/bin/Debug/net10.0/MemCtl >/dev/null 2>&1 || status=$?
  [[ $status -eq 2 ]]
}

stop_server() {
  local deadline state
  [[ -n ${server_pid:-} ]] || return 0
  if kill -0 "$server_pid" 2>/dev/null; then
    kill -TERM -- "-$server_pid" 2>/dev/null || kill -TERM "$server_pid" 2>/dev/null || true
    deadline=$((SECONDS + 5))
    while kill -0 "$server_pid" 2>/dev/null && (( SECONDS < deadline )); do
      state=$(ps -o stat= -p "$server_pid" 2>/dev/null || true)
      [[ $state == Z* ]] && break
      sleep 0.05
    done
    if kill -0 "$server_pid" 2>/dev/null; then
      state=$(ps -o stat= -p "$server_pid" 2>/dev/null || true)
      if [[ $state != Z* ]]; then
        kill -KILL -- "-$server_pid" 2>/dev/null || kill -KILL "$server_pid" 2>/dev/null || true
      fi
    fi
  fi
  wait "$server_pid" 2>/dev/null || true
  server_pid=
}

measure_server_readiness() {
  local port started finished deadline
  port=$(python3 - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)
  printf '%s\n' \
    'keys:' \
    '  - key: benchmark-key' \
    '    agent_id: benchmark-agent' \
    '    default_namespace: memory-system' \
    '    allowed_namespaces: [memory-system]' >"$keys_path"
  : >"$server_stdout"
  : >"$server_stderr"

  started=$(date +%s%N)
  MEMSRV_HTTP_URL="http://127.0.0.1:$port" \
  MEMSRV_AGENT_KEYS_PATH="$keys_path" \
  MEMSRV_CONNECTION_STRING="Host=127.0.0.1;Port=55432;Database=$scratch_database;Username=memsrv;Password=memsrv_dev" \
    setsid src/MemSrv.Server/bin/Debug/net10.0/MemSrv.Server >"$server_stdout" 2>"$server_stderr" &
  server_pid=$!
  deadline=$((SECONDS + 20))
  until curl --fail --silent --show-error "http://127.0.0.1:$port/healthz" >/dev/null 2>&1; do
    if ! kill -0 "$server_pid" 2>/dev/null; then
      printf 'benchmark server exited before readiness:\n' >&2
      sed -n '1,120p' "$server_stderr" >&2
      return 1
    fi
    if (( SECONDS >= deadline )); then
      printf 'benchmark server did not become healthy within 20s:\n' >&2
      sed -n '1,120p' "$server_stderr" >&2
      return 1
    fi
    sleep 0.05
  done
  finished=$(date +%s%N)

  stop_server
  [[ ! -s $server_stdout ]] || {
    printf 'benchmark HTTP server wrote to stdout:\n' >&2
    sed -n '1,120p' "$server_stdout" >&2
    return 1
  }
  printf '%-38s %8ss\n' 'Child server HTTP readiness' \
    "$(awk -v start="$started" -v finish="$finished" 'BEGIN { printf "%.3f", (finish-start)/1000000000 }')"
}

cleanup() {
  stop_server
  "${compose[@]}" exec -T postgres psql -X -U overmind -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP DATABASE IF EXISTS \"$scratch_database\" WITH (FORCE)" >/dev/null 2>&1 || true
  rm -rf "$benchmark_dir"
}
trap cleanup EXIT

printf 'Phase timings (wall clock)\n'
measure 'Docker/PostgreSQL readiness' make db-up
measure 'Build/restore' dotnet build memsrv.sln
measure 'Test discovery/host startup' \
  dotnet test tests/MemSrv.Tests --no-build --list-tests
measure 'Template validation/migration' tools/test-db.sh template
measure 'Disposable database/schema clone' tools/test-db.sh reset "$scratch_database"
measure 'Child CLI no-command startup' measure_memctl_no_command_startup
measure_server_readiness
measure 'Full test phase' \
  env MEMSRV_TEST_RESULTS_DIR="$results_dir" tools/run-test-suite.sh

python3 - "$results_dir" <<'PY'
import glob
import os
import sys
import xml.etree.ElementTree as ET

directory = sys.argv[1]
results = []
counts = {"Passed": 0, "Failed": 0, "NotExecuted": 0}
for path in sorted(glob.glob(os.path.join(directory, "*.trx"))):
    root = ET.parse(path).getroot()
    for node in root.iter():
        if not node.tag.endswith("UnitTestResult"):
            continue
        outcome = node.attrib.get("outcome", "")
        counts[outcome] = counts.get(outcome, 0) + 1
        if outcome != "Passed":
            continue
        hours, minutes, seconds = node.attrib["duration"].split(":")
        duration = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
        results.append((duration, node.attrib.get("testName", "<unknown>")))

print("\nTRX test-body summary")
print(f"passed={counts.get('Passed', 0)} failed={counts.get('Failed', 0)} skipped={counts.get('NotExecuted', 0)}")
print(f"sum of successful test durations={sum(item[0] for item in results):.3f}s")
print("slowest successful tests:")
for duration, name in sorted(results, reverse=True)[:10]:
    marker = " OVER 10s" if duration > 10 else ""
    print(f"  {duration:8.3f}s  {name}{marker}")
PY
