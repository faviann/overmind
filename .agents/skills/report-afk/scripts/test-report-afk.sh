#!/usr/bin/env bash
set -euo pipefail

readonly skill_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

mkdir -p "$fixture/bin" "$fixture/repo"
git -C "$fixture/repo" init --quiet
events="$fixture/events"
touch "$events"

cat >"$fixture/bin/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >>"$REPORT_AFK_TEST_EVENTS"
case "$*" in
  "repo view --json nameWithOwner,url")
    printf '%s\n' '{"nameWithOwner":"acme/widget","url":"https://github.com/acme/widget"}' ;;
  "pr list --state all --label afk-review --limit 1000 --json number,title,state,mergedAt,url,body,mergeStateStatus")
    cat <<'JSON'
[{"number":10,"title":"Ship complete slice","state":"MERGED","mergedAt":"2026-07-18T09:00:00Z","url":"https://github.com/acme/widget/pull/10","body":"Closes #100\n\n## Evidence\n- Closure gate: all tested\n\n## Workflow telemetry\n| Final workflow outcome | Closes |\n\n## Follow-ups\nNone.","mergeStateStatus":"UNKNOWN"},{"number":11,"title":"Make partial progress","state":"OPEN","mergedAt":null,"url":"https://github.com/acme/widget/pull/11","body":"Progresses #101\n\n## Evidence\n- Three criteria remain\n\n## Workflow telemetry\n| Final workflow outcome | Progresses |\n\n## Follow-ups\n- #301","mergeStateStatus":"CLEAN"},{"number":12,"title":"Checks failed","state":"OPEN","mergedAt":null,"url":"https://github.com/acme/widget/pull/12","body":"Closes #102\n\n## Validation\n- integration: failed\n\n## Workflow telemetry\n| Final workflow outcome | Closes |\n\n## Follow-ups\nNone.","mergeStateStatus":"UNSTABLE"},{"number":13,"title":"Blocked work","state":"OPEN","mergedAt":null,"url":"https://github.com/acme/widget/pull/13","body":"Closes #103\n\n## Review\n- Awaiting policy decision\n\n## Workflow telemetry\n| Final workflow outcome | Closes |\n\n## Follow-ups\n- #302","mergeStateStatus":"BLOCKED"},{"number":14,"title":"Closed without merge","state":"CLOSED","mergedAt":null,"url":"https://github.com/acme/widget/pull/14","body":"Closes #104","mergeStateStatus":"UNKNOWN"},{"number":15,"title":"No required checks","state":"OPEN","mergedAt":null,"url":"https://github.com/acme/widget/pull/15","body":"Closes #105\n\n## Evidence\n- No checks configured\n\n## Workflow telemetry\n| Final workflow outcome | Closes |\n\n## Follow-ups\nNone.","mergeStateStatus":"CLEAN"}]
JSON
    ;;
  "pr checks 10 --required --json name,state,bucket,link") printf '%s\n' '[{"name":"test","state":"COMPLETED","bucket":"pass","link":"https://ci/10"}]' ;;
  "pr checks 11 --required --json name,state,bucket,link") printf '%s\n' '[{"name":"test","state":"IN_PROGRESS","bucket":"pending","link":"https://ci/11"}]' ;;
  "pr checks 12 --required --json name,state,bucket,link") printf '%s\n' '[{"name":"test","state":"COMPLETED","bucket":"fail","link":"https://ci/12"}]'; exit 1 ;;
  "pr checks 13 --required --json name,state,bucket,link") printf '%s\n' '[]' ;;
  "pr checks 15 --required --json name,state,bucket,link")
    case "${REPORT_AFK_TEST_CHECKS_MODE:-no-checks}" in
      no-checks) printf "%s\n" "no required checks reported on the 'issue-105' branch" >&2; exit 1 ;;
      unreadable) printf '%s\n' 'GraphQL: service unavailable' >&2; exit 1 ;;
      malformed) printf '%s\n' '{not-json' ;;
    esac ;;
  "issue view 100 --json number,title,state,url,labels") printf '%s\n' '{"number":100,"title":"Complete issue","state":"CLOSED","url":"https://github.com/acme/widget/issues/100","labels":[]}' ;;
  "issue view 101 --json number,title,state,url,labels") printf '%s\n' '{"number":101,"title":"Partial issue","state":"OPEN","url":"https://github.com/acme/widget/issues/101","labels":[]}' ;;
  "issue view 102 --json number,title,state,url,labels") printf '%s\n' '{"number":102,"title":"Failed issue","state":"OPEN","url":"https://github.com/acme/widget/issues/102","labels":[]}' ;;
  "issue view 103 --json number,title,state,url,labels") printf '%s\n' '{"number":103,"title":"Blocked issue","state":"OPEN","url":"https://github.com/acme/widget/issues/103","labels":[]}' ;;
  "issue view 105 --json number,title,state,url,labels") printf '%s\n' '{"number":105,"title":"No-check issue","state":"OPEN","url":"https://github.com/acme/widget/issues/105","labels":[]}' ;;
  "issue list --state all --label afk-review --limit 1000 --json number,title,state,url,labels")
    printf '%s\n' '[{"number":201,"title":"Discovered edge case","state":"OPEN","url":"https://github.com/acme/widget/issues/201","labels":[{"name":"afk-review"},{"name":"needs-triage"}]}]' ;;
  "issue list --state open --label Sandcastle --limit 1000 --json number,title,state,url,labels")
    printf '%s\n' '[{"number":202,"title":"Queued work","state":"OPEN","url":"https://github.com/acme/widget/issues/202","labels":[{"name":"ready-for-agent"},{"name":"Sandcastle"}]}]' ;;
  "pr view 11 --json labels") printf '%s\n' '{"labels":[{"name":"afk-review"}]}' ;;
  "issue view 201 --json labels") printf '%s\n' '{"labels":[{"name":"afk-review"},{"name":"needs-triage"}]}' ;;
  "issue view 202 --json labels") printf '%s\n' '{"labels":[{"name":"Sandcastle"}]}' ;;
  pr\ edit\ *\ --remove-label\ afk-review) ;;
  issue\ edit\ *\ --remove-label\ afk-review) ;;
  *) printf 'Unexpected gh call: %s\n' "$*" >&2; exit 99 ;;
