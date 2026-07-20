#!/usr/bin/env bash
set -euo pipefail

# Guarded unattended merge stage for the one-shot AFK tracer.
#
# Given an already-created, afk-review-tagged pull request that a fully
# evidenced work-on run produced, this stage decides whether the work may be
# merged into the protected default branch without a human. It refuses (and
# preserves the pull request for review) for every partial, uncertain,
# conflicting, or insufficiently protected case, and only after verifying the
# merge landed and the issue closed does it delete the temporary artifacts.
#
# usage: afk-merge.sh <issue-number> <branch> <default-branch> <pr-number>

issue_number="${1:?usage: afk-merge.sh <issue-number> <branch> <default-branch> <pr-number>}"
branch="${2:?usage: afk-merge.sh <issue-number> <branch> <default-branch> <pr-number>}"
default_branch="${3:?usage: afk-merge.sh <issue-number> <branch> <default-branch> <pr-number>}"
pr_number="${4:?usage: afk-merge.sh <issue-number> <branch> <default-branch> <pr-number>}"

read -ra required_checks <<<"${AFK_REQUIRED_CHECKS:-test}"
readonly ci_timeout_seconds=3600
readonly ci_poll_seconds=30

ensure_afk_review() {
  gh pr edit "$pr_number" --add-label afk-review >/dev/null 2>&1
}

# Non-merge outcome that mirrors the tracer's "awaits review" end state: keep
# the pull request open, guarantee afk-review, explain the reason, never claim a
# merge. The tracer run as a whole still succeeded, so exit 0.
refuse() {
  if ! ensure_afk_review; then
    printf 'AFK pause failed: could not ensure afk-review on pull request #%s; artifacts preserved\n' \
      "$pr_number" >&2
    exit 1
  fi
  printf 'merge refused: %s\n' "$1" >&2
  printf 'AFK issue #%s completed: pull request #%s awaits review\n' \
    "$issue_number" "$pr_number"
  exit 0
}

# A merge was attempted but landing or closure could not be verified. Preserve
# every artifact, keep afk-review, and fail loudly without claiming success.
abort_unverified() {
  ensure_afk_review || \
    printf 'AFK merge verification warning: could not ensure afk-review on pull request #%s\n' \
      "$pr_number" >&2
  printf 'AFK merge verification failed: %s; artifacts preserved for review\n' \
    "$1" >&2
  exit 1
}

repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner)" \
  || refuse "could not resolve the GitHub repository"
[[ -n "$repo" ]] || refuse "GitHub returned no repository name"

# --- Merge preflight: branch protection (AC1) -------------------------------
protection="$(gh api "repos/$repo/branches/$default_branch/protection" 2>/dev/null)" \
  || refuse "default branch $default_branch is not protected"

[[ "$(jq -r '.required_pull_request_reviews != null' <<<"$protection")" == true ]] \
  || refuse "default branch $default_branch does not require pull requests"
[[ "$(jq -r '.required_status_checks.strict == true' <<<"$protection")" == true ]] \
  || refuse "default branch $default_branch does not require up-to-date branches"

# Fail closed: a set-but-empty AFK_REQUIRED_CHECKS (e.g. " ") would otherwise
# leave the designated-check loop with nothing to enforce, silently defeating
# AC1's "every designated CI check" requirement.
[[ "${#required_checks[@]}" -gt 0 ]] \
  || refuse "no designated CI checks configured; refusing unattended merge"

mapfile -t protected_checks < <(
  jq -r '((.required_status_checks.checks // []) | map(.context))
         + (.required_status_checks.contexts // [])
         | .[]' <<<"$protection"
)
for required_check in "${required_checks[@]}"; do
  present=0
  for protected_check in "${protected_checks[@]}"; do
    [[ "$protected_check" == "$required_check" ]] && { present=1; break; }
  done
  [[ "$present" == 1 ]] \
    || refuse "default branch $default_branch does not require designated check: $required_check"
done

# --- Merge eligibility: durable evidence (AC2/AC3) --------------------------
pr_body="$(gh pr view "$pr_number" --json body --jq .body)" \
  || refuse "could not read pull request #$pr_number body"

set +e
discovery_result="$("$(dirname "${BASH_SOURCE[0]}")/afk-followups.sh" \
  "$issue_number" <<<"$pr_body")"
discovery_status=$?
set -e
case "$discovery_status" in
  0) ;;
  2) refuse "$discovery_result" ;;
  *)
    ensure_afk_review || true
    printf 'AFK discovery processing failed for issue #%s; artifacts preserved\n' \
      "$issue_number" >&2
    exit 1
    ;;
