#!/usr/bin/env bash
set -euo pipefail

readonly template=memory_test_template
readonly lock_file=/tmp/overmind-test-template.lock
readonly command=${1:-}
readonly memctl_apphost=${MEMCTL_APPHOST:-src/MemCtl/bin/Debug/net10.0/MemCtl}

maintenance_psql() {
  docker compose exec -T postgres psql -U overmind -d postgres "$@"
}

quote_identifier() {
  local value=$1
  printf '"%s"' "${value//\"/\"\"}"
}

validate_database_name() {
  [[ $1 =~ ^[a-zA-Z_][a-zA-Z0-9_]*$ ]] || {
    printf 'invalid test database name: %s\n' "$1" >&2
    exit 2
  }
}

migration_fingerprint() {
  {
    while IFS= read -r migration; do
      printf '%s' "$(basename "$migration")"
      cat "$migration"
    done < <(LC_ALL=C find migrations -maxdepth 1 -type f -name '*.sql' -print | LC_ALL=C sort)
  } | sha256sum | awk '{print "overmind-test-migrations-sha256=" $1}'
}

ensure_template() {
  local fingerprint current
  fingerprint=$(migration_fingerprint)
  current=$(maintenance_psql -XAtqc \
    "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = '$template'")
  [[ $current == "$fingerprint" ]] && return

  maintenance_psql -Xv ON_ERROR_STOP=1 \
    -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$template' AND pid <> pg_backend_pid()" \
    -c "DROP DATABASE IF EXISTS $template" \
    -c "CREATE DATABASE $template"
  [[ -x $memctl_apphost ]] || {
    printf 'MemCtl apphost not found at %s; run dotnet build memsrv.sln first.\n' "$memctl_apphost" >&2
    exit 1
  }
  MEMSRV_ADMIN_CONNECTION_STRING="postgres://overmind:overmind_dev@127.0.0.1:55432/$template" \
    "$memctl_apphost" migrate
  maintenance_psql -Xv ON_ERROR_STOP=1 \
    -c "COMMENT ON DATABASE $template IS '$fingerprint'"
}

with_template_lock() {
  exec 9>"$lock_file"
  flock 9
  ensure_template
}

reset_database() {
  local database=$1 quoted
  validate_database_name "$database"
  quoted=$(quote_identifier "$database")
  with_template_lock
  maintenance_psql -Xv ON_ERROR_STOP=1 \
    -c "DROP DATABASE IF EXISTS $quoted WITH (FORCE)" \
    -c "CREATE DATABASE $quoted TEMPLATE $template"
}

sweep_databases() {
  local database quoted
  while IFS= read -r database; do
    [[ -z $database ]] && continue
    validate_database_name "$database"
    quoted=$(quote_identifier "$database")
    # Deliberately omit WITH (FORCE): a connection arriving after selection
    # makes DROP fail rather than killing a live suite.
    maintenance_psql -Xv ON_ERROR_STOP=0 -c "DROP DATABASE $quoted" >/dev/null || true
  done < <(maintenance_psql -XAtqc "
    SELECT d.datname
    FROM pg_database d
    WHERE d.datname LIKE 'memory\_test\_%' ESCAPE '\'
      AND d.datname <> '$template'
      AND shobj_description(d.oid, 'pg_database') LIKE 'overmind-test-created-at=%'
      AND substring(shobj_description(d.oid, 'pg_database') from 26)::timestamptz < now() - interval '6 hours'
      AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.datname = d.datname)")
}

case "$command" in
  template) with_template_lock ;;
  reset) reset_database "${2:-${MEMSRV_TEST_DATABASE:-memory_test}}" ;;
  sweep) sweep_databases ;;
  *) printf 'usage: %s {template|reset [database]|sweep}\n' "$0" >&2; exit 2 ;;
esac
