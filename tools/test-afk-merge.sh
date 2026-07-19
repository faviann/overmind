#!/usr/bin/env bash
set -euo pipefail

# Black-box scenarios for the guarded merge stage (tools/afk-merge.sh). Each
# scenario drives the REAL afk-merge.sh against fake gh/git adapters on PATH and
# a real fixture repository with a pre-seeded afk/issue-42 branch and worktree.
# No sandcastle, no codex: the merge gate is exercised in isolation.

readonly command_under_test="$(git rev-parse --show-toplevel)/tools/afk-merge.sh"
readonly real_git="$(command -v git)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

repo="$fixture/repo"
remote="$fixture/remote.git"
worktree="$fixture/worktree"
adapters="$fixture/adapters"
events="$fixture/events"
state="$fixture/state"
bodies="$fixture/bodies"
out="$fixture/out"
clock="$fixture/clock"
mkdir -p "$adapters" "$bodies"

# --- Fixture pull-request bodies --------------------------------------------
cat >"$bodies/closes" <<'EOF'
## Issues

Closes #42

## Workflow telemetry

| Field | Observed value |
|---|---|
| Final workflow outcome | Closes |
EOF

cat >"$bodies/progresses" <<'EOF'
## Issues

Progresses #42

## Workflow telemetry

| Field | Observed value |
|---|---|
| Final workflow outcome | Progresses |
EOF

# Contradiction: input issue under Closes but the durable telemetry outcome
# says Progresses (an inferred/unverified closure would have been downgraded).
cat >"$bodies/contradiction" <<'EOF'
## Issues

Closes #42

## Workflow telemetry

| Field | Observed value |
|---|---|
| Final workflow outcome | Progresses |
EOF

# Missing evidence: closing keyword present but no telemetry table at all.
cat >"$bodies/missing" <<'EOF'
## Issues

Closes #42
EOF

cat >"$bodies/followup" <<'EOF'
## Issues

Closes #42

## Follow-ups

- #77 - investigate an out-of-scope edge case

## Workflow telemetry

| Field | Observed value |
|---|---|
| Final workflow outcome | Closes |
EOF

readonly merge_clean='{"state":"OPEN","mergeable":"MERGEABLE","mergeStateStatus":"CLEAN"}'
readonly merge_conflict='{"state":"OPEN","mergeable":"CONFLICTING","mergeStateStatus":"DIRTY"}'
readonly merge_blocked='{"state":"OPEN","mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED"}'

# --- Adapters ---------------------------------------------------------------
cat >"$adapters/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'git %s\n' "$*" >>"$AFK_TEST_EVENTS"
if [[ "$*" == "push origin --delete afk/issue-42" && "${AFK_TEST_FAIL_PUSH_DELETE:-0}" == 1 ]]; then
  exit 1
fi
exec "$AFK_TEST_REAL_GIT" "$@"
EOF

