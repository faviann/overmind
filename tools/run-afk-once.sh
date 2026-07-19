#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'AFK preflight failed: %s\n' "$*" >&2
  exit 1
}

for command in git gh codex flock jq setsid; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || fail "run this command from a Git repository"
cd "$repo_root"
workflow_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

git_dir="$(git rev-parse --git-common-dir)"
exec 9>"$git_dir/afk-tracer.lock"
flock -n 9 || fail "another AFK tracer owns this repository; stop it before launching another"

gh auth status >/dev/null 2>&1 || fail "GitHub authentication is unavailable; run 'gh auth login'"
codex login status >/dev/null 2>&1 || fail "Codex authentication is unavailable; run 'codex login'"

skills_root="${AFK_SKILLS_ROOT:-${HOME}/.agents/skills}"
for skill in work-on select-issue implement tdd code-review; do
  [[ -f "$skills_root/$skill/SKILL.md" ]] || \
    fail "required shared skill is unavailable: $skills_root/$skill/SKILL.md"
done
selector="$skills_root/work-on/scripts/select-issue-codex.sh"
[[ -x "$selector" ]] || fail "shared AFK selector is unavailable or not executable: $selector"

labels="$(gh label list --limit 1000 --json name --jq '.[].name')" || \
  fail "could not read GitHub labels"
for label in ready-for-agent Sandcastle afk-review; do
  grep -Fxq "$label" <<<"$labels" || fail "missing required GitHub label: $label"
done

repo_name="$(gh repo view --json nameWithOwner --jq .nameWithOwner)" || \
  fail "could not resolve the GitHub repository"
[[ -n "$repo_name" ]] || fail "GitHub returned no repository name"
default_branch="$(gh repo view --json defaultBranchRef --jq .defaultBranchRef.name)" || \
  fail "could not resolve the default branch"
[[ -n "$default_branch" ]] || fail "GitHub returned no default branch"
git ls-remote --exit-code origin "refs/heads/$default_branch" >/dev/null 2>&1 || \
  fail "cannot access origin's default branch: $default_branch"
git fetch --quiet origin "refs/heads/$default_branch:refs/remotes/origin/$default_branch" || \
  fail "could not synchronize origin/$default_branch; no issue was claimed"

[[ -x "$workflow_root/node_modules/.bin/tsx" && \
  -f "$workflow_root/node_modules/@ai-hero/sandcastle/package.json" ]] || \
  fail "checked-in AFK dependencies are not installed; run 'npm ci' in $workflow_root before launch"

poll_seconds="${AFK_POLL_SECONDS:-60}"
[[ "$poll_seconds" =~ ^[0-9]+([.][0-9]+)?$ ]] || \
  fail "AFK_POLL_SECONDS must be a non-negative number"

draining=0
stop_count=0
active_pid=""
sleep_pid=""
issue_active=0

force_active_issue() {
  local attempt
  [[ -n "$active_pid" ]] || return 0

  kill -TERM -- "-$active_pid" 2>/dev/null || true
  for attempt in {1..20}; do
    kill -0 -- "-$active_pid" 2>/dev/null || break
    sleep 0.05
  done
  if kill -0 -- "-$active_pid" 2>/dev/null; then
    kill -KILL -- "-$active_pid" 2>/dev/null || true
  fi
  wait "$active_pid" 2>/dev/null || true
  active_pid=""
}

stop_watcher() {
  stop_count=$((stop_count + 1))
  if [[ "$stop_count" -ge 2 ]]; then
    printf 'AFK watcher forcing termination\n' >&2
    force_active_issue
    exit 130
  fi

  draining=1
  if [[ "$issue_active" == 0 ]]; then
    [[ -z "$sleep_pid" ]] || kill -TERM "$sleep_pid" 2>/dev/null || true
    printf 'AFK watcher stopped while idle\n' >&2
    exit 0
  fi
  printf 'AFK watcher draining current issue; no more work will be claimed\n' >&2
}
trap stop_watcher INT TERM

sleep_until_poll() {
  sleep "$poll_seconds" &
  sleep_pid=$!
  wait "$sleep_pid"
  sleep_pid=""
}

wait_for_active_issue() {
  local status
  while [[ -n "$active_pid" ]]; do
    set +e
    wait "$active_pid"
    status=$?
    set -e
    if kill -0 "$active_pid" 2>/dev/null; then
      continue
    fi
    active_pid=""
    return "$status"
  done
}

