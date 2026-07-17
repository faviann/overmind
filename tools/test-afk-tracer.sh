#!/usr/bin/env bash
set -euo pipefail

readonly command_under_test="$(git rev-parse --show-toplevel)/tools/run-afk-once.sh"
readonly real_git="$(command -v git)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

repo="$fixture/repo"
remote="$fixture/remote.git"
adapters="$fixture/adapters"
skills="$fixture/skills"
events="$fixture/events"
state="$fixture/state"
pr_body="$fixture/pr-body.md"
mkdir -p "$repo" "$adapters" "$skills/work-on/scripts" \
  "$skills/implement" "$skills/tdd" "$skills/code-review" "$skills/select-issue"
touch "$events" "$state"
for skill in work-on implement tdd code-review select-issue; do
  touch "$skills/$skill/SKILL.md"
done

git init --bare --quiet "$remote"
git -C "$repo" init --quiet --initial-branch=main
git -C "$repo" config user.email afk-test@example.invalid
git -C "$repo" config user.name "AFK test"
git -C "$repo" commit --quiet --allow-empty -m base
git -C "$repo" remote add origin "$remote"
git -C "$repo" push --quiet -u origin main

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
printf 'codex-agent cwd=%s branch=%s args=%s prompt=%s\n' \
  "$PWD" "$branch" "$*" "$prompt" >>"$AFK_TEST_EVENTS"
[[ "$prompt" == '$work-on #42' ]]

printf 'completed by scripted work-on boundary\n' >afk-result.txt
git add afk-result.txt
git commit --quiet -m 'Complete scripted AFK issue'
cat >"$AFK_TEST_PR_BODY" <<'BODY'
## Issues

Closes #42

## Workflow telemetry

| Field | Observed value |
|---|---|
| Final workflow outcome | Closes |
BODY
gh pr create --head "$branch" --body-file "$AFK_TEST_PR_BODY" >/dev/null
printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"<promise>COMPLETE</promise>"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}'
EOF

cat >"$adapters/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh %s\n' "$*" >>"$AFK_TEST_EVENTS"
if [[ "${1:-} ${2:-}" == "pr create" ]]; then
  [[ "$*" == "pr create --head afk/issue-42 --body-file $AFK_TEST_PR_BODY" ]]
  grep -q '^Closes #42$' "$AFK_TEST_PR_BODY"
  grep -q '^## Workflow telemetry$' "$AFK_TEST_PR_BODY"
  printf 'pr-created\n' >>"$AFK_TEST_STATE"
  printf 'https://github.com/acme/widget/pull/7\n'
  exit 0
fi
case "$*" in
  "auth status") [[ "${AFK_TEST_FAIL_GH_AUTH:-0}" != 1 ]] ;;
  "label list --limit 1000 --json name --jq .[].name")
    printf '%s\n' ready-for-agent Sandcastle
    if [[ "${AFK_TEST_INCLUDE_REVIEW_LABEL:-0}" == 1 ]]; then
      printf '%s\n' afk-review
    fi
    ;;
  "repo view --json nameWithOwner --jq .nameWithOwner") printf 'acme/widget\n' ;;
  "repo view --json defaultBranchRef --jq .defaultBranchRef.name") printf 'main\n' ;;
  "issue edit 42 --remove-label Sandcastle") printf 'claimed\n' >>"$AFK_TEST_STATE" ;;
  "pr list --head afk/issue-42 --state open --json number --jq .[].number")
    grep -qx pr-created "$AFK_TEST_STATE"
    printf '7\n'
    ;;
  "pr edit 7 --add-label afk-review") ;;
  *) printf 'unexpected gh call: %s\n' "$*" >&2; exit 90 ;;
esac
EOF

chmod +x "$adapters/gh" "$adapters/codex" "$adapters/git" \
  "$skills/work-on/scripts/select-issue-codex.sh"

run_command() {
  (
    cd "$repo"
    PATH="$adapters:$PATH" \
      AFK_SKILLS_ROOT="$skills" \
      AFK_TEST_EVENTS="$events" \
      AFK_TEST_STATE="$state" \
      AFK_TEST_PR_BODY="$pr_body" \
      AFK_TEST_REAL_GIT="$real_git" \
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
  "missing-skill|required shared skill is unavailable: $skills/tdd/SKILL.md|0|0|tdd"
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
  if grep -Eq '^(selector |gh issue edit|gh label (create|edit)|codex-agent )' "$events"; then
    echo "policy repair or issue work began after $case_name failure" >&2
    exit 1
  fi
  if [[ -n "$missing_skill" ]]; then
    touch "$skills/$missing_skill/SKILL.md"
  fi
done

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
if grep -Eq '^(selector |gh issue edit|codex-agent )' "$events"; then
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
run_command 1

claim_line="$(grep -n '^gh issue edit 42 --remove-label Sandcastle$' "$events" | cut -d: -f1)"
launch_line="$(grep -n '^codex-agent ' "$events" | cut -d: -f1)"
[[ -n "$claim_line" && -n "$launch_line" && "$claim_line" -lt "$launch_line" ]]
grep -Eq '^codex-agent cwd=.*/\.sandcastle/worktrees/.* branch=afk/issue-42 args=exec --json --dangerously-bypass-approvals-and-sandbox -m gpt-5\.6-sol -c model_reasoning_effort="medium" prompt=\$work-on #42$' "$events"
git -C "$repo" show-ref --verify --quiet refs/heads/afk/issue-42
git -C "$repo" show afk/issue-42:afk-result.txt | grep -q '^completed by scripted work-on boundary$'
grep -qx pr-created "$state"
grep -q '^gh pr edit 7 --add-label afk-review$' "$events"

launches_before="$(grep -c '^codex-agent ' "$events")"
if run_command 1 >"$fixture/duplicate.out" 2>&1; then
  echo "expected the consumed authorization to prevent a second run" >&2
  exit 1
fi
launches_after="$(grep -c '^codex-agent ' "$events")"
[[ "$launches_before" == 1 && "$launches_after" == 1 ]]
grep -q 'selector did not return exactly one issue' "$fixture/duplicate.out"

printf 'AFK tracer black-box scenario passed\n'
