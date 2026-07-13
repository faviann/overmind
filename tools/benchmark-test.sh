#!/usr/bin/env bash
set -euo pipefail

readonly root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly results_dir="$root/tests/MemSrv.Tests/TestResults/benchmark"
readonly scratch_database="memory_test_benchmark_$$"

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

measure_memctl_startup() {
  local status=0
  src/MemCtl/bin/Debug/net10.0/MemCtl --help >/dev/null 2>&1 || status=$?
  [[ $status -eq 2 ]]
}

cleanup() {
  docker compose exec -T postgres psql -X -U overmind -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP DATABASE IF EXISTS \"$scratch_database\" WITH (FORCE)" >/dev/null 2>&1 || true
}
trap cleanup EXIT

printf 'Phase timings (wall clock)\n'
measure 'Docker/PostgreSQL readiness' make db-up
measure 'Build/restore' dotnet build memsrv.sln
measure 'Test discovery/host startup' \
  dotnet test tests/MemSrv.Tests --no-build --list-tests
measure 'Template validation/migration' tools/test-db.sh template
measure 'Disposable database/schema clone' tools/test-db.sh reset "$scratch_database"
measure 'Child CLI startup (memctl help)' measure_memctl_startup
measure 'Child server startup/fail-closed (5)' \
  dotnet test tests/MemSrv.Tests --no-build --filter 'FullyQualifiedName~ServerStartupTests'
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
