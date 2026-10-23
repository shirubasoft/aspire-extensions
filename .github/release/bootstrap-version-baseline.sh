#!/usr/bin/env bash
# Tags an extension's initial version when it has no release tag yet. The tag marks the mainline
# commit before the extension folder first appeared, so semantic-release analyzes only the
# extension's own history rather than unrelated breaking changes from earlier mainline history.
set -euo pipefail

usage="Usage: bootstrap-version-baseline.sh <tag-prefix> <initial-version> <extension-path>"
tag_prefix="${1:?$usage}"
initial_version="${2:?$usage}"
extension_path="${3:?$usage}"

if git describe --tags --abbrev=0 --match "$tag_prefix-v[0-9]*" >/dev/null 2>&1; then
  exit 0
fi

if [[ "$(git rev-parse --is-shallow-repository)" == true ]]; then
  echo "The version baseline needs full history. Fetch with fetch-depth: 0." >&2
  exit 1
fi

# Follow first parents so a merged branch is introduced by its merge commit, whose first parent
# already contains every mainline commit that landed before the merge.
# Read the whole list rather than piping to head, which can end rev-list with SIGPIPE under pipefail.
introducing_commits="$(git rev-list --first-parent --reverse HEAD -- "$extension_path")"
first_commit="${introducing_commits%%$'\n'*}"
if [[ -z "$first_commit" ]]; then
  echo "No commit reachable from HEAD touches $extension_path." >&2
  exit 1
fi

# An extension created by the root commit keeps the root commit as its baseline.
baseline="$(git rev-parse --verify --quiet "$first_commit^" || printf '%s' "$first_commit")"
git tag "$tag_prefix-v$initial_version" "$baseline"
printf 'Created %s-v%s at %s, the commit before %s first appeared.\n' \
  "$tag_prefix" "$initial_version" "$baseline" "$extension_path"
