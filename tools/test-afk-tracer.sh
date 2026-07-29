#!/usr/bin/env bash
set -euo pipefail

readonly command_under_test="$(git rev-parse --show-toplevel)/tools/run-afk-once.sh"
readonly workflow_root="$(git rev-parse --show-toplevel)"
readonly real_git="$(command -v git)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

repo="$fixture/repo"
remote="$fixture/remote.git"
adapters="$fixture/adapters"
test_home="$fixture/home"
skills="$test_home/.agents/skills"
events="$fixture/events"
state="$fixture/state"
pr_body="$fixture/pr-body.md"
timer_events="$fixture/timer-events"
timer_recorder="$fixture/timer-recorder.cjs"
agent_prompt="$fixture/agent-prompt.txt"
mkdir -p "$repo" "$adapters" "$skills/work-on/scripts" \
  "$skills/tdd" "$skills/code-review" "$skills/select-issue"
touch "$events" "$state" "$timer_events" "$agent_prompt"
for skill in work-on tdd code-review select-issue; do
  printf 'scripted %s skill\n' "$skill" >"$skills/$skill/SKILL.md"
done
cat >"$skills/work-on/SKILL.md" <<'EOF'
---
name: work-on
description: Scripted AFK work-on fixture.
---

AFK_TEST_WORK_ON_INSTRUCTION
EOF

git init --bare --quiet "$remote"
git -C "$repo" init --quiet --initial-branch=main
git -C "$repo" config user.email afk-test@example.invalid
git -C "$repo" config user.name "AFK test"
git -C "$repo" commit --quiet --allow-empty -m base
git -C "$repo" remote add origin "$remote"
git -C "$repo" push --quiet -u origin main

cat >"$timer_recorder" <<'EOF'
const fs = require("node:fs");

const originalSetTimeout = globalThis.setTimeout;
globalThis.setTimeout = function recordScheduledDelay(callback, delay, ...args) {
  const milliseconds = Number(delay);
  if (Number.isFinite(milliseconds)) {
    fs.appendFileSync(process.env.AFK_TEST_TIMER_EVENTS, `${milliseconds}\n`);
  }
  return originalSetTimeout(callback, delay, ...args);
};
EOF

cat >"$adapters/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == fetch && "${AFK_TEST_FAIL_FETCH:-0}" == 1 ]]; then
  printf 'git-fetch-failed %s\n' "$*" >>"$AFK_TEST_EVENTS"
  exit 1
fi
exec "$AFK_TEST_REAL_GIT" "$@"
EOF

cat >"$skills/work-on/scripts/select-issue-codex.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'selector %s\n' "$*" >>"$AFK_TEST_EVENTS"
[[ "${1:-}" == afk ]]
if ! grep -qx claimed "$AFK_TEST_STATE"; then
  if [[ "${AFK_TEST_REMOVE_WORK_ON_AFTER_SELECTION:-0}" == 1 ]]; then
    rm "$(dirname "$0")/../SKILL.md"
  elif [[ "${AFK_TEST_REPLACE_WORK_ON_AFTER_SELECTION:-0}" == 1 ]]; then
    printf 'replacement work-on instructions\n' >"$(dirname "$0")/../SKILL.md"
  fi
  if [[ "${AFK_TEST_REMOVE_TDD_AFTER_SELECTION:-0}" == 1 ]]; then
    rm "$(dirname "$0")/../../tdd/SKILL.md"
  fi
  printf 'Selected issue: https://github.com/acme/widget/issues/42\n'
fi
EOF

cat >"$adapters/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-} ${2:-}" == "login status" ]]; then
  printf 'codex-auth\n' >>"$AFK_TEST_EVENTS"
  [[ "${AFK_TEST_FAIL_CODEX_AUTH:-0}" != 1 ]]
  exit 0
fi

prompt="$(cat)"
branch="$(git branch --show-current)"
[[ -f .git ]]
printf '%s' "$prompt" >"$AFK_TEST_AGENT_PROMPT"
printf 'codex-agent cwd=%s branch=%s args=%s\n' \
  "$PWD" "$branch" "$*" >>"$AFK_TEST_EVENTS"

printf 'completed by scripted work-on boundary\n' >afk-result.txt
git add afk-result.txt
git commit --quiet -m 'Complete scripted AFK issue'
cat >"$AFK_TEST_PR_BODY" <<'BODY'
## Issues

