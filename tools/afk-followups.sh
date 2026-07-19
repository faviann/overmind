#!/usr/bin/env bash
set -euo pipefail

# Process canonical work-on Follow-ups from a pull-request body on stdin.
# Exit 0: no blocking discovery. Exit 2: a discovered issue blocks this issue.
# Any other nonzero status means classification/labeling was not reliable.

issue_number="${1:?usage: afk-followups.sh <issue-number> < pr-body.md}"
pr_body="$(cat)"

mapfile -t followups < <(
  awk '
    /^## Follow-ups$/ { in_followups = 1; next }
    /^## / { in_followups = 0 }
    in_followups && match($0, /^- #[0-9]+/) {
      print substr($0, RSTART + 3, RLENGTH - 3)
    }
  ' <<<"$pr_body"
)
[[ "${#followups[@]}" -gt 0 ]] || exit 0

for followup in "${followups[@]}"; do
  gh issue edit "$followup" --add-label needs-triage --add-label afk-review >/dev/null || {
    printf 'could not mark discovered issue #%s for triage and review\n' "$followup" >&2
    exit 1
  }
done

repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner)" || {
  printf 'could not resolve repository while classifying discovered work\n' >&2
  exit 1
}
blocker_numbers="$(gh api "repos/$repo/issues/$issue_number/dependencies/blocked_by" \
  --paginate --jq '.[].number')" || {
  printf 'could not classify discovered work against issue #%s dependencies\n' \
    "$issue_number" >&2
  exit 1
}

for followup in "${followups[@]}"; do
  if grep -Fxq "$followup" <<<"$blocker_numbers"; then
    printf 'discovered issue #%s blocks issue #%s\n' "$followup" "$issue_number"
    exit 2
  fi
done
