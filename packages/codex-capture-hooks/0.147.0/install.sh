#!/bin/sh
set -eu
codex_home=${1:-"${CODEX_HOME:-$HOME/.codex}"}
package_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
test "$(codex --version)" = "codex-cli 0.147.0" || {
  printf '%s\n' 'This hook package requires codex-cli 0.147.0.' >&2
  exit 2
}
test ! -e "$codex_home/hooks.json" || {
  printf '%s\n' "$codex_home/hooks.json already exists; merge it explicitly." >&2
  exit 2
}
install -d "$codex_home" "$HOME/.local/bin"
install -m 0755 "$package_dir/overmind-codex-wake-0.147.0" \
  "$HOME/.local/bin/overmind-codex-wake-0.147.0"
install -m 0644 "$package_dir/hooks.json" "$codex_home/hooks.json"
