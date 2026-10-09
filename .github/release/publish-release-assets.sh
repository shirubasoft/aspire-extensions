#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 || -z "$1" || -z "$2" || -z "$3" ]]; then
  echo "Usage: $0 <release-version> <artifact-directory-name> <package-specs>" >&2
  exit 2
fi
if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "NUGET_API_KEY is required." >&2
  exit 1
fi

release_version="$1"
artifact_directory_name="$2"
package_specs_value="$3"
artifact_path="${RELEASE_ARTIFACTS_DIR:-$PWD/artifacts/$artifact_directory_name}"
if [[ "$artifact_path" != /* ]]; then
  echo "RELEASE_ARTIFACTS_DIR must be an absolute path." >&2
  exit 1
fi

config_file="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/NuGet.Config"

IFS=';' read -r -a package_specs <<< "$package_specs_value"
for package_spec in "${package_specs[@]}"; do
  package_id="${package_spec%:symbols}"
  dotnet nuget push "$artifact_path/$package_id.$release_version.nupkg" \
    --configfile "$config_file" \
    --source https://api.nuget.org/v3/index.json \
    --api-key "$NUGET_API_KEY" \
    --symbol-source https://api.nuget.org/v3/index.json \
    --symbol-api-key "$NUGET_API_KEY" \
    --skip-duplicate
done