cat >"$adapters/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh %s\n' "$*" >>"$AFK_TEST_EVENTS"
case "$*" in
  "repo view --json nameWithOwner --jq .nameWithOwner")
    printf 'acme/widget\n' ;;
  "api repos/acme/widget/branches/main/protection")
    case "${AFK_TEST_PROTECTION:-good}" in
      404) printf 'Branch not protected (HTTP 404)\n' >&2; exit 1 ;;
      good) printf '%s\n' '{"required_pull_request_reviews":{"required_approving_review_count":0},"required_status_checks":{"strict":true,"checks":[{"context":"test"}]}}' ;;
      no-prs) printf '%s\n' '{"required_pull_request_reviews":null,"required_status_checks":{"strict":true,"checks":[{"context":"test"}]}}' ;;
      not-strict) printf '%s\n' '{"required_pull_request_reviews":{"required_approving_review_count":1},"required_status_checks":{"strict":false,"checks":[{"context":"test"}]}}' ;;
      missing-check) printf '%s\n' '{"required_pull_request_reviews":{"required_approving_review_count":1},"required_status_checks":{"strict":true,"checks":[{"context":"lint"}]}}' ;;
      *) printf 'unexpected protection fixture: %s\n' "${AFK_TEST_PROTECTION}" >&2; exit 91 ;;
    esac ;;
  "pr view 7 --json body --jq .body")
    cat "$AFK_TEST_BODY" ;;
  "api repos/acme/widget/issues/42/dependencies/blocked_by --paginate --jq .[].number")
    [[ "${AFK_TEST_DEPENDENCY_QUERY_FAIL:-0}" != 1 ]] || exit 1
    [[ -z "${AFK_TEST_BLOCKERS:-}" ]] || printf '%s\n' "$AFK_TEST_BLOCKERS" ;;
  "issue edit 77 --add-label needs-triage --add-label afk-review") ;;
  "pr checks 7 --required --json name,state,bucket,link")
    checks_call="$(grep -c '^gh pr checks 7 ' "$AFK_TEST_EVENTS")"
    case "${AFK_TEST_CHECKS:-pass}" in
      pass) printf '%s\n' '[{"name":"test","state":"SUCCESS","bucket":"pass","link":"https://github.com/acme/widget/actions/runs/100/job/1"}]' ;;
      retry-pass)
        if [[ "$checks_call" == 1 ]]; then
          printf '%s\n' '[{"name":"test","state":"FAILURE","bucket":"fail","link":"https://github.com/acme/widget/actions/runs/100/job/1"}]'
        else
          printf '%s\n' '[{"name":"test","state":"SUCCESS","bucket":"pass","link":"https://github.com/acme/widget/actions/runs/101/job/2"}]'
        fi ;;
      repeat-fail) printf '%s\n' '[{"name":"test","state":"FAILURE","bucket":"fail","link":"https://github.com/acme/widget/actions/runs/100/job/1"}]' ;;
      pending) printf '%s\n' '[{"name":"test","state":"IN_PROGRESS","bucket":"pending","link":"https://github.com/acme/widget/actions/runs/100/job/1"}]' ;;
      *) exit 91 ;;
    esac ;;
  "run rerun 100 --failed") ;;
  "pr view 7 --json state,mergeable,mergeStateStatus")
    printf '%s\n' "$AFK_TEST_MERGEABILITY" ;;
  "pr merge 7 --merge")
    printf 'pr-merged\n' >>"$AFK_TEST_STATE" ;;
  "pr view 7 --json state,mergedAt,mergeCommit")
    printf '%s\n' "$AFK_TEST_POST_MERGE" ;;
  "issue view 42 --json state --jq .state")
    printf '%s\n' "${AFK_TEST_ISSUE_STATE:-CLOSED}" ;;
  "pr edit 7 --add-label afk-review")
    [[ "${AFK_TEST_FAIL_REVIEW_LABEL:-0}" != 1 ]] || exit 1
    printf 'afk-review-added\n' >>"$AFK_TEST_STATE" ;;
  *) printf 'unexpected gh call: %s\n' "$*" >&2; exit 90 ;;
esac
EOF

cat >"$adapters/date" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == +%s ]]
cat "$AFK_TEST_CLOCK"
EOF

cat >"$adapters/sleep" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'sleep %s\n' "$*" >>"$AFK_TEST_EVENTS"
if [[ "$AFK_TEST_CHECKS" == pending ]]; then
  printf '3600\n' >"$AFK_TEST_CLOCK"
else
  current="$(cat "$AFK_TEST_CLOCK")"
  printf '%s\n' "$((current + 30))" >"$AFK_TEST_CLOCK"
fi
EOF

chmod +x "$adapters/git" "$adapters/gh" "$adapters/date" "$adapters/sleep"

