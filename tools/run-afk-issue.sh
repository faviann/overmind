#!/usr/bin/env bash
set -euo pipefail

# Run one already-selected, already-claimed AFK issue to a terminal outcome.
# The watcher starts this script in its own process group so a second stop can
# force the whole Sandcastle/agent tree without weakening graceful drain.

issue_number="${1:?usage: run-afk-issue.sh <issue-number> <branch> <default-branch>}"
branch="${2:?usage: run-afk-issue.sh <issue-number> <branch> <default-branch>}"
default_branch="${3:?usage: run-afk-issue.sh <issue-number> <branch> <default-branch>}"

workflow_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

"$workflow_root/node_modules/.bin/tsx" "$workflow_root/.sandcastle/main.mts" \
  "$issue_number" "$branch" "$default_branch"

mapfile -t pull_requests < <(
  gh pr list --head "$branch" --state open --json number --jq '.[].number'
)
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
