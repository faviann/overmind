#!/usr/bin/env bash
set -euo pipefail

readonly template=memory_test_template
readonly lock_file=/tmp/overmind-test-template.lock
readonly command=${1:-}
readonly root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
readonly compose=(docker compose --file "$root/compose.dev.yaml")
readonly memctl_apphost=${MEMCTL_APPHOST:-src/MemCtl/bin/Debug/net10.0/MemCtl}
readonly external_admin_connection=${MEMSRV_TEST_ADMIN_CONNECTION_STRING:-}
readonly memsrv_role_lock_id=757002524895691804

maintenance_psql() {
  if [[ -n $external_admin_connection ]]; then
    psql --dbname="$external_admin_connection" "$@"
  else
    "${compose[@]}" exec -T postgres psql -U overmind -d postgres "$@"
  fi
}

database_connection() {
  local database=$1 base query=''
  if [[ -z $external_admin_connection ]]; then
    printf 'postgres://overmind:overmind_dev@127.0.0.1:55432/%s' "$database"
    return
  fi

  base=$external_admin_connection
  if [[ $base == *\?* ]]; then
    query="?${base#*\?}"
    base=${base%%\?*}
  fi
  printf '%s/%s%s' "${base%/*}" "$database" "$query"
}

preflight_external() {
  [[ $external_admin_connection =~ ^postgres(ql)?://[^/]+/.+ ]] || {
    printf '%s must be a PostgreSQL connection URL naming an existing maintenance database (normally postgres).\n' \
      'MEMSRV_TEST_ADMIN_CONNECTION_STRING' >&2
    exit 2
  }
  command -v psql >/dev/null || {
    printf 'external test database mode requires the PostgreSQL psql client on PATH.\n' >&2
    exit 2
  }

  local facts version superuser role_facts
  if ! facts=$(maintenance_psql -XAtv ON_ERROR_STOP=1 -F '|' -c \
    "SELECT current_setting('server_version_num'), rolsuper FROM pg_roles WHERE rolname = current_user"); then
    printf 'cannot connect to the external PostgreSQL test instance; check MEMSRV_TEST_ADMIN_CONNECTION_STRING.\n' >&2
    exit 1
  fi
  IFS='|' read -r version superuser <<<"$facts"
  if [[ ! $version =~ ^18[0-9]{4}$ ]]; then
    printf 'external test database must be PostgreSQL 18; server reported version_num=%s.\n' \
      "${version:-unknown}" >&2
    exit 1
  fi
  if [[ $superuser != t ]]; then
    printf 'external test database authority must be a PostgreSQL superuser (database/role lifecycle and grant checks require it).\n' >&2
    exit 1
  fi

  if ! role_facts=$(maintenance_psql -XAtqv ON_ERROR_STOP=1 \
    -v memsrv_role_lock_id="$memsrv_role_lock_id" <<'SQL' | sed -n 's/^role=//p'
SELECT pg_advisory_lock(:memsrv_role_lock_id);
SELECT 'role=' || concat_ws('|',
  r.rolcanlogin,
  r.rolsuper,
  r.rolcreatedb,
  r.rolcreaterole,
  r.rolreplication,
  r.rolbypassrls,
  r.rolinherit,
  r.rolconnlimit = -1,
  r.rolvaliduntil IS NULL,
  r.rolconfig IS NULL,
  r.rolpassword IS NOT NULL,
  NOT EXISTS (SELECT FROM pg_auth_members m WHERE m.member = r.oid))
FROM pg_roles r
WHERE r.rolname = 'memsrv';
SELECT pg_advisory_unlock(:memsrv_role_lock_id);
SQL
  ); then
    printf 'could not inspect the external PostgreSQL memsrv role.\n' >&2
    exit 1
  fi
  if [[ -z $role_facts ]]; then
    printf 'external test database is missing the required memsrv role; provision it as a restricted LOGIN role before running tests.\n' >&2
    exit 1
  fi
  if [[ $role_facts != 't|f|f|f|f|f|t|t|t|t|t|t' ]]; then
    printf '%s\n' \
      'external memsrv role has incompatible attributes; require LOGIN, INHERIT, a password, no elevated role flags or memberships, and default connection/configuration limits.' >&2
    exit 1
  fi
}

provision_environment() {
  if [[ -n $external_admin_connection ]]; then
    preflight_external
  else
    "${compose[@]}" up -d --wait postgres
  fi
  sweep_databases
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
  MEMSRV_ADMIN_CONNECTION_STRING="$(database_connection "$template")" \
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
  provision) provision_environment ;;
  preflight) [[ -n $external_admin_connection ]] || exit 0; preflight_external ;;
  template) with_template_lock ;;
  reset) reset_database "${2:-${MEMSRV_TEST_DATABASE:-memory_test}}" ;;
  sweep) sweep_databases ;;
  *) printf 'usage: %s {provision|preflight|template|reset [database]|sweep}\n' "$0" >&2; exit 2 ;;
esac