# --- Fixture repository helpers ---------------------------------------------
setup_repo() {
  rm -rf "$repo" "$remote" "$worktree"
  git init --bare --quiet "$remote"
  git init --quiet --initial-branch=main "$repo"
  git -C "$repo" config user.email afk-test@example.invalid
  git -C "$repo" config user.name "AFK test"
  git -C "$repo" commit --quiet --allow-empty -m base
  git -C "$repo" remote add origin "$remote"
  git -C "$repo" push --quiet -u origin main
  git -C "$repo" worktree add --quiet -b afk/issue-42 "$worktree" main
  git -C "$worktree" commit --quiet --allow-empty -m 'Complete scripted AFK issue'
  git -C "$worktree" push --quiet -u origin afk/issue-42
}

# Simulate GitHub merging the pull request: land a --no-ff merge commit on the
# remote default branch and echo its oid so the fake gh can report it.
seed_merge() {
  git -C "$repo" merge --quiet --no-ff afk/issue-42 -m "Merge pull request #7"
  git -C "$repo" push --quiet origin main
  git -C "$repo" rev-parse HEAD
}

run_merge() {
  (
    cd "$repo"
    PATH="$adapters:$PATH" \
      AFK_TEST_EVENTS="$events" \
      AFK_TEST_STATE="$state" \
      AFK_TEST_REAL_GIT="$real_git" \
      AFK_TEST_CLOCK="$clock" \
      "$command_under_test" 42 afk/issue-42 main 7
  )
}

reset_events() {
  : >"$events"
  : >"$state"
  printf '0\n' >"$clock"
  export AFK_TEST_CHECKS=pass
  export AFK_TEST_BLOCKERS=''
  unset AFK_TEST_DEPENDENCY_QUERY_FAIL
  unset AFK_TEST_FAIL_REVIEW_LABEL
}

assert_no_merge_call() {
  if grep -q '^gh pr merge ' "$events"; then
    echo "FAIL[$1]: merge was attempted" >&2; exit 1
  fi
}
assert_afk_review_present() {
  grep -q '^gh pr edit 7 --add-label afk-review$' "$events" \
    || { echo "FAIL[$1]: afk-review not ensured" >&2; exit 1; }
}
assert_no_success_message() {
  if grep -q 'merged: pull request' "$out"; then
    echo "FAIL[$1]: success claimed" >&2; exit 1
  fi
}
assert_no_deletion() {
  if grep -Eq '^git (push origin --delete|branch -D|worktree remove)' "$events"; then
    echo "FAIL[$1]: artifacts were deleted" >&2; exit 1
  fi
}
assert_branch_present() {
  git -C "$repo" show-ref --verify --quiet refs/heads/afk/issue-42 \
    || { echo "FAIL[$1]: local branch deleted" >&2; exit 1; }
}
assert_worktree_present() {
  git -C "$repo" worktree list --porcelain \
    | grep -q '^branch refs/heads/afk/issue-42$' \
    || { echo "FAIL[$1]: worktree removed" >&2; exit 1; }
}
assert_pr_open() {
  # The gate never transitions PR state; "still open" is proven by the absence
  # of a merge call plus the preserved local artifacts.
  assert_no_merge_call "$1"
}

# --- Prohibited classes: each must refuse, preserve, keep afk-review ---------
prohibited=(
  # name              protection    body-file      merge-state
  "unprotected|404|closes|$merge_clean"
  "prs-not-required|no-prs|closes|$merge_clean"
  "not-strict|not-strict|closes|$merge_clean"
  "check-missing|missing-check|closes|$merge_clean"
  "progresses-pr|good|progresses|$merge_clean"
  "unverified-outcome|good|contradiction|$merge_clean"
  "missing-evidence|good|missing|$merge_clean"
  "conflict|good|closes|$merge_conflict"
)

