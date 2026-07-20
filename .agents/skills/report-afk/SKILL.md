---
name: report-afk
description: Reconstruct and acknowledge the current GitHub repository's AFK review inbox from durable pull requests and issues. Use when a maintainer asks for an AFK report, morning report, AFK outcomes, review inbox, individual AFK acknowledgement, or explicit approval of every artifact in the presented AFK report.
---

# Report AFK Outcomes

Use `scripts/report-afk.py` relative to this file. Treat GitHub pull requests and
issues as the record of truth; do not read or require local Sandcastle logs.

## Report

1. Run `scripts/report-afk.py report` from the current repository.
2. Present the report verbatim, keeping its `pr:N` and `issue:N` artifact keys
   visible. Do not remove any label while generating or displaying it.

Include open and merged `afk-review` pull requests, their linked
`Closes` or `Progresses` issues, discovered `afk-review` issues, and the open
`Sandcastle` queue.

## Acknowledge selected artifacts

Wait for the user to explicitly select artifact keys from the report. Then run:

```sh
scripts/report-afk.py ack pr:123 issue:456
```

Pass only the selected keys. Never acknowledge a queue entry unless it also
appears as an `afk-review` artifact. Remove only `afk-review` during
acknowledgement; never add or remove readiness, dependency, triage, or
`Sandcastle` labels.

## Approve all

Approve all only after an explicit user instruction to approve every artifact
in a report presented in the current context. Run `ack` with exactly the
artifact keys that appear in that presented report — every `pr:N` and
`issue:N` key — never a new query or a reconstructed artifact list:

```sh
scripts/report-afk.py ack pr:123 pr:124 issue:456
```

Queue entries carry no artifact key; never acknowledge them through
approve-all. If no report was presented in the current context, generate and
present one, then stop and wait for confirmation. Never treat approval or
acknowledgement as readiness, queue authorization, merge approval, or
permission to do more work.
