---
name: report-afk
description: Reconstruct and acknowledge the current GitHub repository's AFK review inbox from durable pull requests and issues. Use when a maintainer asks for an AFK report, morning report, AFK outcomes, review inbox, individual AFK acknowledgement, or explicit approval of every artifact in the presented AFK report.
---

# Report AFK Outcomes

Use `scripts/report-afk.py` relative to this file. Treat GitHub pull requests and
issues as the record of truth; do not read or require local Sandcastle logs.

## Report

1. Run `scripts/report-afk.py report` from the current repository.
2. Preserve the exact output and its artifact keys in the current context. A
   temporary file plus `tee` is appropriate when acknowledgement may follow.
3. Present the report verbatim. Do not remove any label while generating or
   displaying it.

The report includes open and merged `afk-review` pull requests, their linked
`Closes` or `Progresses` issues, discovered `afk-review` issues, and the open
`Sandcastle` queue.

## Acknowledge selected artifacts

Wait for the user to explicitly select artifact keys from the report. Then run:

```sh
scripts/report-afk.py ack pr:123 issue:456
```

Pass only the selected keys. Never acknowledge a queue entry unless it also
appears as an `afk-review` artifact. Acknowledgement removes only `afk-review`;
never add or remove readiness, dependency, triage, or `Sandcastle` labels.

## Approve all

Run approve-all only after an explicit user instruction to approve all artifacts
in a report presented in the current context. Feed the exact preserved report,
not a new query or reconstructed artifact list, to:

```sh
scripts/report-afk.py approve-all < "$report_afk_presented_file"
```

If no report was presented in the current context, generate and present one,
then stop and wait for confirmation. Never treat approval or acknowledgement as
readiness, queue authorization, merge approval, or permission to do more work.
