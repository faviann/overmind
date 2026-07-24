# Overmind — memory server

A self-hosted **memory/control substrate for agents**: one .NET server
exposing a small MCP tool surface over one PostgreSQL database. It records
what was said, what was decided, what was merely proposed, what was approved,
what source produced a fact, what agent used it, and how to replay the chain.

Not another chatbot memory feature, and not flat vector RAG: the working
agent is the extractor, and a human is the quality gate. Nothing enters
shared memory unreviewed.

The repository also contains a disabled, explicitly non-production synthetic
Codex capture tracer used to exercise one narrow Phase-2 ledger slice. See
[docs/capture-synthetic-slice.md](docs/capture-synthetic-slice.md); it is not
part of the supported server deployment.

## Core commitments

- **Append-only trace ledger** — the raw record of what happened is
  immutable (enforced by grants *and* trigger) and primary; every summary or
  index is a derived, rebuildable projection.
- **Provenance on every fact** — identity, source, versioning, and causal
  ("what did the agent actually consume?") provenance are v1 schema, logged
  server-side, never dependent on agent cooperation.
- **Proposal → approval** — agents propose shared memories; only an operator
  approves (`memctl`, reviewer identity required). Private notes are
  direct-write and owner-scoped.
- **Two-step hybrid retrieval** — search returns previews + provenance
  (FTS + recency, RRF-fused, per-lane scores kept); full content is fetched
  by id, and that fetch is itself traced.
- **The server is the only door** — consumers get bearer keys, never
  connection strings; governance lives in code, not prompts.

## Status

Phase 1 is complete. `v1.0.0` is the first compatibility release: it serves
streamable HTTP MCP with bearer-key identity by default, retains stdio MCP for
trusted local agents, and includes the `memctl` operator CLI.

## Documents

| Doc | Role |
|---|---|
| [`docs/north-star.md`](docs/north-star.md) | What the mature system is (orientation; never wins conflicts) |
| [`docs/memory-server-phase1-spec.md`](docs/memory-server-phase1-spec.md) | **Binding** build spec, schema, tool contracts, Do-Not-Build list |
| [`docs/agent-memory-handoff-v4.md`](docs/agent-memory-handoff-v4.md) | Intent and architecture where the spec is silent |
| [`CONTEXT.md`](CONTEXT.md) | Domain vocabulary |
| [`docs/articles/`](docs/articles/) | The article series on agentic memory the design draws from |

## Watch the authorized issue queue while AFK

Setup and launch are deliberately separate. Complete this one-time operator
checklist before authorizing any issue:

1. Authenticate the GitHub CLI and Codex, then verify both sessions:

   ```sh
   gh auth login
   gh auth status
   codex login
   codex login status
   ```

2. Install the repository's checked-in Node dependencies:

   ```sh
   npm ci
   ```

3. Install or update the shared `work-on`, `select-issue`, `implement`, `tdd`,
   and `code-review` skills. The watcher reads shared skills from
   `${AFK_SKILLS_ROOT:-$HOME/.agents/skills}`.

   ```sh
   npx skills add mattpocock/skills --global --agent '*' \
     --skill work-on --skill select-issue --skill implement --skill tdd \
     --skill code-review --yes
   npx skills update --global --yes

   skills_root="${AFK_SKILLS_ROOT:-$HOME/.agents/skills}"
   for skill in work-on select-issue implement tdd code-review; do
     test -f "$skills_root/$skill/SKILL.md"
   done
   test -x "$skills_root/work-on/scripts/select-issue-codex.sh"
   ```

4. Create these repository labels if they are missing, then verify all four
   before launch: `ready-for-agent`, `Sandcastle`, `afk-review`, and
   `needs-triage`.

   ```sh
   gh label create ready-for-agent --color 0e8a16
   gh label create Sandcastle --color fbca04
   gh label create afk-review --color 1d76db
   gh label create needs-triage --color d93f0b
   gh label list --limit 1000 --json name --jq '.[].name'
   ```

   `gh label create` reports that a label already exists; that is not an error
   requiring replacement. Confirm the existing label in the final listing.

