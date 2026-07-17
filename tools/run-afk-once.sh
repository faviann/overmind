#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'AFK preflight failed: %s\n' "$*" >&2
  exit 1
}

for command in git gh codex npm flock; do
  command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || fail "run this command from a Git repository"
cd "$repo_root"

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

labels="$(gh label list --limit 100 --json name --jq '.[].name')" || \
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

[[ -x node_modules/.bin/tsx && -f node_modules/@ai-hero/sandcastle/package.json ]] || \
  fail "checked-in AFK dependencies are not installed; run 'npm ci' before launch"

selection="$($selector afk)" || fail "intelligent AFK selection failed"
mapfile -t selected_urls < <(sed -nE 's|^Selected issue: (https://github.com/[^/]+/[^/]+/issues/[0-9]+)$|\1|p' <<<"$selection")
[[ "${#selected_urls[@]}" -eq 1 ]] || fail "selector did not return exactly one issue"

selected_url="${selected_urls[0]}"
selected_repo="$(sed -nE 's|^https://github.com/([^/]+/[^/]+)/issues/[0-9]+$|\1|p' <<<"$selected_url")"
[[ "$selected_repo" == "$repo_name" ]] || \
  fail "selector returned an issue from $selected_repo instead of $repo_name"
issue_number="${selected_url##*/}"
branch="afk/issue-$issue_number"

gh issue edit "$issue_number" --remove-label Sandcastle >/dev/null || \
  fail "could not claim issue #$issue_number by removing Sandcastle"

git fetch --quiet origin "refs/heads/$default_branch:refs/remotes/origin/$default_branch" || \
  fail "could not update origin/$default_branch after claiming issue #$issue_number"

npm run --silent afk:sandcastle -- "$issue_number" "$branch" "$default_branch"

mapfile -t pull_requests < <(
  gh pr list --head "$branch" --state open --json number --jq '.[].number'
)
[[ "${#pull_requests[@]}" -eq 1 ]] || \
  fail "expected exactly one open pull request for $branch; authorization remains consumed"
gh pr edit "${pull_requests[0]}" --add-label afk-review >/dev/null || \
  fail "could not add afk-review to pull request #${pull_requests[0]}"

printf 'AFK issue #%s completed: pull request #%s awaits review\n' \
  "$issue_number" "${pull_requests[0]}"