sync_default_branch() {
  git fetch --quiet origin \
    "refs/heads/$default_branch:refs/remotes/origin/$default_branch"
}

observe_frontier() {
  authorized_queue="$(
    gh issue list --state open \
      --label ready-for-agent --label Sandcastle \
      --limit 1000 --json number,updatedAt --jq 'sort_by(.number)'
  )" || return 1

  if [[ "$(jq 'length' <<<"$authorized_queue")" -eq 0 ]]; then
    issue_frontier=""
  else
    issue_frontier="$(
      gh issue list --state all --limit 1000 \
        --json number,state,updatedAt --jq 'sort_by(.number)'
    )" || return 1
  fi
  default_oid="$(git rev-parse "origin/$default_branch")" || return 1
  frontier="$default_oid"$'\n'"$authorized_queue"$'\n'"$issue_frontier"
}

selected_issue_urls() {
  sed -nE \
    's|^Selected issue: (https://github.com/[^/]+/[^/]+/issues/[0-9]+)$|\1|p'
}

last_idle_frontier=""
printf 'AFK watcher started for %s; polling every %s seconds\n' "$repo_name" "$poll_seconds"

while :; do
  [[ "$draining" == 0 ]] || exit 0

  sync_default_branch || {
    printf 'AFK watcher could not synchronize origin/%s; waiting without claiming work\n' \
      "$default_branch" >&2
    sleep_until_poll
    continue
  }

  # This cheap live query is the authorization boundary. The selector/model is
  # never invoked while the two-label queue is empty.
  observe_frontier || fail "could not read the live authorized queue and dependency frontier"

  if [[ "$(jq 'length' <<<"$authorized_queue")" -eq 0 ]]; then
    last_idle_frontier=""
    sleep_until_poll
    continue
  fi

  # Open/closed issue changes can move native dependency frontiers without
  # changing an authorized issue's labels. Include them in the cheap frontier
  # observation so a newly closed blocker wakes selection, while an unchanged
  # blocked queue remains token-free.
  if [[ "$frontier" == "$last_idle_frontier" ]]; then
    sleep_until_poll
    continue
  fi

  selection="$($selector afk)" || fail "intelligent AFK selection failed"
  mapfile -t selected_urls < <(selected_issue_urls <<<"$selection")
  if [[ "${#selected_urls[@]}" -eq 0 ]]; then
    last_idle_frontier="$frontier"
    sleep_until_poll
    continue
  fi
  [[ "${#selected_urls[@]}" -eq 1 ]] || fail "selector returned more than one issue"

  selected_url="${selected_urls[0]}"
  selected_repo="$(sed -nE 's|^https://github.com/([^/]+/[^/]+)/issues/[0-9]+$|\1|p' <<<"$selected_url")"
  [[ "$selected_repo" == "$repo_name" ]] || \
    fail "selector returned an issue from $selected_repo instead of $repo_name"
  issue_number="${selected_url##*/}"

  # Selection may take long enough for authorization, dependencies, issue
  # metadata, or the default branch to change. Re-read every input after the
  # selector returns. Any change invalidates its reasoning and restarts the
  # selection cycle without consuming authorization.
  selected_frontier="$frontier"
  if ! sync_default_branch; then
    printf 'AFK watcher could not refresh origin/%s before claim; selection discarded\n' \
      "$default_branch" >&2
    last_idle_frontier=""
    sleep_until_poll
    continue
  fi
  observe_frontier || fail "could not revalidate the live queue before claim"
  if [[ "$frontier" != "$selected_frontier" ]]; then
    last_idle_frontier=""
    continue
  fi

  # Re-run the complete intelligent policy at the stable frontier. This is the
  # final staleness/blocker/umbrella/conflict decision for the claim, rather
  # than relying on the earlier potentially long-running recommendation.
  confirmation="$($selector afk)" || fail "intelligent AFK claim validation failed"
  mapfile -t confirmed_urls < <(selected_issue_urls <<<"$confirmation")
  if [[ "${#confirmed_urls[@]}" -ne 1 ]]; then
    # The earlier recommendation proves this frontier was not consistently
    # ineligible. Retry after a token-free sleep instead of caching a transient
    # empty/invalid confirmation forever.
    last_idle_frontier=""
    sleep_until_poll
    continue
  fi

  # The stable-frontier confirmation is authoritative. Two valid policy runs
  # may rank the same eligible pool differently; requiring identical URLs
  # would strand both issues without any GitHub state change to wake the loop.
  selected_url="${confirmed_urls[0]}"
  selected_repo="$(sed -nE 's|^https://github.com/([^/]+/[^/]+)/issues/[0-9]+$|\1|p' <<<"$selected_url")"
  [[ "$selected_repo" == "$repo_name" ]] || \
    fail "claim validation returned an issue from $selected_repo instead of $repo_name"
  issue_number="${selected_url##*/}"

  confirmed_frontier="$frontier"
  if ! sync_default_branch; then
    printf 'AFK watcher could not refresh origin/%s after claim validation; selection discarded\n' \
      "$default_branch" >&2
    last_idle_frontier=""
    sleep_until_poll
    continue
  fi
  observe_frontier || fail "could not perform final live validation before claim"
  if [[ "$frontier" != "$confirmed_frontier" ]]; then
    last_idle_frontier=""
    continue
  fi

  jq -e --argjson issue "$issue_number" 'any(.number == $issue)' \
    <<<"$authorized_queue" >/dev/null || \
    fail "selector returned issue #$issue_number outside the live authorized queue"

  branch="afk/issue-$issue_number"
  issue_active=1
  preclaim_state="$(gh issue view "$issue_number" --json state,labels)" || \
    fail "could not perform final claim check for issue #$issue_number"

  # A first stop may arrive after selection validation but before the claim.
  # Honor it before consuming authorization, even when it interrupted the
  # final GitHub read while the issue was considered active.
  if [[ "$draining" != 0 ]]; then
    issue_active=0
    printf 'AFK watcher drained before claim; no issue was claimed\n' >&2
    exit 0
  fi

  if [[ "$(jq -r '.state' <<<"$preclaim_state")" != OPEN ]] || \
     ! jq -e '(.labels | map(.name) | index("ready-for-agent")) != null' \
       <<<"$preclaim_state" >/dev/null || \
     ! jq -e '(.labels | map(.name) | index("Sandcastle")) != null' \
       <<<"$preclaim_state" >/dev/null; then
    printf 'AFK issue #%s changed before claim; no authorization was consumed\n' \
      "$issue_number" >&2
    issue_active=0
    last_idle_frontier=""
    continue
  fi

  # Removing Sandcastle is the single claim mutation. The exclusive watcher
  # lock plus the immediately preceding live validation prevents stale local
  # selections and duplicate attempts.
  gh api --method DELETE \
    "repos/$repo_name/issues/$issue_number/labels/Sandcastle" >/dev/null || \
    fail "could not claim issue #$issue_number by removing Sandcastle"

  claim_state="$(gh issue view "$issue_number" --json state,labels)" || \
    fail "could not verify claimed issue #$issue_number"
  if [[ "$(jq -r '.state' <<<"$claim_state")" != OPEN ]] || \
     ! jq -e '(.labels | map(.name) | index("ready-for-agent")) != null' \
       <<<"$claim_state" >/dev/null || \
     jq -e '(.labels | map(.name) | index("Sandcastle")) != null' \
       <<<"$claim_state" >/dev/null; then
    printf 'AFK issue #%s changed during claim; authorization remains consumed and work will not launch\n' \
      "$issue_number" >&2
    issue_active=0
    last_idle_frontier=""
    continue
  fi

  # The default may advance independently even after a valid claim. Refresh
  # once more so Sandcastle always creates the worktree from the latest
  # verified origin/default state.
  if ! sync_default_branch; then
    printf 'AFK issue #%s could not refresh origin/%s after claim; authorization remains consumed\n' \
      "$issue_number" "$default_branch" >&2
    issue_active=0
    last_idle_frontier=""
    continue
  fi

  # A separate process group gives the second stop signal a deterministic
  # force boundary across Sandcastle and its agent descendants.
  setsid "$workflow_root/tools/run-afk-issue.sh" \
    "$issue_number" "$branch" "$default_branch" &
  active_pid=$!
  set +e
  wait_for_active_issue
  issue_status=$?
  set -e
  if [[ "$issue_status" -ne 0 ]]; then
    printf 'AFK issue #%s reached a failed terminal outcome (status %s); authorization remains consumed\n' \
      "$issue_number" "$issue_status" >&2
  fi
  issue_active=0

  last_idle_frontier=""
  [[ "$draining" == 0 ]] || exit 0
done