5. Protect the default branch in GitHub repository settings. Require pull
   requests, require branches to be up to date before merging (strict status
   checks), and require the `test`, `test-compose`, and `reference-compose` CI
   contexts. This read-only check shows the configured policy:

   ```sh
   repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
   branch="$(gh repo view --json defaultBranchRef --jq .defaultBranchRef.name)"
   gh api "repos/$repo/branches/$branch/protection" |
     jq '{required_pull_request_reviews, required_status_checks}'
   ```

   `AFK_REQUIRED_CHECKS` is a space-separated override for the designated CI
   contexts. When it is unset, the watcher requires all three contexts above:
   `test test-compose reference-compose`. For example, an explicit equivalent
   launch is:

   ```sh
   AFK_REQUIRED_CHECKS='test test-compose reference-compose' make afk
   ```

For each launch, choose exactly one open issue that is already
`ready-for-agent`, review it, and add only the `Sandcastle` authorization:

```sh
gh issue view <issue-number> --json state,labels
gh issue edit <issue-number> --add-label Sandcastle
```

Do not use `Sandcastle` to bypass triage or make an issue ready. Start
`make afk` inside an operator-managed persistent shell such as tmux or herdr:

```sh
tmux new-session -s overmind-afk
make afk
```

Launch preflight is read-only: it checks policy and prerequisites but never
creates labels, installs skills, or repairs configuration. After preflight, the
watcher observes the live `ready-for-agent` + `Sandcastle` queue and claims the
selected issue by removing `Sandcastle` before agent launch. It then runs the
full `work-on` lifecycle on a named isolated branch/worktree and labels the
resulting pull request `afk-review`. It processes one issue at a time and starts
each selection from the latest verified default branch. An empty or unchanged
ineligible queue is polled without invoking an agent or selector model. A first
termination signal drains an active issue (or exits immediately while idle); a
second signal forces termination. The consumed `Sandcastle` authorization is
one attempt: a retry requires an operator to review the issue again and
explicitly re-add `Sandcastle`.

After tagging `afk-review`, the watcher hands the pull request to a guarded merge
stage. It merges unattended only when every gate holds, and otherwise leaves the
pull request open for review:

- Merge preflight requires the default branch to be protected: it must require
  pull requests, require branches to be up to date before merging (strict
  status checks), and require every designated CI check. Designated checks come
  from `AFK_REQUIRED_CHECKS` (space-separated, default
  `test test-compose reference-compose`). An unprotected
  or under-protected branch refuses the merge.
- Merge eligibility requires durable evidence that the work fully closed the
  issue: the pull request body must `Closes #<issue>` (never `Progresses` it)
  and its workflow-telemetry `Final workflow outcome` row must be exactly
  `Closes`. GitHub must also report the pull request `OPEN`, `MERGEABLE`, and
  `CLEAN`. A `Progresses` pull request, an inferred or unverified outcome,
  missing telemetry, a conflict, or a failing/pending check refuses the merge.
  If protection requires one or more approving reviews, the pull request stays
  `BLOCKED` and the tracer correctly refuses; unattended merge only applies when
  protection requires pull requests without a pending human approval.
- Eligible work merges with a merge commit (`gh pr merge --merge`), preserving
  the issue and remediation commits.
- Before claiming success the stage verifies the merge commit landed on the
  default branch and the linked issue closed. Only then does it delete the
  temporary worktree and the local and remote `afk/issue-<n>` branches.
- Any refusal, failed merge, or failed verification preserves all artifacts,
  keeps `afk-review`, prints the reason, and never claims a merge happened.
- Required CI is polled for up to sixty minutes. Explicit failures trigger one
  mechanical `gh run rerun --failed` attempt; another failure or the timeout
  leaves the pull request open with `afk-review` and preserves its artifacts.
- Out-of-scope issues recorded by `work-on` under `Follow-ups` receive only
  `needs-triage` and `afk-review`. A native blocking relationship pauses that
  lane; an unreadable relationship is treated as blocking. Neither discovery
  nor an idle agent can restore the consumed `Sandcastle` authorization.

## Connect an agent

Agents use MCP; they never receive an administrative database credential.
Choose HTTP for normal LAN use. Stdio is the trusted host-local option where a
Claude Code-spawned server process can reach PostgreSQL directly.

### Claude Code over HTTP

Ask the operator for your bearer key, export it before starting Claude Code,
and put this in the project `.mcp.json` (or the equivalent user-scoped MCP
configuration):