Closes #42

## Closure gate

| Acceptance criterion | Production path | Exact artifact/mode/seam | Evidence | Status |
|---|---|---|---|---|
| Scripted AFK workflow completes issue #42 | `run-afk-once.sh` | Public one-shot tracer fixture | Scenario output | tested |

## Workflow telemetry

| Field | Observed value |
|---|---|
| Final workflow outcome | Closes |
BODY
gh pr create --head "$branch" --body-file "$AFK_TEST_PR_BODY" >/dev/null
printf '%s\n' '{"type":"item.started","item":{"type":"command_execution","command":"scripted-tracer-command --secret-argument zumbleflux"}}'
printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"scripted commentary line one\nscripted commentary line two\nscripted \u001b[31mcoloured\u001b[0m commentary line three\n"}}'
# Hold the turn open after the commentary so the scenario can observe that
# commentary while this agent is demonstrably still running. `sleep` on PATH is
# the adapter that kills its parent, so poll with the real binary.
release_wait=0
while [[ ! -e "$AFK_TEST_RELEASE" && "$release_wait" -lt 500 ]]; do
  /usr/bin/sleep 0.02
  release_wait=$((release_wait + 1))
done
printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"<promise>COMPLETE</promise>"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}'
EOF

cat >"$adapters/sleep" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'sleep %s\n' "$*" >>"$AFK_TEST_EVENTS"
kill -TERM "$PPID"
EOF

cat >"$adapters/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh %s\n' "$*" >>"$AFK_TEST_EVENTS"
if [[ "${1:-} ${2:-}" == "pr create" ]]; then
  [[ "$*" == "pr create --head afk/issue-42 --body-file $AFK_TEST_PR_BODY" ]]
  grep -q '^Closes #42$' "$AFK_TEST_PR_BODY"
  grep -q '^## Closure gate$' "$AFK_TEST_PR_BODY"
  grep -Fq '| Scripted AFK workflow completes issue #42 | `run-afk-once.sh` | Public one-shot tracer fixture | Scenario output | tested |' "$AFK_TEST_PR_BODY"
  grep -q '^## Workflow telemetry$' "$AFK_TEST_PR_BODY"
  grep -Fq '| Final workflow outcome | Closes |' "$AFK_TEST_PR_BODY"
  printf 'pr-created\n' >>"$AFK_TEST_STATE"
  printf 'https://github.com/acme/widget/pull/7\n'
  exit 0
fi
case "$*" in
  "auth status") [[ "${AFK_TEST_FAIL_GH_AUTH:-0}" != 1 ]] ;;
  "label list --limit 1000 --json name --jq .[].name")
    printf '%s\n' ready-for-agent Sandcastle
    if [[ "${AFK_TEST_INCLUDE_REVIEW_LABEL:-0}" == 1 ]]; then
      printf '%s\n' afk-review needs-triage
    fi
    ;;
  "repo view --json nameWithOwner --jq .nameWithOwner") printf 'acme/widget\n' ;;
  "repo view --json defaultBranchRef --jq .defaultBranchRef.name") printf 'main\n' ;;
  "issue list --state open --label ready-for-agent --label Sandcastle --limit 1000 --json number,updatedAt --jq sort_by(.number)")
    if grep -qx claimed "$AFK_TEST_STATE"; then
      printf '[]\n'
    else
      printf '[{"number":42,"updatedAt":"2026-01-01T00:00:00Z"}]\n'
    fi
    ;;
  "issue list --state all --limit 1000 --json number,state,updatedAt --jq sort_by(.number)")
    printf '[{"number":42,"state":"OPEN","updatedAt":"2026-01-01T00:00:00Z"}]\n' ;;
  "api --method DELETE repos/acme/widget/issues/42/labels/Sandcastle") printf 'claimed\n' >>"$AFK_TEST_STATE" ;;
  "issue view 42 --json state,labels")
    if grep -qx claimed "$AFK_TEST_STATE"; then
      printf '{"state":"OPEN","labels":[{"name":"ready-for-agent"}]}\n'
    else
      printf '{"state":"OPEN","labels":[{"name":"ready-for-agent"},{"name":"Sandcastle"}]}\n'
    fi ;;
  "api repos/acme/widget/branches/main/protection")
    protection_call="$(grep -c '^gh api repos/acme/widget/branches/main/protection$' "$AFK_TEST_EVENTS")"
    if [[ "$protection_call" == 2 ]]; then
      printf '%s\n' '{"required_pull_request_reviews":null,"required_status_checks":{"strict":true,"checks":[{"context":"test"},{"context":"test-compose"},{"context":"reference-compose"}]}}'
    else
      printf '%s\n' '{"required_pull_request_reviews":{"required_approving_review_count":0},"required_status_checks":{"strict":true,"checks":[{"context":"test"},{"context":"test-compose"},{"context":"reference-compose"}]}}'
    fi
    ;;
  "pr list --head afk/issue-42 --state open --json number --jq .[].number")
    grep -qx pr-created "$AFK_TEST_STATE"
    printf '7\n'
    ;;
  "pr edit 7 --add-label afk-review") ;;
  "pr view 7 --json body --jq .body") cat "$AFK_TEST_PR_BODY" ;;
  *) printf 'unexpected gh call: %s\n' "$*" >&2; exit 90 ;;
