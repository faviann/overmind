# Issue tracker: GitHub

Issues and PRDs for this repo live in GitHub Issues for `faviann/overmind-tasks`. Use the `gh` CLI for all operations and pass `--repo faviann/overmind-tasks` explicitly.

## Conventions

- **Create an issue**: `gh issue create --repo faviann/overmind-tasks --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --repo faviann/overmind-tasks --comments`, filtering comments by `jq` and also fetching labels.
- **List issues**: `gh issue list --repo faviann/overmind-tasks --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Comment on an issue**: `gh issue comment <number> --repo faviann/overmind-tasks --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --repo faviann/overmind-tasks --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --repo faviann/overmind-tasks --comment "..."`

Do not use the deprecated `faviann/cortex-tasks` repo.

## When a skill says "publish to the issue tracker"

Create a GitHub issue in `faviann/overmind-tasks`.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --repo faviann/overmind-tasks --comments`.
