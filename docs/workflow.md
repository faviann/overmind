# Per-slice workflow (checklist, not a pipeline)

A convention the agent follows, never a runtime to build. No tooling
that enforces stage transitions. Mechanics live in the skills
(`/tdd`, `/grilling`, `/code-review`); this file is the ordering plus
the project-specific gates and commands.

## Checklist

1. **Orient** — spec + slice issue answer most questions. Remaining
   open decisions → plain questions to the human. Cascading vagueness →
   scoped `/grilling`. Never re-open closed decisions.

2. **Seams** — confirm the seams tests observe behavior through
   (normally MCP tools or `memctl`). No test at an unconfirmed seam;
   an internal seam needs explicit human blessing.

3. **TDD loop** — `/tdd`. ONE failing test at a confirmed seam,
   failing for the right reason. From RED the test is frozen: any
   edit — even "the interface should be different" — goes to the
   human. Minimum code to green. Commit per green cycle, gated on
   `make test-one` + typecheck; message names the behavior.

4. **Validate** — slice end: `make test` + `make accept` (vertical
   path: log trace → propose → approve --by → search → fetch →
   consumed event in trace). Fixes are new commits.

5. **Review** — `/code-review`; refactoring happens here, not in the
   loop. Refactor commits: `git diff -- '*Tests*'` empty. Test-quality
   fixes → separate, labeled commits. If compilation forces one atomic
   commit, label it "refactor + test adaptation" and get approval
   first. A contract change is not a refactor — new red→green cycle.
   Refactors repeatedly hitting tests = wrong seam; raise the pattern,
   don't relax the rule.

6. **Memory** — non-obvious design rationale → ADR in `docs/adr/`;
   other durable decisions → one line in `docs/decisions.md`. Record
   the WHY. (Post-server this routes through `save_note` — issue #10.)

7. **Closeout** — on the slice issue: outcome, decisions, leftovers.
   Durable record, not the `/handoff` skill. GitHub down → park
   locally, post when back; the issue stays the system of record.

## Gates
- **A** (before slice): human approved scope via issue review
- **B** (before tests): seams confirmed; tests reviewed by sampling
- **C** (immutability): tests frozen from RED; refactor commits carry
  an empty test diff; exceptions only as labeled, human-approved commits