esac
EOF

chmod +x "$adapters/gh" "$adapters/codex" "$adapters/git" "$adapters/sleep" \
  "$skills/work-on/scripts/select-issue-codex.sh"

# The final launch boundary must independently load the skill before
# Sandcastle/Codex can create a branch or mutate anything. This catches a
# post-preflight disappearance without relying on the watcher check.
: >"$events"
missing_home="$fixture/missing-home"
missing_skill="$missing_home/.agents/skills/work-on/SKILL.md"
missing_skill_digest="$(printf 'missing\n' | sha256sum | cut -d' ' -f1)"
mkdir -p "$missing_home"
set +e
(
  cd "$repo"
  PATH="$adapters:$PATH" \
    HOME="$missing_home" \
    AFK_TEST_EVENTS="$events" \
    AFK_TEST_AGENT_PROMPT="$agent_prompt" \
    AFK_TEST_PR_BODY="$pr_body" \
    AFK_TEST_RELEASE="$fixture/release" \
    "$workflow_root/node_modules/.bin/tsx" \
    "$workflow_root/.sandcastle/main.mts" \
    99 afk/issue-99 main "$missing_skill_digest"
) >"$fixture/missing-launch-skill.out" 2>&1
missing_launch_status=$?
set -e
[[ "$missing_launch_status" -ne 0 ]] || {
  echo "expected missing launch skill to fail" >&2
  exit 1
}
grep -Fq "cannot load the required work-on skill at $missing_skill" \
  "$fixture/missing-launch-skill.out"
if grep -q '^codex-agent ' "$events"; then
  echo "Codex launched before the required work-on skill was loaded" >&2
  exit 1
fi

# Even a readable skill must match the workflow fingerprint pinned before the
# one-shot authorization was consumed.
: >"$events"
wrong_digest="$(printf '0%.0s' {1..64})"
set +e
(
  cd "$repo"
  PATH="$adapters:$PATH" \
    HOME="$test_home" \
    AFK_TEST_EVENTS="$events" \
    AFK_TEST_AGENT_PROMPT="$agent_prompt" \
    AFK_TEST_PR_BODY="$pr_body" \
    AFK_TEST_RELEASE="$fixture/release" \
    "$workflow_root/node_modules/.bin/tsx" \
    "$workflow_root/.sandcastle/main.mts" \
    98 afk/issue-98 main "$wrong_digest"
) >"$fixture/changed-launch-skill.out" 2>&1
changed_launch_status=$?
set -e
[[ "$changed_launch_status" -ne 0 ]] || {
  echo "expected changed launch skill to fail" >&2
  exit 1
}
grep -Fq "cannot load the required work-on skill because it changed after preflight: $skills/work-on/SKILL.md" \
  "$fixture/changed-launch-skill.out"
if grep -q '^codex-agent ' "$events"; then
  echo "Codex launched with a work-on skill that changed after preflight" >&2
  exit 1
fi

