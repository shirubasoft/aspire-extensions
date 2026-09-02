#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || -z "$1" ]]; then
  echo "Usage: eng/build-extension.sh <extension-path> [--package-version <version>]" >&2
  exit 2
fi

extension_path="${1%/}"
shift
package_version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --package-version)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--package-version requires a value." >&2
        exit 2
      fi
      package_version="$2"
      shift 2
      ;;
    *)
      echo "Usage: eng/build-extension.sh <extension-path> [--package-version <version>]" >&2
      exit 2
      ;;
  esac
done

extension_name="$(basename "$extension_path")"
solution="$extension_path/$extension_name.slnx"
package_project="$extension_path/src/$extension_name/$extension_name.csproj"
pack_projects_file="$extension_path/pack-projects.txt"

if [[ ! -f "$solution" || ! -f "$package_project" ]]; then
  echo "The extension does not follow the repository layout: $extension_path" >&2
  exit 1
fi

package_id="$(dotnet msbuild "$package_project" -getProperty:PackageId -nologo)"
if [[ -z "$package_id" ]]; then
  echo "The extension package project does not define PackageId: $package_project" >&2
  exit 1
fi
artifact_path="artifacts/$package_id"

version_arguments=()
if [[ -n "$package_version" ]]; then
  version_arguments+=("-p:Version=$package_version")
fi

package_projects=("$package_project")
if [[ -f "$pack_projects_file" ]]; then
  package_projects=()
  while IFS= read -r relative_project || [[ -n "$relative_project" ]]; do
    relative_project="${relative_project%$'\r'}"
    if [[ -z "$relative_project" || "$relative_project" == \#* ]]; then
      continue
    fi
    case "$relative_project" in
      /*|../*|*/../*|*/..)
        echo "Pack project paths must stay within the extension: $relative_project" >&2
        exit 1
        ;;
    esac
    project="$extension_path/$relative_project"
    if [[ ! -f "$project" ]]; then
      echo "Pack project does not exist: $project" >&2
      exit 1
    fi
    package_projects+=("$project")
  done < "$pack_projects_file"
  if [[ ${#package_projects[@]} -eq 0 ]]; then
    echo "The pack project list is empty: $pack_projects_file" >&2
    exit 1
  fi
fi

dotnet restore "$solution"
dotnet format "$solution" --verify-no-changes --no-restore
dotnet build "$solution" --configuration Release --no-restore "${version_arguments[@]}"
dotnet test "$solution" --configuration Release --no-build --no-restore "${version_arguments[@]}"
for project in "${package_projects[@]}"; do
  dotnet pack "$project" \
    --configuration Release --no-build --no-restore \
    --output "$artifact_path" \
    "${version_arguments[@]}"
done
