#!/usr/bin/env bash
set -euo pipefail

readonly command_under_test="$(git rev-parse --show-toplevel)/tools/run-afk-once.sh"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

repo="$fixture/repo"
remote="$fixture/remote.git"
adapters="$fixture/adapters"
skills="$fixture/skills"
events="$fixture/events"
state="$fixture/state"
mkdir -p "$repo" "$adapters" "$skills/work-on/scripts" \
  "$skills/implement" "$skills/tdd" "$skills/code-review" "$skills/select-issue" \
  "$repo/node_modules/.bin" "$repo/node_modules/@ai-hero/sandcastle"
touch "$events" "$state"
touch "$repo/node_modules/.bin/tsx" "$repo/node_modules/@ai-hero/sandcastle/package.json"
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

cat >"$adapters/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'codex %s\n' "$*" >>"$AFK_TEST_EVENTS"
[[ "${1:-} ${2:-}" == "login status" ]]
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

cat >"$adapters/npm" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'sandcastle %s\n' "$*" >>"$AFK_TEST_EVENTS"
[[ "$*" == "run --silent afk:sandcastle -- 42 afk/issue-42 main" ]]
printf 'pr-created\n' >>"$AFK_TEST_STATE"
EOF

cat >"$adapters/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh %s\n' "$*" >>"$AFK_TEST_EVENTS"
case "$*" in
  "auth status") ;;
  "label list --limit 100 --json name --jq .[].name")
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

chmod +x "$adapters/gh" "$adapters/codex" "$adapters/npm" \
  "$skills/work-on/scripts/select-issue-codex.sh" "$repo/node_modules/.bin/tsx"

run_command() {
  (
    cd "$repo"
    PATH="$adapters:$PATH" \
      AFK_SKILLS_ROOT="$skills" \
      AFK_TEST_EVENTS="$events" \
      AFK_TEST_STATE="$state" \
      AFK_TEST_INCLUDE_REVIEW_LABEL="${1:-0}" \
      "$command_under_test"
  )
}

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
launch_line="$(grep -n '^sandcastle ' "$events" | cut -d: -f1)"
[[ -n "$claim_line" && -n "$launch_line" && "$claim_line" -lt "$launch_line" ]]
grep -q '^sandcastle run --silent afk:sandcastle -- 42 afk/issue-42 main$' "$events"
grep -q '^gh pr edit 7 --add-label afk-review$' "$events"

launches_before="$(grep -c '^sandcastle ' "$events")"
if run_command 1 >"$fixture/duplicate.out" 2>&1; then
  echo "expected the consumed authorization to prevent a second run" >&2
  exit 1
fi
launches_after="$(grep -c '^sandcastle ' "$events")"
[[ "$launches_before" == 1 && "$launches_after" == 1 ]]
grep -q 'selector did not return exactly one issue' "$fixture/duplicate.out"

printf 'AFK tracer black-box scenario passed\n'