run_command() {
  (
    cd "$repo"
    PATH="$adapters:$PATH" \
      HOME="$test_home" \
      AFK_TEST_EVENTS="$events" \
      AFK_TEST_STATE="$state" \
      AFK_TEST_PR_BODY="$pr_body" \
      AFK_TEST_REAL_GIT="$real_git" \
      AFK_TEST_TIMER_EVENTS="$timer_events" \
      AFK_TEST_AGENT_PROMPT="$agent_prompt" \
      AFK_TEST_RELEASE="$fixture/release" \
      NODE_OPTIONS="${NODE_OPTIONS:-} --require=$timer_recorder" \
      AFK_TEST_FAIL_FETCH="${2:-0}" \
      AFK_TEST_FAIL_GH_AUTH="${3:-0}" \
      AFK_TEST_FAIL_CODEX_AUTH="${4:-0}" \
      AFK_TEST_INCLUDE_REVIEW_LABEL="${1:-0}" \
      "$command_under_test"
  )
}

preflight_cases=(
  'github-auth|GitHub authentication is unavailable|1|0|'
  'codex-auth|Codex authentication is unavailable|0|1|'
  "missing-selector-skill|required shared skill is unavailable, unreadable, or empty: $skills/select-issue/SKILL.md|0|0|select-issue"
  "missing-worker-skill|required shared skill is unavailable, unreadable, or empty: $skills/work-on/SKILL.md|0|0|work-on"
  "missing-delegation-skill|required shared skill is unavailable, unreadable, or empty: $skills/tdd/SKILL.md|0|0|tdd"
  "missing-review-skill|required shared skill is unavailable, unreadable, or empty: $skills/code-review/SKILL.md|0|0|code-review"
)
for preflight_case in "${preflight_cases[@]}"; do
  IFS='|' read -r case_name diagnostic fail_gh fail_codex missing_skill \
    <<<"$preflight_case"
  : >"$events"
  : >"$state"
  if [[ -n "$missing_skill" ]]; then
    rm "$skills/$missing_skill/SKILL.md"
  fi
  if run_command 1 0 "$fail_gh" "$fail_codex" \
    >"$fixture/$case_name.out" 2>&1; then
    echo "expected $case_name preflight to fail" >&2
    exit 1
  fi
  grep -Fq "$diagnostic" "$fixture/$case_name.out"
  if grep -Eq '^(selector |gh api --method DELETE|gh label (create|edit)|codex-agent )' "$events"; then
    echo "policy repair or issue work began after $case_name failure" >&2
    exit 1
  fi
  if [[ -n "$missing_skill" ]]; then
    printf 'scripted %s skill\n' "$missing_skill" \
      >"$skills/$missing_skill/SKILL.md"
  fi
done

: >"$events"
: >"$state"
if AFK_TEST_REMOVE_WORK_ON_AFTER_SELECTION=1 \
  run_command 1 >"$fixture/work-on-selection-race.out" 2>&1; then
  echo "expected work-on disappearance after selection to fail" >&2
  exit 1
fi
grep -Fq "required shared skill changed or became unavailable before claim: $skills/work-on/SKILL.md" \
  "$fixture/work-on-selection-race.out"
if grep -Eq '^(gh api --method DELETE|codex-agent )' "$events"; then
  echo "authorization was consumed after the work-on skill disappeared" >&2
  exit 1
fi
cat >"$skills/work-on/SKILL.md" <<'EOF'
---
name: work-on
description: Scripted AFK work-on fixture.
---

AFK_TEST_WORK_ON_INSTRUCTION
EOF

: >"$events"
: >"$state"
if AFK_TEST_REPLACE_WORK_ON_AFTER_SELECTION=1 \
  run_command 1 >"$fixture/work-on-replacement-race.out" 2>&1; then
  echo "expected work-on replacement after selection to fail" >&2
  exit 1
fi
grep -Fq "shared work-on skill changed before claim: $skills/work-on/SKILL.md" \
  "$fixture/work-on-replacement-race.out"
if grep -Eq '^(gh api --method DELETE|codex-agent )' "$events"; then
  echo "authorization was consumed after the work-on skill changed" >&2
  exit 1
fi
cat >"$skills/work-on/SKILL.md" <<'EOF'
---
name: work-on
description: Scripted AFK work-on fixture.
---

AFK_TEST_WORK_ON_INSTRUCTION
EOF

: >"$events"
: >"$state"
if AFK_TEST_REMOVE_TDD_AFTER_SELECTION=1 \
  run_command 1 >"$fixture/supporting-skill-selection-race.out" 2>&1; then
  echo "expected supporting-skill disappearance after selection to fail" >&2
  exit 1
fi
grep -Fq "required shared skill changed or became unavailable before claim: $skills/tdd/SKILL.md" \
  "$fixture/supporting-skill-selection-race.out"