for scenario in "${prohibited[@]}"; do
  IFS='|' read -r name protection body_name merge_state <<<"$scenario"
  setup_repo
  reset_events
  export AFK_TEST_PROTECTION="$protection"
  export AFK_TEST_BODY="$bodies/$body_name"
  export AFK_TEST_MERGEABILITY="$merge_state"
  export AFK_TEST_POST_MERGE='{"state":"MERGED","mergeCommit":{"oid":"unused"}}'
  export AFK_TEST_ISSUE_STATE=CLOSED
  if ! run_merge >"$out" 2>&1; then
    echo "FAIL[$name]: refusal must exit 0 (tracer awaits review)" >&2
    cat "$out" >&2; exit 1
  fi
  grep -q '^merge refused: ' "$out" \
    || { echo "FAIL[$name]: no refusal reason printed" >&2; cat "$out" >&2; exit 1; }
  grep -q 'awaits review' "$out" \
    || { echo "FAIL[$name]: awaits-review end state not reported" >&2; exit 1; }
  assert_no_merge_call "$name"
  assert_pr_open "$name"
  assert_afk_review_present "$name"
  assert_no_success_message "$name"
  assert_no_deletion "$name"
  assert_branch_present "$name"
  assert_worktree_present "$name"
done

# --- Prohibited: set-but-empty AFK_REQUIRED_CHECKS must fail closed ----------
setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_POST_MERGE='{"state":"MERGED","mergeCommit":{"oid":"unused"}}'
export AFK_TEST_ISSUE_STATE=CLOSED
export AFK_REQUIRED_CHECKS=" "
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[empty-checks]: refusal must exit 0" >&2; cat "$out" >&2; exit 1
fi
grep -q '^merge refused: no designated CI checks configured' "$out" \
  || { echo "FAIL[empty-checks]: did not refuse on empty check list" >&2; cat "$out" >&2; exit 1; }
assert_no_merge_call empty-checks
assert_afk_review_present empty-checks
assert_no_success_message empty-checks
assert_no_deletion empty-checks
assert_branch_present empty-checks
assert_worktree_present empty-checks
unset AFK_REQUIRED_CHECKS

# --- Required CI: one mechanical retry, then bounded human-review pause -----
setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_CHECKS=retry-pass
export AFK_TEST_POST_MERGE='{"state":"MERGED","mergeCommit":{"oid":"unused"}}'
export AFK_TEST_ISSUE_STATE=CLOSED
if run_merge >"$out" 2>&1; then
  # The fixture does not seed a real merge commit in this scenario, so reaching
  # post-merge verification proves CI allowed exactly one merge attempt.
  :
fi
[[ "$(grep -c '^gh run rerun 100 --failed$' "$events")" == 1 ]] \
  || { echo "FAIL[ci-retry]: expected exactly one mechanical rerun" >&2; cat "$events" >&2; exit 1; }
checks_line="$(grep -n '^gh pr checks 7 ' "$events" | head -n1 | cut -d: -f1)"
mergeability_line="$(grep -n '^gh pr view 7 --json state,mergeable,mergeStateStatus$' "$events" | head -n1 | cut -d: -f1)"
[[ "$checks_line" -lt "$mergeability_line" ]] \
  || { echo "FAIL[ci-retry]: CI must settle before final mergeability evaluation" >&2; cat "$events" >&2; exit 1; }
grep -q '^gh pr merge 7 --merge$' "$events" \
  || { echo "FAIL[ci-retry]: merge gate did not continue after passing retry" >&2; exit 1; }

setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_CHECKS=repeat-fail
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[ci-repeat]: repeated CI failure should pause for review" >&2; cat "$out" >&2; exit 1
fi
[[ "$(grep -c '^gh run rerun 100 --failed$' "$events")" == 1 ]] \
  || { echo "FAIL[ci-repeat]: retry was not bounded to one" >&2; cat "$events" >&2; exit 1; }
grep -q 'required CI failed again after one mechanical retry' "$out"
assert_pr_open ci-repeat
assert_afk_review_present ci-repeat
assert_no_deletion ci-repeat
assert_branch_present ci-repeat
assert_worktree_present ci-repeat

setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_CHECKS=repeat-fail
export AFK_TEST_FAIL_REVIEW_LABEL=1
if run_merge >"$out" 2>&1; then
  echo "FAIL[ci-repeat-label]: missing afk-review must fail loudly" >&2; cat "$out" >&2; exit 1
