#!/usr/bin/env bash
set -euo pipefail

extension_path=""
package_version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --extension)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--extension requires a path." >&2
        exit 2
      fi
      extension_path="$2"
      shift 2
      ;;
    --package-version)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--package-version requires a value." >&2
        exit 2
      fi
      package_version="$2"
      shift 2
      ;;
    *)
      echo "Usage: ./build.sh [--extension <path>] [--package-version <version>]" >&2
      exit 2
      ;;
  esac
done

dotnet tool restore
dotnet restore Aspire.Extensions.Tools.slnx
dotnet format Aspire.Extensions.Tools.slnx --verify-no-changes --no-restore
dotnet build Aspire.Extensions.Tools.slnx --configuration Release --no-restore
dotnet test Aspire.Extensions.Tools.slnx --configuration Release --no-build --no-restore

if [[ -n "$extension_path" ]]; then
  extension_paths=("$extension_path")
else
  mapfile -t extension_paths < <(find extensions -mindepth 1 -maxdepth 1 -type d -name 'Shirubasoft.Aspire.Extensions.*' | sort)
fi

for path in "${extension_paths[@]}"; do
  arguments=("$path")
  if [[ -n "$package_version" ]]; then
    arguments+=(--package-version "$package_version")
  fi
  eng/build-extension.sh "${arguments[@]}"
done
