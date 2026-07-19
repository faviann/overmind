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
  "pr view 7 --json state,mergeable,mergeStateStatus")
    printf '%s\n' "$AFK_TEST_MERGEABILITY" ;;
  "pr merge 7 --merge")
    printf 'pr-merged\n' >>"$AFK_TEST_STATE" ;;
  "pr view 7 --json state,mergedAt,mergeCommit")
    printf '%s\n' "$AFK_TEST_POST_MERGE" ;;
  "issue view 42 --json state --jq .state")
    printf '%s\n' "${AFK_TEST_ISSUE_STATE:-CLOSED}" ;;
  "pr edit 7 --add-label afk-review")
    printf 'afk-review-added\n' >>"$AFK_TEST_STATE" ;;
  *) printf 'unexpected gh call: %s\n' "$*" >&2; exit 90 ;;
esac
EOF

chmod +x "$adapters/git" "$adapters/gh"

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
      "$command_under_test" 42 afk/issue-42 main 7
  )
}

reset_events() { : >"$events"; : >"$state"; }

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
  "failed-check|good|closes|$merge_blocked"
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