fi
grep -q 'could not ensure afk-review' "$out"
! grep -q 'awaits review' "$out"
assert_no_merge_call ci-repeat-label
assert_no_deletion ci-repeat-label
assert_branch_present ci-repeat-label
assert_worktree_present ci-repeat-label

setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_CHECKS=pending
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[ci-timeout]: CI timeout should pause for review" >&2; cat "$out" >&2; exit 1
fi
grep -q '^sleep 30$' "$events"
grep -q 'required CI did not complete within 3600 seconds' "$out"
assert_pr_open ci-timeout
assert_afk_review_present ci-timeout
assert_no_deletion ci-timeout
assert_branch_present ci-timeout
assert_worktree_present ci-timeout

# --- Discoveries: label for triage, never authorize, block conservatively ---
setup_repo
merge_oid="$(seed_merge)"
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/followup"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_POST_MERGE="{\"state\":\"MERGED\",\"mergedAt\":\"2026-01-01T00:00:00Z\",\"mergeCommit\":{\"oid\":\"$merge_oid\"}}"
export AFK_TEST_ISSUE_STATE=CLOSED
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[nonblocking-discovery]: non-blocking discovery stopped current issue" >&2; cat "$out" >&2; exit 1
fi
grep -q '^gh issue edit 77 --add-label needs-triage --add-label afk-review$' "$events"
! grep -Eq 'issue edit 77 .*--add-label (ready-for-agent|Sandcastle)' "$events"
grep -q '^gh pr merge 7 --merge$' "$events"

setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/followup"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_BLOCKERS=77
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[blocking-discovery]: blocking discovery should pause for review" >&2; cat "$out" >&2; exit 1
fi
grep -q 'discovered issue #77 blocks issue #42' "$out"
assert_pr_open blocking-discovery
assert_afk_review_present blocking-discovery

setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/followup"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_DEPENDENCY_QUERY_FAIL=1
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[uncertain-discovery]: uncertain discovery should pause for review" >&2; cat "$out" >&2; exit 1
fi
grep -q 'could not classify discovered work' "$out"
assert_pr_open uncertain-discovery
assert_afk_review_present uncertain-discovery

# --- Failed verification: merge lands but is not on the default branch -------
# Post-merge state reports MERGED with a merge commit that origin/main never
# received (fabricated oid), exercising AC5's "landed on default branch" check.
setup_repo
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_POST_MERGE='{"state":"MERGED","mergedAt":"2026-01-01T00:00:00Z","mergeCommit":{"oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}'
export AFK_TEST_ISSUE_STATE=CLOSED
if run_merge >"$out" 2>&1; then
  echo "FAIL[not-on-default]: unverified landing must exit nonzero" >&2
  cat "$out" >&2; exit 1
fi
grep -q 'verification failed' "$out" \
  || { echo "FAIL[not-on-default]: no failure message" >&2; cat "$out" >&2; exit 1; }
assert_afk_review_present not-on-default
assert_no_success_message not-on-default
assert_no_deletion not-on-default
assert_branch_present not-on-default
assert_worktree_present not-on-default
grep -q '^gh pr merge 7 --merge$' "$events" \
  || { echo "FAIL[not-on-default]: merge should have been attempted" >&2; exit 1; }

# --- Failed verification: merge lands but issue did not close ---------------
setup_repo
merge_oid="$(seed_merge)"
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_POST_MERGE="{\"state\":\"MERGED\",\"mergedAt\":\"2026-01-01T00:00:00Z\",\"mergeCommit\":{\"oid\":\"$merge_oid\"}}"
export AFK_TEST_ISSUE_STATE=OPEN
if run_merge >"$out" 2>&1; then
  echo "FAIL[failed-verify]: unverified closure must exit nonzero" >&2
  cat "$out" >&2; exit 1
fi
grep -q 'verification failed' "$out" \
  || { echo "FAIL[failed-verify]: no failure message" >&2; cat "$out" >&2; exit 1; }
assert_afk_review_present failed-verify
assert_no_success_message failed-verify
assert_no_deletion failed-verify
assert_branch_present failed-verify
assert_worktree_present failed-verify
grep -q '^gh pr merge 7 --merge$' "$events" \
  || { echo "FAIL[failed-verify]: merge should have been attempted" >&2; exit 1; }

# --- Success: fully evidenced Closes PR merges and cleans up ----------------
setup_repo
merge_oid="$(seed_merge)"
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_POST_MERGE="{\"state\":\"MERGED\",\"mergedAt\":\"2026-01-01T00:00:00Z\",\"mergeCommit\":{\"oid\":\"$merge_oid\"}}"
export AFK_TEST_ISSUE_STATE=CLOSED
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[success]: eligible merge must exit 0" >&2
  cat "$out" >&2; exit 1
fi
grep -q "^gh pr merge 7 --merge$" "$events" \
  || { echo "FAIL[success]: merge commit not requested" >&2; exit 1; }
grep -q '^AFK issue #42 merged: pull request #7 landed on main' "$out" \
  || { echo "FAIL[success]: no success message" >&2; cat "$out" >&2; exit 1; }
grep -q '^git push origin --delete afk/issue-42$' "$events" \
  || { echo "FAIL[success]: remote branch not deleted" >&2; exit 1; }
grep -q "^git worktree remove --force $worktree\$" "$events" \
  || { echo "FAIL[success]: worktree not removed" >&2; exit 1; }
if git -C "$repo" show-ref --verify --quiet refs/heads/afk/issue-42; then
  echo "FAIL[success]: local branch still present" >&2; exit 1
fi
if git -C "$repo" worktree list --porcelain | grep -q '^branch refs/heads/afk/issue-42$'; then
  echo "FAIL[success]: worktree still present" >&2; exit 1
fi
if git -C "$repo" ls-remote --exit-code origin afk/issue-42 >/dev/null 2>&1; then
  echo "FAIL[success]: remote branch still present" >&2; exit 1
fi

# --- Success with an incomplete cleanup: report must stay honest ------------
# The merge is verified, but the remote-branch deletion fails. The stage must
# still exit 0 (the merge landed) yet must NOT claim the branch was deleted; it
# must flag the leftover remote branch for manual cleanup.
setup_repo
merge_oid="$(seed_merge)"
reset_events
export AFK_TEST_PROTECTION=good
export AFK_TEST_BODY="$bodies/closes"
export AFK_TEST_MERGEABILITY="$merge_clean"
export AFK_TEST_POST_MERGE="{\"state\":\"MERGED\",\"mergedAt\":\"2026-01-01T00:00:00Z\",\"mergeCommit\":{\"oid\":\"$merge_oid\"}}"
export AFK_TEST_ISSUE_STATE=CLOSED
export AFK_TEST_FAIL_PUSH_DELETE=1
if ! run_merge >"$out" 2>&1; then
  echo "FAIL[cleanup-incomplete]: verified merge must still exit 0" >&2
  cat "$out" >&2; exit 1
fi
grep -q '^gh pr merge 7 --merge$' "$events" \
  || { echo "FAIL[cleanup-incomplete]: merge not requested" >&2; exit 1; }
grep -q 'manual cleanup needed for: remote branch origin/afk/issue-42' "$out" \
  || { echo "FAIL[cleanup-incomplete]: leftover not flagged" >&2; cat "$out" >&2; exit 1; }
if grep -q 'branch afk/issue-42 deleted' "$out"; then
  echo "FAIL[cleanup-incomplete]: falsely claimed branch deleted" >&2; cat "$out" >&2; exit 1
fi
git -C "$repo" ls-remote --exit-code origin afk/issue-42 >/dev/null 2>&1 \
  || { echo "FAIL[cleanup-incomplete]: remote branch should remain" >&2; exit 1; }
unset AFK_TEST_FAIL_PUSH_DELETE

printf 'AFK merge black-box scenarios passed\n'
