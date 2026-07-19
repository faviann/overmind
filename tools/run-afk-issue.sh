#!/usr/bin/env bash
set -euo pipefail

# Run one already-selected, already-claimed AFK issue to a terminal outcome.
# The watcher starts this script in its own process group so a second stop can
# force the whole Sandcastle/agent tree without weakening graceful drain.

issue_number="${1:?usage: run-afk-issue.sh <issue-number> <branch> <default-branch>}"
branch="${2:?usage: run-afk-issue.sh <issue-number> <branch> <default-branch>}"
default_branch="${3:?usage: run-afk-issue.sh <issue-number> <branch> <default-branch>}"

workflow_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

set +e
"$workflow_root/node_modules/.bin/tsx" "$workflow_root/.sandcastle/main.mts" \
  "$issue_number" "$branch" "$default_branch"
agent_status=$?
set -e

mapfile -t pull_requests < <(
  gh pr list --head "$branch" --state open --json number --jq '.[].number'
)
if [[ "$agent_status" -ne 0 ]]; then
  if [[ "${#pull_requests[@]}" -eq 1 ]]; then
    pr_number="${pull_requests[0]}"
    if ! gh pr edit "$pr_number" --add-label afk-review >/dev/null; then
      printf 'AFK issue #%s agent stopped (status %s): could not add afk-review to pull request #%s; branch/worktree artifacts preserved\n' \
        "$issue_number" "$agent_status" "$pr_number" >&2
      exit "$agent_status"
    fi
    if ! pr_body="$(gh pr view "$pr_number" --json body --jq .body)"; then
      printf 'AFK issue #%s agent stopped (status %s): could not read pull request #%s discoveries; branch/worktree artifacts preserved\n' \
        "$issue_number" "$agent_status" "$pr_number" >&2
      exit "$agent_status"
    fi
    set +e
    discovery_result="$("$workflow_root/tools/afk-followups.sh" \
      "$issue_number" <<<"$pr_body")"
    discovery_status=$?
    set -e
    if [[ "$discovery_status" -ne 0 && "$discovery_status" -ne 2 ]]; then
      printf 'AFK issue #%s agent stopped (status %s): discovery processing failed; branch/worktree artifacts preserved\n' \
        "$issue_number" "$agent_status" >&2
      exit "$agent_status"
    fi
    if [[ "$discovery_status" == 2 ]]; then
      printf 'AFK issue #%s partial result: %s\n' "$issue_number" "$discovery_result" >&2
    fi
    printf 'AFK issue #%s agent stopped (status %s): pull request #%s and available worktree artifacts preserved for review\n' \
      "$issue_number" "$agent_status" "$pr_number" >&2
  else
    printf 'AFK issue #%s agent stopped (status %s): available branch/worktree artifacts preserved; found %s open pull requests\n' \
      "$issue_number" "$agent_status" "${#pull_requests[@]}" >&2
  fi
  exit "$agent_status"
fi

[[ "${#pull_requests[@]}" -eq 1 ]] || {
  printf 'AFK issue #%s failed: expected exactly one open pull request for %s; authorization remains consumed\n' \
    "$issue_number" "$branch" >&2
  exit 1
}

pr_number="${pull_requests[0]}"
gh pr edit "$pr_number" --add-label afk-review >/dev/null || {
  printf 'AFK issue #%s failed: could not add afk-review to pull request #%s\n' \
    "$issue_number" "$pr_number" >&2
  exit 1
}

exec "$workflow_root/tools/afk-merge.sh" \
  "$issue_number" "$branch" "$default_branch" "$pr_number"