if grep -Eq '^(gh api --method DELETE|codex-agent )' "$events"; then
  echo "authorization was consumed after a supporting skill disappeared" >&2
  exit 1
fi
printf 'scripted tdd skill\n' >"$skills/tdd/SKILL.md"

: >"$events"
: >"$state"
if run_command 0 >"$fixture/missing-label.out" 2>&1; then
  echo "expected missing afk-review preflight to fail" >&2
  exit 1
fi
grep -q 'missing required GitHub label: afk-review' "$fixture/missing-label.out" || {
  cat "$fixture/missing-label.out" >&2
  exit 1
}
if grep -q '^selector ' "$events"; then
  echo "selection ran after failed preflight" >&2
  exit 1
fi

: >"$events"
if run_command 1 1 >"$fixture/fetch.out" 2>&1; then
  echo "expected default-branch synchronization preflight to fail" >&2
  exit 1
fi
grep -q 'could not synchronize origin/main; no issue was claimed' "$fixture/fetch.out"
grep -q '^git-fetch-failed ' "$events"
if grep -Eq '^(selector |gh api --method DELETE|codex-agent )' "$events"; then
  echo "issue work began after failed default-branch synchronization" >&2
  exit 1
fi

: >"$events"
(
  exec 8>"$repo/.git/afk-tracer.lock"
  flock -n 8
  if run_command 1 >"$fixture/ownership.out" 2>&1; then
    echo "expected exclusive watcher ownership preflight to fail" >&2
    exit 1
  fi
  grep -q 'another AFK tracer owns this repository' "$fixture/ownership.out"
)
if grep -q '^selector ' "$events"; then
  echo "selection ran without exclusive ownership" >&2
  exit 1
fi

: >"$events"
: >"$timer_events"
progress="$fixture/progress.out"

fail_with() {
  echo "$1" >&2
  cat "$2" >&2
  exit 1
}

# Commentary must reach the launch terminal *as it is produced*, not as a batch
# flushed when the agent exits. The scripted agent blocks after its first
# commentary message until this scenario releases it, so observing that
# commentary in the capture below happens while the agent is provably still
# running mid-turn. A watcher that buffered the stream to exit would never
# satisfy this poll, and the bounded loop turns that into a loud failure.
rm -f "$fixture/release"
run_command 1 >"$progress" 2>&1 &
watcher_pid=$!

live_wait=0
until grep -Fxq '[afk #42] scripted commentary line one' "$progress" 2>/dev/null; do
  live_wait=$((live_wait + 1))
  if [[ "$live_wait" -ge 500 ]]; then
    kill "$watcher_pid" 2>/dev/null || true
    wait "$watcher_pid" 2>/dev/null || true
    fail_with \
      "agent commentary never appeared in watcher terminal output while the agent was still running" \
      "$progress"
  fi
  /usr/bin/sleep 0.02
done

touch "$fixture/release"
watcher_status=0
wait "$watcher_pid" || watcher_status=$?
[[ "$watcher_status" == 0 ]] ||
  fail_with "watcher exited with status $watcher_status" "$progress"

# The operator watching the launch terminal must see the agent working on the
# active issue, while the durable log keeps the complete record.
grep -Fxq '[afk #42] scripted commentary line one' "$progress" ||
  fail_with "agent commentary line one missing from watcher terminal output" "$progress"
grep -Fxq '[afk #42] scripted commentary line two' "$progress" ||
  fail_with "agent commentary line two missing from watcher terminal output" "$progress"
grep -Fxq '[afk #42] scripted coloured commentary line three' "$progress" ||
  fail_with "ANSI-coloured agent commentary missing or unstripped in watcher terminal output" "$progress"
[[ "$(grep -Fc '[31m' "$progress" || true)" == 0 ]] ||
  fail_with "ANSI colour sequence residue leaked into watcher terminal output" "$progress"
[[ "$(grep -Fc '[0m' "$progress" || true)" == 0 ]] ||
  fail_with "ANSI reset sequence residue leaked into watcher terminal output" "$progress"
# Criterion 7 ("long or multiline agent content is rendered without making
# subsequent lifecycle messages ambiguous"): agent text chunks end with a
# newline, so splitting them yields a trailing empty segment. That segment must
# never surface as a bare prefix line with no content behind it.
[[ "$(grep -Ec '^\[afk #42\] ?$' "$progress" || true)" == 0 ]] ||
  fail_with "empty agent line emitted as a bare run prefix in watcher terminal output" "$progress"
