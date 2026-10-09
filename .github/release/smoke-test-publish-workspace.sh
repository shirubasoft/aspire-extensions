#!/usr/bin/env bash
set -euo pipefail

tooling_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
publish_directory="$(dirname "$GITHUB_WORKSPACE")/release-publish"
if [[ -e "$publish_directory" || -L "$publish_directory" ]]; then
  echo "Smoke-test publish directory already exists: $publish_directory" >&2
  exit 1
fi

# Use production's sibling directory so the guard inspects the real runner ancestors.
head_sha="$(git -C "$GITHUB_WORKSPACE" rev-parse refs/remotes/origin/main)"
bash "$tooling_directory/create-publish-workspace.sh" \
  "$GITHUB_WORKSPACE" "$publish_directory" "$head_sha" "https://github.com/$GITHUB_REPOSITORY.git"
trap 'rm -rf -- "$publish_directory"' EXIT
cd "$publish_directory"
node "$tooling_directory/assert-release-workspace-safe.mjs"
echo "Publish workspace guard passed in $PWD."
