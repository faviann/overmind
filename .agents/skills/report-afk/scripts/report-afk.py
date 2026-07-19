#!/usr/bin/env python3
"""Report and acknowledge durable AFK review artifacts."""

from __future__ import annotations

import json
import re
import subprocess
import sys
from typing import Any


PR_FIELDS = "number,title,state,mergedAt,url,body,mergeStateStatus"
ISSUE_FIELDS = "number,title,state,url,labels"
ARTIFACT_PATTERN = re.compile(r"^(pr|issue):([1-9][0-9]*)$")
REPORT_MARKER_PATTERN = re.compile(
    r"<!-- report-afk:v1 repo=([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+) artifacts=([^ ]*) -->"
)


def gh(*args: str, tolerate_failure: bool = False) -> str:
    result = subprocess.run(
        ["gh", *args], text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    if result.returncode and (not tolerate_failure or not result.stdout.strip()):
        detail = result.stderr.strip() or result.stdout.strip() or "unknown error"
        raise RuntimeError(f"gh {' '.join(args)} failed: {detail}")
    return result.stdout


def gh_json(*args: str, tolerate_failure: bool = False) -> Any:
    output = gh(*args, tolerate_failure=tolerate_failure).strip()
    return json.loads(output or "[]")


def section(body: str, names: list[str]) -> str:
    for name in names:
        match = re.search(
            rf"(?ims)^##\s+{re.escape(name)}\s*$\n(.*?)(?=^##\s+|\Z)", body
        )
        if match:
            lines = []
            for line in match.group(1).splitlines():
                cleaned = re.sub(r"^\s*[-*]\s+", "", line.strip())
                if cleaned:
                    lines.append(cleaned)
            if lines:
                summary = " ".join(lines)
                return summary if len(summary) <= 600 else f"{summary[:597]}..."
    return "Not recorded"


def linked_issue(body: str) -> tuple[str, int] | None:
    match = re.search(
        r"(?im)^\s*(Closes|Progresses)\s+"
        r"(?:(?:https://github\.com/[^/\s]+/[^/\s]+/issues/)|#)(\d+)\b",
        body,
    )
    return (match.group(1).title(), int(match.group(2))) if match else None


def checks_for(number: int) -> list[dict[str, Any]]:
    return gh_json(
        "pr",
        "checks",
        str(number),
        "--required",
        "--json",
        "name,state,bucket,link",
        tolerate_failure=True,
    )


def format_checks(checks: list[dict[str, Any]]) -> str:
    if not checks:
        return "None reported"
    rendered = []
    for check in checks:
        name = check.get("name", "unnamed")
        link = check.get("link")
        label = f"[{name}]({link})" if link else name
        rendered.append(
            f"{label}: {check.get('bucket', 'unknown')} ({check.get('state', 'UNKNOWN')})"
        )
    return ", ".join(rendered)


def outcome(pr: dict[str, Any], relation: str | None, checks: list[dict[str, Any]]) -> str:
    if relation == "Progresses":
        return "Partial"
    if any(str(check.get("bucket", "")).lower() in {"fail", "cancel"} for check in checks):
        return "Failed"
    if pr.get("mergeStateStatus") in {"BLOCKED", "DIRTY"}:
        return "Blocked"
    if pr.get("mergedAt"):
        return "Merged"
    return "Open"


def repository() -> dict[str, str]:
    return gh_json("repo", "view", "--json", "nameWithOwner,url")


def parse_artifacts(keys: list[str]) -> list[tuple[str, int]]:
    parsed = []
    seen = set()
    for key in keys:
        match = ARTIFACT_PATTERN.fullmatch(key)
        if not match:
            raise RuntimeError(f"invalid artifact key: {key}")
        artifact = (match.group(1), int(match.group(2)))
        if artifact not in seen:
            parsed.append(artifact)
            seen.add(artifact)
    return parsed


def remove_review_labels(artifacts: list[tuple[str, int]]) -> None:
    for kind, number in artifacts:
        gh(kind, "edit", str(number), "--remove-label", "afk-review")
        print(f"Acknowledged {kind}:{number}")


def acknowledge(keys: list[str]) -> None:
    repository()
    artifacts = parse_artifacts(keys)
    if not artifacts:
        raise RuntimeError("ack requires at least one artifact key")
    for kind, number in artifacts:
        artifact = gh_json(kind, "view", str(number), "--json", "labels")
        labels = {label["name"] for label in artifact.get("labels", [])}
        if "afk-review" not in labels:
            raise RuntimeError(f"{kind}:{number} does not currently carry afk-review")
    remove_review_labels(artifacts)


def approve_all(presented_report: str) -> None:
    repo_name = repository()["nameWithOwner"]
    markers = REPORT_MARKER_PATTERN.findall(presented_report)
    if len(markers) != 1:
        raise RuntimeError("approve-all requires exactly one complete presented report on stdin")
    report_repo, artifact_csv = markers[0]
    if report_repo != repo_name:
        raise RuntimeError(f"presented report belongs to {report_repo}, not {repo_name}")
    keys = artifact_csv.split(",") if artifact_csv else []
    remove_review_labels(parse_artifacts(keys))


def report() -> None:
    repository_data = repository()
    repo_name = repository_data["nameWithOwner"]
    prs = gh_json(
        "pr", "list", "--state", "all", "--label", "afk-review", "--limit", "1000", "--json", PR_FIELDS
    )
    prs = [pr for pr in prs if pr.get("state") == "OPEN" or pr.get("mergedAt")]
    discoveries = gh_json(
        "issue", "list", "--state", "all", "--label", "afk-review", "--limit", "1000", "--json", ISSUE_FIELDS
    )
    queue = gh_json(
        "issue", "list", "--state", "open", "--label", "Sandcastle", "--limit", "1000", "--json", ISSUE_FIELDS
    )
    artifacts = [f"pr:{pr['number']}" for pr in prs] + [
        f"issue:{issue['number']}" for issue in discoveries
    ]

    print(f"# AFK review inbox — {repo_name}")
    print()
    print("## Pull requests")
    if not prs:
        print("\nNone.")
    for pr in prs:
        checks = checks_for(pr["number"])
        mapping = linked_issue(pr.get("body") or "")
        relation = mapping[0] if mapping else None
        print(f"\n### pr:{pr['number']} — [#{pr['number']} {pr['title']}]({pr['url']})")
        print(f"\n- **Outcome:** {outcome(pr, relation, checks)}")
        if mapping:
            issue = gh_json("issue", "view", str(mapping[1]), "--json", ISSUE_FIELDS)
            print(
                f"- **Issue:** {mapping[0]} [#{issue['number']} {issue['title']}]"
                f"({issue['url']}) ({issue['state']})"
            )
        else:
            print("- **Issue:** No `Closes` or `Progresses` mapping recorded")
        print(f"- **Required checks:** {format_checks(checks)}")
        body = pr.get("body") or ""
        print(f"- **Evidence:** {section(body, ['Evidence', 'Closure gate', 'Validation', 'Review'])}")
        print(f"- **Telemetry:** {section(body, ['Workflow telemetry'])}")
        print(f"- **Follow-ups:** {section(body, ['Follow-ups'])}")

    print("\n## Discovered issues")
    if discoveries:
        for issue in discoveries:
            print(
                f"\n- issue:{issue['number']} — [#{issue['number']} {issue['title']}]"
                f"({issue['url']}) ({issue['state']})"
            )
    else:
        print("\nNone.")

    print("\n## Authorized queue")
    if queue:
        for issue in queue:
            print(f"\n- [#{issue['number']} {issue['title']}]({issue['url']})")
    else:
        print("\nNone.")

    artifact_csv = ",".join(artifacts)
    print(f"\n<!-- report-afk:v1 repo={repo_name} artifacts={artifact_csv} -->")


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: report-afk.py report | ack <artifact-key...> | approve-all", file=sys.stderr)
        return 2
    try:
        action = sys.argv[1]
        if action == "report" and len(sys.argv) == 2:
            report()
        elif action == "ack":
            acknowledge(sys.argv[2:])
        elif action == "approve-all" and len(sys.argv) == 2:
            approve_all(sys.stdin.read())
        else:
            print("usage: report-afk.py report | ack <artifact-key...> | approve-all", file=sys.stderr)
            return 2
    except (RuntimeError, KeyError, json.JSONDecodeError) as error:
        print(f"report-afk: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