grep -Fxq '[afk #42] tool: Bash' "$progress" ||
  fail_with "tool activity missing from watcher terminal output" "$progress"
[[ "$(grep -Fc 'zumbleflux' "$progress" || true)" == 0 ]] ||
  fail_with "full tool arguments leaked into watcher terminal output" "$progress"
[[ "$(grep -Fc '"type":"item.' "$progress" || true)" == 0 ]] ||
  fail_with "raw provider JSON leaked into watcher terminal output" "$progress"

# Criterion 7, second half: a lifecycle message emitted *after* the streamed
# agent commentary must stay unambiguous — it reaches the terminal intact, on
# its own line, and never wearing the agent run prefix.
completion_line='AFK issue #42 completed: pull request #7 awaits review'
grep -Fxq "$completion_line" "$progress" ||
  fail_with "lifecycle completion message missing or altered after streamed agent commentary" "$progress"
[[ "$(grep -Fc "[afk #42] $completion_line" "$progress" || true)" == 0 ]] ||
  fail_with "lifecycle completion message was rendered as agent commentary" "$progress"
commentary_last="$(grep -Fxn '[afk #42] scripted coloured commentary line three' "$progress" | cut -d: -f1)"
completion_at="$(grep -Fxn "$completion_line" "$progress" | cut -d: -f1)"
[[ -n "$commentary_last" && -n "$completion_at" && "$completion_at" -gt "$commentary_last" ]] ||
  fail_with "lifecycle completion message did not follow the streamed agent commentary" "$progress"

durable_log="$repo/.sandcastle/logs/afk-issue-42.log"
[[ -s "$durable_log" ]] ||
  fail_with "durable per-run log $durable_log is missing or empty" "$progress"
grep -Fq 'Bash(scripted-tracer-command --secret-argument zumbleflux)' "$durable_log" ||
  fail_with "durable log lost the complete tool invocation" "$durable_log"
grep -Fq 'tail -f .sandcastle/logs/afk-issue-42.log' "$progress" ||
  fail_with "watcher terminal no longer displays the durable log location" "$progress"

# A watcher must observe the authorized queue before asking the intelligent
# selector to spend model tokens, and it must return to that live query after
# an issue reaches its terminal outcome.
[[ "$(grep -c '^gh issue list --state open --label ready-for-agent --label Sandcastle ' "$events")" -ge 2 ]]

claim_line="$(grep -n '^gh api --method DELETE repos/acme/widget/issues/42/labels/Sandcastle$' "$events" | cut -d: -f1)"
launch_line="$(grep -n '^codex-agent ' "$events" | cut -d: -f1)"
[[ -n "$claim_line" && -n "$launch_line" && "$claim_line" -lt "$launch_line" ]]
grep -Eq '^codex-agent cwd=.*/\.sandcastle/worktrees/.* branch=afk/issue-42 args=exec --json --dangerously-bypass-approvals-and-sandbox -m gpt-5\.6-sol -c model_reasoning_effort="medium"$' "$events"
grep -Fqx '$work-on #42' "$agent_prompt"
git -C "$repo" show-ref --verify --quiet refs/heads/afk/issue-42
git -C "$repo" show afk/issue-42:afk-result.txt | grep -q '^completed by scripted work-on boundary$'
grep -qx pr-created "$state"
[[ "$(grep -c '^gh pr edit 7 --add-label afk-review$' "$events")" == 2 ]]
[[ "$(grep -c '^gh api repos/acme/widget/branches/main/protection$' "$events")" == 2 ]]
! grep -q '^gh pr checks 7 ' "$events"
grep -Fxq '1200000' "$timer_events" || {
  echo "real Sandcastle did not schedule the configured 1,200,000 ms idle timer" >&2
  cat "$timer_events" >&2
  exit 1
}

launches_before="$(grep -c '^codex-agent ' "$events")"
run_command 1 >"$fixture/duplicate.out" 2>&1
launches_after="$(grep -c '^codex-agent ' "$events")"
[[ "$launches_before" == 1 && "$launches_after" == 1 ]]
grep -q 'AFK watcher stopped while idle' "$fixture/duplicate.out"

printf 'AFK tracer black-box scenario passed\n'

"$(dirname "$command_under_test")/test-afk-watcher.sh"
