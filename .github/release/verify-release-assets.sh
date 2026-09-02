#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 || -z "$1" || -z "$2" || -z "$3" ]]; then
  echo "Usage: $0 <release-version> <artifact-directory-name> <package-specs>" >&2
  exit 2
fi

release_version="$1"
artifact_directory_name="$2"
package_specs_value="$3"
artifact_path="artifacts/$artifact_directory_name"
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

IFS=';' read -r -a package_specs <<< "$package_specs_value"
for package_spec in "${package_specs[@]}"; do
  package_id="${package_spec%:symbols}"
  package_path="$artifact_path/$package_id.$release_version.nupkg"
  if [[ ! -s "$package_path" ]]; then
    echo "The CI artifact does not contain $package_path." >&2
    exit 1
  fi
  if [[ "$package_spec" == *:symbols ]]; then
    symbol_path="$artifact_path/$package_id.$release_version.snupkg"
    if [[ ! -s "$symbol_path" ]]; then
      echo "The CI artifact does not contain $symbol_path." >&2
      exit 1
    fi
  fi
done
