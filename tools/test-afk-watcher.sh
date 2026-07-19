#!/usr/bin/env bash
set -euo pipefail

# Black-box watcher scenarios. The real public command and per-issue runner are
# copied into a disposable repository; only gh, the selector, Sandcastle's tsx
# entry boundary, and sleep/clock are scripted.

readonly source_root="$(git rev-parse --show-toplevel)"
readonly real_git="$(command -v git)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

root="$fixture/workflow"
repo="$fixture/repo"
remote="$fixture/remote.git"
adapters="$fixture/adapters"
skills="$fixture/skills"
events="$fixture/events"
state="$fixture/state"
mkdir -p "$root/tools" "$root/node_modules/.bin" \
  "$root/node_modules/@ai-hero/sandcastle" "$repo" "$adapters" \
  "$skills/work-on/scripts" "$skills/implement" "$skills/tdd" \
  "$skills/code-review" "$skills/select-issue"
cp "$source_root/tools/run-afk-once.sh" "$source_root/tools/run-afk-issue.sh" \
  "$source_root/tools/afk-merge.sh" "$root/tools/"
touch "$root/node_modules/@ai-hero/sandcastle/package.json" "$events" "$state"
for skill in work-on implement tdd code-review select-issue; do
  touch "$skills/$skill/SKILL.md"
done

cat >"$adapters/git" <<'EOF'
#!/usr/bin/env bash
printf 'git %s\n' "$*" >>"$AFK_TEST_EVENTS"
exec "$AFK_TEST_REAL_GIT" "$@"
EOF

cat >"$adapters/codex" <<'EOF'
#!/usr/bin/env bash
[[ "${1:-} ${2:-}" == "login status" ]] && exit 0
exit 90
EOF

cat >"$skills/work-on/scripts/select-issue-codex.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'selector\n' >>"$AFK_TEST_EVENTS"
count="$(grep -c '^selector$' "$AFK_TEST_EVENTS")"
case "$AFK_TEST_SCENARIO" in
  chain)
    grep -qx claim-42 "$AFK_TEST_STATE" || { printf 'Selected issue: https://github.com/acme/widget/issues/42\n'; exit; }
    grep -qx claim-43 "$AFK_TEST_STATE" || printf 'Selected issue: https://github.com/acme/widget/issues/43\n'
    ;;
  live-add|drain-before-claim) printf 'Selected issue: https://github.com/acme/widget/issues/42\n' ;;
  drain|force)
    if grep -qx claim-42 "$AFK_TEST_STATE"; then
      printf 'Selected issue: https://github.com/acme/widget/issues/43\n'
    else
      printf 'Selected issue: https://github.com/acme/widget/issues/42\n'
    fi
    ;;
  frontier)
    if [[ "$count" -ge 2 ]]; then
      printf 'Selected issue: https://github.com/acme/widget/issues/42\n'
    fi
    ;;
  token-wait) ;;
  claim-race)
    printf 'authorization-removed\n' >>"$AFK_TEST_STATE"
    printf 'Selected issue: https://github.com/acme/widget/issues/42\n'
    ;;
  eligibility-race)
    if ! grep -qx blocker-added "$AFK_TEST_STATE"; then
      printf 'blocker-added\n' >>"$AFK_TEST_STATE"
      printf 'Selected issue: https://github.com/acme/widget/issues/42\n'
    fi
    ;;
  default-race)
    if ! grep -qx default-advanced "$AFK_TEST_STATE"; then
      "$AFK_TEST_REAL_GIT" -C "$AFK_TEST_REPO" commit --quiet --allow-empty -m 'Advance default during selection'
      "$AFK_TEST_REAL_GIT" -C "$AFK_TEST_REPO" push --quiet origin main
      printf 'default-advanced\n' >>"$AFK_TEST_STATE"
    fi
    printf 'Selected issue: https://github.com/acme/widget/issues/42\n'
    ;;
esac
EOF

cat >"$root/node_modules/.bin/tsx" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
issue="$2"
if [[ -e "$AFK_TEST_ACTIVE" ]]; then
  printf 'concurrent-agent %s\n' "$issue" >>"$AFK_TEST_EVENTS"
  exit 91
fi
touch "$AFK_TEST_ACTIVE"
trap 'rm -f "$AFK_TEST_ACTIVE"' EXIT
printf 'agent-base %s %s\n' "$issue" "$(git rev-parse origin/main)" >>"$AFK_TEST_EVENTS"
printf 'agent-pgid %s\n' "$(ps -o pgid= -p $$ | tr -d ' ')" >>"$AFK_TEST_EVENTS"
printf 'agent %s\n' "$issue" >>"$AFK_TEST_EVENTS"
if [[ "$AFK_TEST_SCENARIO" == drain || "$AFK_TEST_SCENARIO" == force ]]; then
  [[ "$AFK_TEST_SCENARIO" != force ]] || trap '' TERM
  while [[ ! -e "$AFK_TEST_RELEASE" ]]; do /usr/bin/sleep 0.02; done
