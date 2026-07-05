# Per-slice workflow (checklist, not a pipeline)

This is a convention the agent follows, never a runtime to build.
Do not write tooling that enforces stage transitions.

## Checklist

1. **Grill** — `/grill-me` scoped to THIS slice's open questions only.
   The spec and handoff doc answer most questions; explore them before
   asking. Never grill on closed decisions.

2. **Red gate** — write ONE failing behavior test through the public
   interface (MCP tool or `memctl`). Present it to the human for review
   before implementing. Once the human says the pattern is trusted,
   sampling replaces per-test review — but the first slices are all-review.

3. **TDD loop** — minimum code to green. `make test-one` for the fast
   loop, `make test` before commit. Commit per green cycle with a
   message naming the behavior, not the code.

4. **Refactor** — with the suite green. Tests must not change:
   `git diff -- '*Tests*'` must come back empty. Any test edit during
   refactor stops the slice and goes to the human.

5. **Validate** — `make accept`. The vertical path (log trace → propose →
   approve --by → search → fetch → consumed event visible in trace) must
   still pass end to end.

6. **Memory update** — new decisions made during this slice:
   - design decision with a non-obvious rationale → ADR patch in `docs/adr/`
   - durable fact/decision → `save_note` proposal into namespace
     `memory-system` (pre-server: one line each in `docs/decisions.md`)
   Record the WHY, not the what — the what lives in git.

7. **Handoff** — end the session with a short summary: outcome,
   decisions made, leftovers/deferred items. Append it to the issue
   (or `docs/handoffs.md` if local tracking).

## Gates summary
- Gate A (before slice): human approved the slice scope (via /to-issues review)
- Gate B (at RED): human reviews the failing test
- Gate C (after refactor): test-diff must be empty; deviations to human