#!/usr/bin/env bash
set -euo pipefail

root_dir="${OVERMIND_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
manifest="$root_dir/reference/csharp-sdk.manifest"
packages="$root_dir/Directory.Packages.props"
target="$root_dir/reference/csharp-sdk"
marker="$target/.overmind-reference"

fail() {
  echo "sdk-reference: $*" >&2
  exit 1
}

[[ -f "$manifest" ]] || fail "missing manifest: $manifest"

package_version=$(sed -n 's/^PACKAGE_VERSION=//p' "$manifest")
repository=$(sed -n 's/^REPOSITORY=//p' "$manifest")
commit=$(sed -n 's/^COMMIT=//p' "$manifest")
[[ -n "$package_version" && -n "$repository" && "$commit" =~ ^[0-9a-f]{40}$ ]] || \
  fail "manifest must define PACKAGE_VERSION, REPOSITORY, and a 40-character COMMIT"

nuget_version=$(sed -n 's/.*PackageVersion Include="ModelContextProtocol" Version="\([^"]*\)".*/\1/p' "$packages")
[[ -n "$nuget_version" ]] || fail "cannot find ModelContextProtocol version in $packages"
[[ "$nuget_version" == "$package_version" ]] || \
  fail "version mismatch: NuGet ModelContextProtocol is $nuget_version but SDK reference manifest is $package_version"

tree_digest() {
  local directory=$1
  (
    cd "$directory"
    find . -type f ! -name .overmind-reference -print0 \
      | sort -z \
      | xargs -0 -r sha256sum \
      | sha256sum \
      | cut -d' ' -f1
  )
}

if [[ -e "$target" ]]; then
  [[ -d "$target" && -f "$marker" ]] || \
    fail "$target is not a provisioned checkout; move it aside and rerun make sdk-reference"
  installed_commit=$(sed -n 's/^COMMIT=//p' "$marker")
  installed_digest=$(sed -n 's/^DIGEST=//p' "$marker")
  current_digest=$(tree_digest "$target")
  [[ -n "$installed_digest" && "$current_digest" == "$installed_digest" ]] || \
    fail "$target has local modifications; preserving them (move or revert the changes before retrying)"
  if [[ "$installed_commit" == "$commit" ]]; then
    echo "sdk-reference: ready at $commit" >&2
    exit 0
  fi
fi

mkdir -p "$root_dir/reference"
tmp_dir=$(mktemp -d "$root_dir/reference/.csharp-sdk.XXXXXX")
cleanup() { rm -rf "$tmp_dir"; }
trap cleanup EXIT

git -C "$tmp_dir" init -q
git -C "$tmp_dir" remote add origin "$repository"
if ! fetch_output=$(git -C "$tmp_dir" fetch --quiet --depth=1 origin "$commit" 2>&1); then
  fail "could not fetch pinned SDK commit $commit from $repository. Check network access and repository availability, then rerun 'make sdk-reference'. Git said: $fetch_output"
fi
git -C "$tmp_dir" checkout -q --detach FETCH_HEAD
rm -rf "$tmp_dir/.git"
digest=$(tree_digest "$tmp_dir")
printf 'COMMIT=%s\nDIGEST=%s\n' "$commit" "$digest" > "$tmp_dir/.overmind-reference"

rm -rf "$target"
mv "$tmp_dir" "$target"
trap - EXIT
echo "sdk-reference: provisioned $repository at $commit" >&2
