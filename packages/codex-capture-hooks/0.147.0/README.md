# Overmind wake hooks for Codex CLI 0.147.0

Review this directory, verify `codex --version` reports exactly 0.147.0, then
run `./install.sh`. Pass a Codex home as its sole argument when it is not
`$CODEX_HOME` or `~/.codex`. The installer refuses to overwrite an existing
`hooks.json`; merge the event entries explicitly if other hooks are installed.

For an installation already owned by this exact package, run `./upgrade.sh`.
Both operations are operator actions. The capture runtime never edits Codex
configuration or updates these files.

The command consumes and discards hook stdin, emits nothing, sends no payload,
and always returns success. It only requests an early scheduled catch-up scan
over `http://127.0.0.1:43191/wake`; it never contacts the central server.