esac
EOF
chmod +x "$fixture/bin/gh"

export PATH="$fixture/bin:$PATH"
export REPORT_AFK_TEST_EVENTS="$events"

report="$fixture/report.md"
(cd "$fixture/repo" && "$skill_root/scripts/report-afk.py" report) >"$report"

grep -Fq '# AFK review inbox — acme/widget' "$report"
grep -Fq 'pr:10 — [#10 Ship complete slice](https://github.com/acme/widget/pull/10)' "$report"
grep -Fq '**Outcome:** Merged' "$report"
grep -Fq '**Issue:** Closes [#100 Complete issue](https://github.com/acme/widget/issues/100) (CLOSED)' "$report"
grep -Fq '**Required checks:** [test](https://ci/10): pass (COMPLETED)' "$report"
grep -Fq '**Outcome:** Partial' "$report"
grep -Fq '**Outcome:** Failed' "$report"
grep -Fq '**Outcome:** Blocked' "$report"
grep -Fq '**Evidence:** integration: failed' "$report"
grep -Fq '**Telemetry:** | Final workflow outcome | Closes |' "$report"
grep -Fq '**Follow-ups:** #301' "$report"
grep -Fq 'issue:201 — [#201 Discovered edge case](https://github.com/acme/widget/issues/201)' "$report"
grep -Fq '[#202 Queued work](https://github.com/acme/widget/issues/202)' "$report"
grep -Fq 'pr:15 — [#15 No required checks](https://github.com/acme/widget/pull/15)' "$report"
grep -A5 -F 'pr:15 —' "$report" | grep -Fq '**Required checks:** None reported'
grep -Fq '<!-- report-afk:v1 repo=acme/widget artifacts=pr:10,pr:11,pr:12,pr:13,pr:15,issue:201 -->' "$report"
if grep -Fq 'Closed without merge' "$report"; then
  printf 'Closed-unmerged pull request appeared in report\n' >&2
  exit 1
