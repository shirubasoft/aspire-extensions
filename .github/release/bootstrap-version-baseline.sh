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

# Walk first parents from HEAD, so a merged branch is introduced by its merge commit, whose first
# parent already contains every mainline commit that landed before the merge. The newest commit
# whose first parent lacks the folder starts the folder's current lifetime, so an extension that
# was removed and added again does not inherit history from its earlier lifetime.
introduction=""
for commit in $(git rev-list --first-parent HEAD -- "$extension_path"); do
  if ! git cat-file -e "$commit^:$extension_path" 2>/dev/null; then
    introduction="$commit"
    break
  fi
done

if [[ -z "$introduction" ]]; then
  echo "No commit reachable from HEAD introduces $extension_path." >&2
  exit 1
fi

# semantic-release analyzes only commits after the baseline tag, and the root commit has no
# parent to tag, so an extension created by the root commit needs a manually tagged release.
if ! baseline="$(git rev-parse --verify --quiet "$introduction^")"; then
  echo "The root commit created $extension_path. Tag $tag_prefix-v<version> manually." >&2
  exit 1
fi

git tag "$tag_prefix-v$initial_version" "$baseline"
printf 'Created %s-v%s at %s, the commit before %s appeared.\n' \
  "$tag_prefix" "$initial_version" "$baseline" "$extension_path"
