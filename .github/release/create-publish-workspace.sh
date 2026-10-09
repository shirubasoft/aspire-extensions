#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 || ! "$3" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Usage: $0 <source-repository> <new-publish-directory> <head-sha> <repository-url>" >&2
  exit 2
fi

source_repository="$1"
publish_directory="$2"
head_sha="$3"
repository_url="$4"
if ! git -C "$source_repository" merge-base --is-ancestor "$head_sha" refs/remotes/origin/main; then
  echo "Commit $head_sha is not on main. Refusing to publish it." >&2
  exit 1
fi

# Clone metadata into a new directory; never materialize files from head_sha.
# A separate clone also keeps source-repository Git config out of credentialed commands.
git clone --no-checkout --no-hardlinks -- "$source_repository" "$publish_directory"
git -C "$publish_directory" remote set-url origin "$repository_url"
git -C "$publish_directory" update-ref refs/heads/main "$head_sha"
git -C "$publish_directory" symbolic-ref HEAD refs/heads/main