fi

if grep -Eq '^(pr|issue) edit ' "$events"; then
  printf 'Report generation mutated GitHub labels\n' >&2
  exit 1
fi

if (cd "$fixture/repo" && REPORT_AFK_TEST_CHECKS_MODE=unreadable \
    "$skill_root/scripts/report-afk.py" report) >/dev/null 2>"$fixture/unreadable.err"; then
  printf 'Unreadable check state did not fail loudly\n' >&2
  exit 1
fi
grep -Fq 'GraphQL: service unavailable' "$fixture/unreadable.err"

if (cd "$fixture/repo" && REPORT_AFK_TEST_CHECKS_MODE=malformed \
    "$skill_root/scripts/report-afk.py" report) >/dev/null 2>"$fixture/malformed.err"; then
  printf 'Malformed check state did not fail loudly\n' >&2
  exit 1
fi
grep -Fq 'report-afk:' "$fixture/malformed.err"

printf '' >"$events"
(cd "$fixture/repo" && "$skill_root/scripts/report-afk.py" ack pr:11 issue:201) >"$fixture/ack.out"
grep -Fxq 'pr edit 11 --remove-label afk-review' "$events"
grep -Fxq 'issue edit 201 --remove-label afk-review' "$events"
[[ "$(grep -Ec '^(pr|issue) edit ' "$events")" == 2 ]]

printf '' >"$events"
if (cd "$fixture/repo" && "$skill_root/scripts/report-afk.py" ack issue:202) >"$fixture/queue.out" 2>"$fixture/queue.err"; then
  printf 'Queued issue without afk-review was acknowledged\n' >&2
  exit 1
fi
grep -Fq 'does not currently carry afk-review' "$fixture/queue.err"
if grep -Eq '^(pr|issue) edit ' "$events"; then
  printf 'Rejected queue acknowledgement mutated GitHub labels\n' >&2
  exit 1
fi

printf '' >"$events"
(cd "$fixture/repo" && "$skill_root/scripts/report-afk.py" approve-all <"$report") >"$fixture/all.out"
for expected in \
  'pr edit 10 --remove-label afk-review' \
  'pr edit 11 --remove-label afk-review' \
  'pr edit 12 --remove-label afk-review' \
  'pr edit 13 --remove-label afk-review' \
  'pr edit 15 --remove-label afk-review' \
  'issue edit 201 --remove-label afk-review'; do
  grep -Fxq "$expected" "$events"
done
[[ "$(grep -Ec '^(pr|issue) edit ' "$events")" == 6 ]]
if grep -Eq '^(pr|issue) (list|view) ' "$events"; then
  printf 'Approve-all re-queried the live artifact set\n' >&2
  exit 1
fi
if grep -Fq 'issue edit 202 ' "$events"; then
  printf 'Approve-all acknowledged a queue-only issue\n' >&2
  exit 1
fi

printf '' >"$events"
sed 's/repo=acme\/widget/repo=other\/repo/' "$report" >"$fixture/wrong-repo.md"
if (cd "$fixture/repo" && "$skill_root/scripts/report-afk.py" approve-all <"$fixture/wrong-repo.md") >/dev/null 2>"$fixture/wrong-repo.err"; then
  printf 'Approve-all accepted a report from another repository\n' >&2
  exit 1
fi
grep -Fq 'belongs to other/repo, not acme/widget' "$fixture/wrong-repo.err"
if grep -Eq '^(pr|issue) edit ' "$events"; then
  printf 'Wrong-repository report mutated GitHub labels\n' >&2
  exit 1
fi

printf 'AFK reporting and acknowledgement scenarios passed\n'
