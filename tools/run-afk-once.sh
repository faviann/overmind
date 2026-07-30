#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'AFK preflight failed: %s\n' "$*" >&2
  exit 1
}

for command in git gh codex flock jq setsid sha256sum tee; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || fail "run this command from a Git repository"
cd "$repo_root"
workflow_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

git_dir="$(git rev-parse --git-common-dir)"
log_dir="${AFK_LOG_DIR:-$git_dir/afk-logs}"
mkdir -p -- "$log_dir" || fail "could not create AFK log directory: $log_dir"
log_dir="$(cd -- "$log_dir" && pwd)" || fail "could not resolve AFK log directory: $log_dir"
log_file="$log_dir/afk-$(date -u +%Y%m%dT%H%M%SZ)-$$.log"
: >"$log_file" || fail "could not create AFK log file: $log_file"
chmod 600 "$log_file" || fail "could not protect AFK log file: $log_file"

# Mirror both terminal streams into one private per-run log while preserving
# their original terminal destinations. The tee processes ignore watcher
# control signals and exit on EOF, leaving this process as the owner of drain,
# force-stop, and exit-status behavior.
exec 3>&1 4>&2
exec > >(trap '' INT TERM HUP; exec tee -a "$log_file" >&3) \
  2> >(trap '' INT TERM HUP; exec tee -a "$log_file" >&4)
exec 3>&- 4>&-
printf 'AFK watcher log: %s\n' "$log_file" >&2

exec 9>"$git_dir/afk-tracer.lock"
flock -n 9 || fail "another AFK tracer owns this repository; stop it before launching another"

gh auth status >/dev/null 2>&1 || fail "GitHub authentication is unavailable; run 'gh auth login'"
codex login status >/dev/null 2>&1 || fail "Codex authentication is unavailable; run 'codex login'"

[[ -n "${HOME:-}" ]] || fail "HOME is unavailable"
skills_root="$HOME/.agents/skills"
[[ -d "$skills_root" ]] || \
  fail "Codex global skills directory is unavailable: $skills_root"
required_skills=(work-on select-issue tdd code-review)
for skill in "${required_skills[@]}"; do
  [[ -r "$skills_root/$skill/SKILL.md" && -s "$skills_root/$skill/SKILL.md" ]] || \
    fail "required shared skill is unavailable, unreadable, or empty: $skills_root/$skill/SKILL.md"
done
work_on_skill="$skills_root/work-on/SKILL.md"
[[ -r "$work_on_skill" && -s "$work_on_skill" ]] || \
  fail "shared work-on skill is unavailable, unreadable, or empty: $work_on_skill"
read -r work_on_digest _ < <(sha256sum -- "$work_on_skill") || \
  fail "could not fingerprint shared work-on skill: $work_on_skill"
[[ "$work_on_digest" =~ ^[0-9a-f]{64}$ ]] || \
  fail "shared work-on skill returned an invalid fingerprint: $work_on_skill"
selector="$skills_root/work-on/scripts/select-issue-codex.sh"
[[ -x "$selector" ]] || fail "shared AFK selector is unavailable or not executable: $selector"

labels="$(gh label list --limit 1000 --json name --jq '.[].name')" || \
  fail "could not read GitHub labels"
for label in ready-for-agent Sandcastle afk-review needs-triage; do
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

read -ra required_checks <<<"${AFK_REQUIRED_CHECKS:-test test-compose reference-compose}"
[[ "${#required_checks[@]}" -gt 0 ]] || \
  fail "no designated CI checks configured"

protection="$(gh api "repos/$repo_name/branches/$default_branch/protection" 2>/dev/null)" || \
  fail "default branch $default_branch is not protected"
[[ "$(jq -r '.required_pull_request_reviews != null' <<<"$protection")" == true ]] || \
  fail "default branch $default_branch does not require pull requests"
[[ "$(jq -r '.required_status_checks.strict == true' <<<"$protection")" == true ]] || \
  fail "default branch $default_branch does not require up-to-date branches"

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
  [[ "$present" == 1 ]] || \
    fail "default branch $default_branch does not require designated check: $required_check"
done

[[ -x "$workflow_root/node_modules/.bin/tsx" && \
  -f "$workflow_root/node_modules/@ai-hero/sandcastle/package.json" ]] || \
  fail "checked-in AFK dependencies are not installed; run 'npm ci' in $workflow_root before launch"

poll_seconds="${AFK_POLL_SECONDS:-60}"
[[ "$poll_seconds" =~ ^[0-9]+([.][0-9]+)?$ ]] || \
  fail "AFK_POLL_SECONDS must be a non-negative number"

# A selection pass that chose nothing is not permanent: the same unchanged
# authorized queue is reconsidered once this bounded cooldown elapses. It is a
# whole number of seconds so a repeatedly empty selection stays token-conscious
# instead of re-running the model on every poll.
idle_retry_seconds=900

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

exit_if_draining_before_claim() {
  [[ "$draining" != 0 ]] || return 0

  issue_active=0
  printf 'AFK watcher drained before claim; no issue was claimed\n' >&2
  exit 0
}

idle_selected_frontier() {
  issue_active=0
  last_idle_frontier="$frontier"
  last_idle_at="$(date +%s)"
  sleep_until_poll
}

