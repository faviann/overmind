#!/usr/bin/env bash
set -euo pipefail

readonly root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly project="$root/tests/MemSrv.Tests"
readonly results_dir=${MEMSRV_TEST_RESULTS_DIR:-}
readonly filters=(
  'FullyQualifiedName~MemSrv.Tests.MemoryServiceTests'
  'FullyQualifiedName~MemSrv.Tests.AcceptanceTests'
  'FullyQualifiedName~MemSrv.Tests.SchemaVerifierTests|FullyQualifiedName~MemSrv.Tests.TestDatabaseLifecycleTests|FullyQualifiedName~MemSrv.Tests.ServerStartupTests'
  'FullyQualifiedName!~MemSrv.Tests.MemoryServiceTests&FullyQualifiedName!~MemSrv.Tests.AcceptanceTests&FullyQualifiedName!~MemSrv.Tests.SchemaVerifierTests&FullyQualifiedName!~MemSrv.Tests.TestDatabaseLifecycleTests&FullyQualifiedName!~MemSrv.Tests.ServerStartupTests'
)

pids=()
for index in "${!filters[@]}"; do
  args=(dotnet test "$project" --no-build --filter "${filters[$index]}")
  if [[ -n $results_dir ]]; then
    mkdir -p "$results_dir"
    args+=(--logger "trx;LogFileName=shard-$((index + 1)).trx" --results-directory "$results_dir")
  fi
  "${args[@]}" &
  pids+=("$!")
done

status=0
for pid in "${pids[@]}"; do
  if ! wait "$pid"; then
    status=1
  fi
done
exit "$status"