```sh
export OVERMIND_BEARER_KEY='<key provisioned for this agent>'
```

```json
{
  "mcpServers": {
    "overmind": {
      "type": "http",
      "url": "http://overmind.faviann.vms:8080/mcp",
      "headers": {
        "Authorization": "Bearer ${OVERMIND_BEARER_KEY}"
      }
    }
  }
}
```

The checked-in configuration contains only an environment-variable reference,
never the key itself. The day-1 endpoint is intentionally plain HTTP on the
trusted LAN. Run `/mcp` inside Claude Code to approve the project server and
check its connection.

### Claude Code over stdio

Stdio is restricted to trusted host-local use. Ask the operator to provision a
launcher at `/usr/local/bin/overmind-stdio` that starts the immutable image in
stdio mode and injects its runtime database credential *after* the consumer
process boundary. The launcher must not accept arbitrary arguments, expose its
environment, or write application logs to stdout.

Register that launcher as a user-scoped server:

```sh
claude mcp add --scope user --transport stdio overmind -- \
  /usr/local/bin/overmind-stdio
```

The Claude Code configuration contains neither a connection string nor a path
to a readable secret. The operator-owned launcher fixes the agent identity,
default namespace, and namespace allowlist; it is the privilege boundary, not
an agent-customizable convenience script. Run `claude mcp get overmind` to
inspect the saved consumer configuration.

## Operator path

### Reference Compose deployment

The default [`compose.yaml`](compose.yaml) is the canonical, production-oriented
reference deployment. It owns PostgreSQL provisioning for this deployment mode:
PostgreSQL becomes healthy, the `memsrv` login and `memory` database converge,
migrations complete, and only then does the HTTP server start.

Create ignored operator inputs from the placeholder-only examples:

```sh
cp .env.example .env
cp agent-keys.example.yaml agent-keys.yaml
chmod 600 .env agent-keys.yaml
```

These two ignored, mode-`0600` files are the reference deployment's intentional
local secret inputs. Replace every placeholder in both files.
`OVERMIND_VERSION` must be an explicit immutable release version; the admin and
runtime passwords have no defaults. Then converge the complete deployment with
one command:

```sh
docker compose up -d --wait
```

The server is published on `0.0.0.0:8080` by default. Override
`OVERMIND_HTTP_BIND` or `OVERMIND_HTTP_PORT` in `.env` when needed. PostgreSQL
has no host-published port, its data lives in a Compose-managed named volume,
and the bearer-key YAML is mounted read-only. Re-running the same command is
safe: role provisioning converges the runtime password and migrations are
idempotent.

Development remains isolated from this reference stack. `make db-up` and the
test targets use [`compose.dev.yaml`](compose.dev.yaml) by default and continue
to use `memory_dev` and disposable test databases. An isolated execution
environment may instead point the complete test lifecycle at an already-running
PostgreSQL 18 test instance, without Docker access:

```sh
MEMSRV_TEST_ADMIN_CONNECTION_STRING='postgres://test_admin:<password>@db:5432/postgres' \
  make test
```

This explicit mode requires `psql` and PostgreSQL superuser authority for the
role, template, clone, and cleanup checks; it never falls back to Compose. Pass
the credential only in the invocation environment, not in a tracked file. See
[testing conventions](docs/testing.md#database-lifecycle) for the complete
contract. `make migrate-dev` remains Compose-only and targets `memory_dev`.

### Existing deployed service

Operator actions stay on the server host. SSH in, identify the service
container, and run the in-image CLI with its existing runtime environment:

```sh
ssh overmind.faviann.vms
docker ps --filter ancestor=ghcr.io/faviann/overmind:1.0.0

docker exec -it <overmind-container> memctl pending memory-system
docker exec -it <overmind-container> memctl show <proposal-uuid>
docker exec -it <overmind-container> \
  memctl approve <proposal-uuid> --by human:<name> --edit
```

The image includes `nano` for the interactive edit path. Approval, rejection,
retirement, audit, release, and migration remain operator-only; none is exposed
as an agent-facing MCP tool. See the
[deployment contract](docs/deployment-contract.md) for key-file provisioning,
the one-shot migration command, health semantics, and the complete runtime
contract.