wait_for_active_issue() {
  local status
  while [[ -n "$active_pid" ]]; do
    if wait "$active_pid"; then
      status=0
    else
      status=$?
    fi
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

# Selector output is free-form model prose. Surface at most one bounded,
# printable line of it to the operator; never echo the whole transcript.
selection_reason() {
  grep -v '^[[:space:]]*$' | tail -n1 | tr -cd '[:print:]' | cut -c1-200
}

last_idle_frontier=""
last_idle_at=0
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

  # An unchanged frontier stays token-free only until the idle cooldown
  # elapses; after that the same authorized queue is reconsidered once, so one
  # transient empty selection cannot suppress authorized work indefinitely.
  if [[ "$frontier" == "$last_idle_frontier" ]]; then
    idle_elapsed=$(( $(date +%s) - last_idle_at ))
    # The -ge 0 term keeps a backwards system-clock step from producing a
    # negative elapsed value, which would otherwise read as "still cooling
    # down" on every poll and suppress authorized work indefinitely.
    if [[ "$idle_elapsed" -ge 0 && "$idle_elapsed" -lt "$idle_retry_seconds" ]]; then
      sleep_until_poll
      continue
    fi
  fi

  selection="$($selector afk)" || fail "intelligent AFK selection failed"
  mapfile -t selected_urls < <(selected_issue_urls <<<"$selection")
  if [[ "${#selected_urls[@]}" -eq 0 ]]; then
    last_idle_frontier="$frontier"
    last_idle_at="$(date +%s)"
    idle_reason="$(selection_reason <<<"$selection" || true)"
    printf 'AFK watcher found authorized work but no issue was selected%s; unchanged authorized work is reconsidered no sooner than %s seconds from now\n' \
      "${idle_reason:+ (selector: $idle_reason)}" "$idle_retry_seconds" >&2
    sleep_until_poll
    continue
  fi
  [[ "${#selected_urls[@]}" -eq 1 ]] || fail "selector returned more than one issue"

  selected_url="${selected_urls[0]}"
  selected_repo="$(sed -nE 's|^https://github.com/([^/]+/[^/]+)/issues/[0-9]+$|\1|p' <<<"$selected_url")"
  [[ "$selected_repo" == "$repo_name" ]] || \
    fail "selector returned an issue from $selected_repo instead of $repo_name"
  issue_number="${selected_url##*/}"

  # The intelligent selector is an external process and can run long enough
  # for the shared workflow to be removed or replaced. Revalidate the exact
  # resolved skill before consuming the one-shot Sandcastle authorization.
  for skill in "${required_skills[@]}"; do
    [[ -r "$skills_root/$skill/SKILL.md" && -s "$skills_root/$skill/SKILL.md" ]] || \
      fail "required shared skill changed or became unavailable before claim: $skills_root/$skill/SKILL.md"
  done
  read -r current_work_on_digest _ < <(sha256sum -- "$work_on_skill") || \
    fail "could not re-fingerprint shared work-on skill before claim: $work_on_skill"
  [[ "$current_work_on_digest" == "$work_on_digest" ]] || \
    fail "shared work-on skill changed before claim: $work_on_skill"

  # Selection can take long enough for authorization, dependencies, issue
  # metadata, or the default branch to change. Re-read every input once after
  # the selector returns; any change invalidates its reasoning and restarts the
  # cycle without consuming authorization. A stable frontier means the selector
  # decided on inputs that still hold, so its recommendation stands as the
  # staleness/blocker/umbrella/conflict decision for this claim.
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
  exit_if_draining_before_claim

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

  # Native issue dependencies are the mechanical execution gate. The selector
  # is advisory, so re-read the selected issue's open blockers immediately
  # before the claim mutation and preserve authorization on either a blocker or
  # an unreadable dependency response.
  if ! blocker_numbers="$(
    gh api "repos/$repo_name/issues/$issue_number/dependencies/blocked_by" \
      --paginate --jq '.[] | select(.state == "open") | .number'
  )"; then
    exit_if_draining_before_claim
    printf 'AFK issue #%s remains authorized because its open blockers could not be read\n' \
      "$issue_number" >&2
    idle_selected_frontier
    continue
  fi
  exit_if_draining_before_claim
  if [[ -n "$blocker_numbers" ]]; then
    blocker_refs="$(sed 's/^/#/' <<<"$blocker_numbers" | paste -sd, -)"
    blocker_count="$(grep -c . <<<"$blocker_numbers")"
    if [[ "$blocker_count" -eq 1 ]]; then
      blocker_kind="issue"
    else
      blocker_kind="issues"
    fi
    printf 'AFK issue #%s remains authorized but blocked by open %s %s\n' \
      "$issue_number" "$blocker_kind" "$blocker_refs" >&2
    idle_selected_frontier
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
    "$issue_number" "$branch" "$default_branch" "$work_on_digest" &
  active_pid=$!
  if wait_for_active_issue; then
    issue_status=0
  else
    issue_status=$?
  fi
  if [[ "$issue_status" -ne 0 ]]; then
    printf 'AFK issue #%s reached a failed terminal outcome (status %s); authorization remains consumed\n' \
      "$issue_number" "$issue_status" >&2
  fi
  issue_active=0

  last_idle_frontier=""
  [[ "$draining" == 0 ]] || exit 0
done