esac

grep -Eq "^Closes #${issue_number}([^0-9].*)?$" <<<"$pr_body" \
  || refuse "missing evidence: pull request does not Close #$issue_number"
if grep -Eq "^Progresses #${issue_number}([^0-9].*)?$" <<<"$pr_body"; then
  refuse "issue #$issue_number appears under Progresses, not Closes"
fi

outcome="$(
  awk -F'|' '/Final workflow outcome/ {
    value = $3
    gsub(/^[ \t]+|[ \t]+$/, "", value)
    print value
  }' <<<"$pr_body" | tail -n1
)"
[[ -n "$outcome" ]] \
  || refuse "missing evidence: no workflow telemetry outcome row"
[[ "$outcome" == "Closes" ]] \
  || refuse "workflow telemetry outcome is not Closes: $outcome"

# --- Closure gate table: every criterion must be tested ---------------------
# The gate table is the only per-criterion evidence in the pull request. Without
# it, `Closes` is a bare assertion. Read the Status column of every row under the
# closure-gate header and refuse anything short of `tested` — including
# `instructional`, which is never eligible for an unattended merge.
gate_statuses="$(
  awk -F'|' '
    /^\|[[:space:]]*Acceptance criterion[[:space:]]*\|/ { in_table = 1; next }
    in_table && $0 !~ /^\|/ { in_table = 0 }
    in_table && /^\|[[:space:]]*-/ { next }
    in_table && /^\|/ {
      value = $(NF - 1)
      gsub(/`/, "", value)
      gsub(/^[ \t]+|[ \t]+$/, "", value)
      if (value != "") print tolower(value)
    }
  ' <<<"$pr_body"
)"
[[ -n "$gate_statuses" ]] \
  || refuse "missing evidence: no closure gate table with acceptance-criterion rows"
while IFS= read -r gate_status; do
  [[ "$gate_status" == "tested" ]] \
    || refuse "closure gate criterion is not tested: $gate_status"
done <<<"$gate_statuses"

# --- Required CI: bounded wait and one mechanical retry --------------------
ci_started="$(date +%s)"
ci_retried=0
while :; do
  set +e
  checks="$(gh pr checks "$pr_number" --required --json name,state,bucket,link)"
  checks_status=$?
  set -e
  case "$checks_status" in
    0|1|8) ;;
    *) refuse "could not read required CI checks for pull request #$pr_number" ;;
  esac
  jq -e 'type == "array"' <<<"$checks" >/dev/null \
    || refuse "GitHub returned invalid required CI data"

  for required_check in "${required_checks[@]}"; do
    jq -e --arg name "$required_check" 'any(.name == $name)' <<<"$checks" >/dev/null \
      || refuse "designated required CI check is missing from pull request: $required_check"
  done

  if jq -e 'all(.bucket == "pass")' <<<"$checks" >/dev/null; then
    break
  fi
  ci_now="$(date +%s)"
  if (( ci_now - ci_started >= ci_timeout_seconds )); then
    refuse "required CI did not complete within $ci_timeout_seconds seconds"
  fi
  ci_remaining=$((ci_timeout_seconds - (ci_now - ci_started)))
  ci_sleep_seconds="$ci_poll_seconds"
  if (( ci_remaining < ci_sleep_seconds )); then
    ci_sleep_seconds="$ci_remaining"
  fi

  failed="$(jq '[.[] | select(.bucket == "fail")]' <<<"$checks")"
  if [[ "$(jq 'length' <<<"$failed")" -gt 0 ]]; then
    if [[ "$ci_retried" == 1 ]]; then
      refuse "required CI failed again after one mechanical retry"
    fi
    mapfile -t failed_runs < <(
      jq -r '.[].link' <<<"$failed" \
        | sed -nE 's|^https://github.com/[^/]+/[^/]+/actions/runs/([0-9]+)(/.*)?$|\1|p' \
        | sort -u
    )
    [[ "${#failed_runs[@]}" -gt 0 ]] \
      || refuse "failed required CI could not be mapped to a workflow run for retry"
    for run_id in "${failed_runs[@]}"; do
      gh run rerun "$run_id" --failed >/dev/null \
        || refuse "could not mechanically retry failed required CI run $run_id"
    done
    ci_retried=1
    sleep "$ci_sleep_seconds"
    continue
  fi

  if jq -e 'any(.bucket == "cancel" or .bucket == "skipping")' \
    <<<"$checks" >/dev/null; then
    refuse "required CI ended without passing"
  fi
  sleep "$ci_sleep_seconds"
done

# --- Merge eligibility: GitHub mergeability (AC2/AC3) -----------------------
# Read this only after required CI settles: GitHub reports an otherwise clean
# pull request as BLOCKED/UNSTABLE while those checks are pending or failing.
mergeability="$(gh pr view "$pr_number" --json state,mergeable,mergeStateStatus)" \
  || refuse "could not read pull request #$pr_number mergeability"
pr_state="$(jq -r '.state' <<<"$mergeability")"
pr_mergeable="$(jq -r '.mergeable' <<<"$mergeability")"
pr_merge_status="$(jq -r '.mergeStateStatus' <<<"$mergeability")"

[[ "$pr_state" == OPEN ]] \
  || refuse "pull request #$pr_number is not open (state: $pr_state)"
case "$pr_merge_status" in
  CLEAN) ;;
  DIRTY|CONFLICTING) refuse "pull request has a merge conflict (mergeStateStatus: $pr_merge_status)" ;;
  BEHIND) refuse "pull request branch is not up to date (mergeStateStatus: BEHIND)" ;;
  BLOCKED|UNSTABLE) refuse "required checks are not passing (mergeStateStatus: $pr_merge_status)" ;;
  *) refuse "pull request merge state is not clean (mergeStateStatus: $pr_merge_status)" ;;
esac
[[ "$pr_mergeable" == MERGEABLE ]] \
  || refuse "pull request is not mergeable (mergeable: $pr_mergeable)"

# --- Merge with a merge commit (AC4) ----------------------------------------
gh pr merge "$pr_number" --merge >/dev/null \
  || abort_unverified "gh pr merge did not complete"

# --- Verify the merge landed and the issue closed (AC5) ---------------------
git fetch --quiet origin \
  "refs/heads/$default_branch:refs/remotes/origin/$default_branch" \
  || abort_unverified "could not synchronize origin/$default_branch"

merged="$(gh pr view "$pr_number" --json state,mergedAt,mergeCommit)" \
  || abort_unverified "could not read merged pull request state"
merged_state="$(jq -r '.state' <<<"$merged")"
merge_commit="$(jq -r '.mergeCommit.oid // empty' <<<"$merged")"
[[ "$merged_state" == MERGED ]] \
  || abort_unverified "pull request did not reach MERGED (state: $merged_state)"
[[ -n "$merge_commit" ]] \
  || abort_unverified "merged pull request reports no merge commit"
git merge-base --is-ancestor "$merge_commit" "origin/$default_branch" \
  || abort_unverified "merge commit $merge_commit is not on origin/$default_branch"

issue_state="$(gh issue view "$issue_number" --json state --jq .state)" \
  || abort_unverified "could not read issue #$issue_number state"
[[ "$issue_state" == CLOSED ]] \
  || abort_unverified "issue #$issue_number is not CLOSED (state: $issue_state)"

# --- Cleanup only after verified success (AC6) ------------------------------
# The merge is verified landed; deletions are best-effort. Never abort here
# (that would preserve nothing and misrepresent a real merge), but the final
# report must honestly name anything that could not be removed.
leftover=()
worktree_path="$(
  git worktree list --porcelain | awk -v ref="refs/heads/$branch" '
    $1 == "worktree" { path = $2 }
    $1 == "branch" && $2 == ref { print path }
  '
)"
if [[ -n "$worktree_path" ]]; then
  git worktree remove --force "$worktree_path" >/dev/null 2>&1 \
    || { printf 'warning: could not remove worktree %s\n' "$worktree_path" >&2
         leftover+=("worktree $worktree_path"); }
fi
git branch -D "$branch" >/dev/null 2>&1 \
  || { printf 'warning: could not delete local branch %s\n' "$branch" >&2
       leftover+=("local branch $branch"); }
git push origin --delete "$branch" >/dev/null 2>&1 \
  || { printf 'warning: could not delete remote branch %s\n' "$branch" >&2
       leftover+=("remote branch origin/$branch"); }

if [[ "${#leftover[@]}" -eq 0 ]]; then
  printf 'AFK issue #%s merged: pull request #%s landed on %s (%s); branch %s deleted\n' \
    "$issue_number" "$pr_number" "$default_branch" "$merge_commit" "$branch"
else
  leftover_list="${leftover[0]}"
  for item in "${leftover[@]:1}"; do
    leftover_list+="; $item"
  done
  printf 'AFK issue #%s merged: pull request #%s landed on %s (%s); manual cleanup needed for: %s\n' \
    "$issue_number" "$pr_number" "$default_branch" "$merge_commit" "$leftover_list"
fi
