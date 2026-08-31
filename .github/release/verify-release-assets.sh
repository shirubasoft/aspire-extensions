#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 || -z "$1" || -z "$2" ]]; then
  echo "Usage: $0 <release-version> <package-id>" >&2
  exit 2
fi

release_version="$1"
package_id="$2"
artifact_path="artifacts/$package_id"
version_file="$artifact_path/release-version.txt"

if [[ ! -f "$version_file" ]]; then
  echo "The CI artifact does not contain $version_file." >&2
  exit 1
fi

artifact_version="$(<"$version_file")"
if [[ "$artifact_version" != "$release_version" ]]; then
  echo "CI packaged version $artifact_version, but semantic-release selected $release_version." >&2
  exit 1
fi

for extension in nupkg snupkg; do
  package_path="$artifact_path/$package_id.$release_version.$extension"
  if [[ ! -s "$package_path" ]]; then
    echo "The CI artifact does not contain $package_path." >&2
    exit 1
  fi
done