else
  /usr/bin/sleep 0.02
fi
EOF

cat >"$adapters/sleep" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'sleep\n' >>"$AFK_TEST_EVENTS"
count="$(grep -c '^sleep$' "$AFK_TEST_EVENTS")"
case "$AFK_TEST_SCENARIO" in
  live-add)
    if [[ "$count" == 1 ]]; then printf 'authorized\n' >>"$AFK_TEST_STATE"; else kill -TERM "$PPID"; fi ;;
  frontier)
    if [[ "$count" == 1 ]]; then printf 'blocker-closed\n' >>"$AFK_TEST_STATE"; else kill -TERM "$PPID"; fi ;;
  token-wait)
    if [[ "$count" -ge 3 ]]; then kill -TERM "$PPID"; fi ;;
  claim-race|eligibility-race|default-race|drain-before-claim)
    kill -TERM "$PPID" ;;
  chain) kill -TERM "$PPID" ;;
  idle-stop) /usr/bin/sleep 30 ;;
  *) /usr/bin/sleep 0.02 ;;
esac
EOF

cat >"$adapters/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh %s\n' "$*" >>"$AFK_TEST_EVENTS"
case "$*" in
  "auth status") ;;
  "label list --limit 1000 --json name --jq .[].name") printf '%s\n' ready-for-agent Sandcastle afk-review ;;
  "repo view --json nameWithOwner --jq .nameWithOwner") printf 'acme/widget\n' ;;
  "repo view --json defaultBranchRef --jq .defaultBranchRef.name") printf 'main\n' ;;
  "issue list --state open --label ready-for-agent --label Sandcastle --limit 1000 --json number,updatedAt --jq sort_by(.number)")
    case "$AFK_TEST_SCENARIO" in
      chain)
        if grep -qx claim-43 "$AFK_TEST_STATE"; then printf '[]\n'
        elif grep -qx claim-42 "$AFK_TEST_STATE"; then printf '[{"number":43,"updatedAt":"b"}]\n'
        else printf '[{"number":42,"updatedAt":"a"}]\n'; fi ;;
      live-add)
        if grep -qx claim-42 "$AFK_TEST_STATE"; then printf '[]\n'
        elif grep -qx authorized "$AFK_TEST_STATE"; then printf '[{"number":42,"updatedAt":"a"}]\n'
        else printf '[]\n'; fi ;;
      idle-stop) printf '[]\n' ;;
      claim-race)
        if grep -qx authorization-removed "$AFK_TEST_STATE"; then printf '[]\n'
        else printf '[{"number":42,"updatedAt":"a"}]\n'; fi ;;
      drain|force)
        if grep -qx claim-42 "$AFK_TEST_STATE"; then printf '[{"number":43,"updatedAt":"b"}]\n'
        else printf '[{"number":42,"updatedAt":"a"}]\n'; fi ;;
      *)
        if grep -qx claim-42 "$AFK_TEST_STATE"; then printf '[]\n'
        else printf '[{"number":42,"updatedAt":"a"}]\n'; fi ;;
    esac ;;
  "issue list --state all --limit 1000 --json number,state,updatedAt --jq sort_by(.number)")
    if grep -qx blocker-added "$AFK_TEST_STATE"; then
      printf '[{"number":1,"state":"OPEN","updatedAt":"b"},{"number":42,"state":"OPEN","updatedAt":"b"}]\n'
    elif grep -qx blocker-closed "$AFK_TEST_STATE"; then
      printf '[{"number":1,"state":"CLOSED","updatedAt":"b"},{"number":42,"state":"OPEN","updatedAt":"a"}]\n'
    else
      printf '[{"number":1,"state":"OPEN","updatedAt":"a"},{"number":42,"state":"OPEN","updatedAt":"a"}]\n'
    fi ;;
  api\ --method\ DELETE\ repos/acme/widget/issues/*/labels/Sandcastle)
    issue_path="${4}"; issue="${issue_path#repos/acme/widget/issues/}"; issue="${issue%%/*}"
    printf 'claim-%s\n' "$issue" >>"$AFK_TEST_STATE"
    printf 'claim %s\n' "$issue" >>"$AFK_TEST_EVENTS" ;;
  issue\ view\ *\ --json\ state,labels)
    if [[ "$AFK_TEST_SCENARIO" == drain-before-claim && ! -e "$AFK_TEST_PRECLAIM_SIGNALLED" ]]; then
      touch "$AFK_TEST_PRECLAIM_SIGNALLED"
      kill -TERM "$PPID"
    fi
    issue="${3}"
    if grep -qx "claim-$issue" "$AFK_TEST_STATE"; then
      printf '{"state":"OPEN","labels":[{"name":"ready-for-agent"}]}\n'
    else
      printf '{"state":"OPEN","labels":[{"name":"ready-for-agent"},{"name":"Sandcastle"}]}\n'
    fi ;;
  pr\ list\ --head\ afk/issue-*\ --state\ open\ --json\ number\ --jq\ .[].number)
    branch="${4}"; issue="${branch##*-}"; printf '%s\n' "$((issue + 100))" ;;
  pr\ edit\ *\ --add-label\ afk-review) ;;
  api\ repos/acme/widget/branches/main/protection)
    if [[ "$AFK_TEST_SCENARIO" == chain ]]; then
      printf '%s\n' '{"required_pull_request_reviews":{"required_approving_review_count":0},"required_status_checks":{"strict":true,"checks":[{"context":"test"}]}}'
    else
      exit 1
    fi ;;
  pr\ view\ *\ --json\ body\ --jq\ .body)
    pr="${3}"; issue="$((pr - 100))"
    printf 'Closes #%s\n\n| Final workflow outcome | Closes |\n' "$issue" ;;
  pr\ view\ *\ --json\ state,mergeable,mergeStateStatus)
    printf '%s\n' '{"state":"OPEN","mergeable":"MERGEABLE","mergeStateStatus":"CLEAN"}' ;;
  pr\ merge\ *\ --merge)
    pr="${3}"
    "$AFK_TEST_REAL_GIT" -C "$AFK_TEST_REPO" commit --quiet --allow-empty -m "Merge scripted PR $pr"
    "$AFK_TEST_REAL_GIT" -C "$AFK_TEST_REPO" push --quiet origin main
    "$AFK_TEST_REAL_GIT" -C "$AFK_TEST_REPO" rev-parse HEAD >"$AFK_TEST_MERGE_OID" ;;
  pr\ view\ *\ --json\ state,mergedAt,mergeCommit)
    printf '{"state":"MERGED","mergedAt":"2026-01-01T00:00:00Z","mergeCommit":{"oid":"%s"}}\n' "$(cat "$AFK_TEST_MERGE_OID")" ;;
  issue\ view\ *\ --json\ state\ --jq\ .state) printf 'CLOSED\n' ;;
  *) printf 'unexpected gh call: %s\n' "$*" >&2; exit 90 ;;
esac
EOF

chmod +x "$root/tools/"*.sh "$root/node_modules/.bin/tsx" "$adapters/"* \
  "$skills/work-on/scripts/select-issue-codex.sh"

setup_scenario() {
  rm -rf "$repo" "$remote"
  mkdir -p "$repo"
  git init --bare --quiet "$remote"
  git -C "$repo" init --quiet --initial-branch=main
  git -C "$repo" config user.email watcher-test@example.invalid
  git -C "$repo" config user.name "Watcher test"
  git -C "$repo" commit --quiet --allow-empty -m base
  git -C "$repo" remote add origin "$remote"
  git -C "$repo" push --quiet -u origin main
  base_oid="$(git -C "$repo" rev-parse HEAD)"
  : >"$events"
  : >"$state"
  rm -f "$fixture/active" "$fixture/release" "$fixture/merge-oid"
  rm -f "$fixture/preclaim-signalled"
}

run_watcher() {
  local scenario="$1"
  cd "$repo"
  exec env PATH="$adapters:$PATH" AFK_SKILLS_ROOT="$skills" AFK_POLL_SECONDS=0 \
      AFK_TEST_REAL_GIT="$real_git" AFK_TEST_SCENARIO="$scenario" \
      AFK_TEST_EVENTS="$events" AFK_TEST_STATE="$state" \
      AFK_TEST_ACTIVE="$fixture/active" AFK_TEST_RELEASE="$fixture/release" \
      AFK_TEST_PRECLAIM_SIGNALLED="$fixture/preclaim-signalled" \
      AFK_TEST_REPO="$repo" AFK_TEST_MERGE_OID="$fixture/merge-oid" \
      "$root/tools/run-afk-once.sh"
}

run_foreground() {
  local scenario="$1"
  setup_scenario
  if ! (run_watcher "$scenario") >"$fixture/$scenario.out" 2>&1; then
    printf 'FAIL[%s]\n' "$scenario" >&2
    cat "$fixture/$scenario.out" >&2
    exit 1
  fi
}

run_background() {
  local scenario="$1"
  setup_scenario
  (run_watcher "$scenario") >"$fixture/$scenario.out" 2>&1 &
  watcher_pid=$!
}

wait_event() {
  local pattern="$1"
  for _ in {1..250}; do
    grep -q "$pattern" "$events" && return
    /usr/bin/sleep 0.02
  done
  printf 'timed out waiting for event: %s\n' "$pattern" >&2
  exit 1
}

wait_output() {
  local pattern="$1" file="$2"
  for _ in {1..250}; do
    grep -q "$pattern" "$file" && return
    /usr/bin/sleep 0.02
  done
  printf 'timed out waiting for output: %s\n' "$pattern" >&2
  exit 1
}

run_foreground chain
[[ "$(grep -c '^agent ' "$events")" == 2 ]] || {
  cat "$fixture/chain.out" >&2
  cat "$events" >&2
  exit 1
}
[[ "$(grep -c '^claim ' "$events")" == 2 ]]
[[ "$(grep -c '^selector$' "$events")" == 2 ]]
[[ "$(grep -c '^git fetch --quiet origin refs/heads/main:refs/remotes/origin/main$' "$events")" -ge 3 ]]
[[ "$(grep -c '^gh issue list --state open --label ready-for-agent --label Sandcastle ' "$events")" -ge 3 ]]
mapfile -t chain_bases < <(sed -n 's/^agent-base [0-9][0-9]* //p' "$events")
[[ "${#chain_bases[@]}" == 2 && "${chain_bases[0]}" != "${chain_bases[1]}" ]]
[[ "$(sed -n 's/^agent //p' "$events" | paste -sd, -)" == 42,43 ]]
! grep -q '^concurrent-agent ' "$events"

run_foreground live-add
[[ "$(grep -c '^agent 42$' "$events")" == 1 ]]
[[ "$(grep -c '^selector$' "$events")" == 1 ]]
grep -q "^agent-base 42 $base_oid$" "$events"

run_foreground frontier
[[ "$(grep -c '^selector$' "$events")" == 2 ]]
[[ "$(grep -c '^agent 42$' "$events")" == 1 ]]

run_foreground token-wait
[[ "$(grep -c '^selector$' "$events")" == 1 ]]
! grep -q '^agent ' "$events"
[[ "$(grep -c '^sleep$' "$events")" == 3 ]]

run_foreground claim-race
! grep -q '^claim ' "$events"
! grep -q '^agent ' "$events"

run_foreground eligibility-race
[[ "$(grep -c '^selector$' "$events")" == 2 ]]
! grep -q '^claim ' "$events"
! grep -q '^agent ' "$events"

run_foreground drain-before-claim
[[ -e "$fixture/preclaim-signalled" ]]
! grep -q '^claim ' "$events"
! grep -q '^agent ' "$events"
grep -q 'no issue was claimed' "$fixture/drain-before-claim.out"

run_foreground default-race
[[ "$(grep -c '^selector$' "$events")" == 2 ]]
[[ "$(grep -c '^claim 42$' "$events")" == 1 ]]
advanced_oid="$(git -C "$repo" rev-parse origin/main)"
[[ "$advanced_oid" != "$base_oid" ]]
grep -q "^agent-base 42 $advanced_oid$" "$events"

run_background drain
wait_event '^agent 42$'
kill -TERM "$watcher_pid"
touch "$fixture/release"
wait "$watcher_pid"
[[ "$(grep -c '^claim ' "$events")" == 1 ]]
grep -q 'draining current issue' "$fixture/drain.out"

run_background force
wait_event '^agent 42$'
kill -TERM "$watcher_pid"
wait_output 'draining current issue' "$fixture/force.out"
kill -TERM "$watcher_pid"
set +e
wait "$watcher_pid"
force_status=$?
set -e
[[ "$force_status" -ne 0 ]]
grep -q 'forcing termination' "$fixture/force.out"
forced_pgid="$(sed -n 's/^agent-pgid //p' "$events" | tail -n1)"
[[ -n "$forced_pgid" ]]
! ps -eo pgid= | tr -d ' ' | grep -qx "$forced_pgid"

run_background idle-stop
wait_event '^sleep$'
kill -TERM "$watcher_pid"
wait "$watcher_pid"
grep -q 'stopped while idle' "$fixture/idle-stop.out"

printf 'AFK live watcher black-box scenarios passed\n'
