#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 || -z "$1" || -z "$2" || -z "$3" ]]; then
  echo "Usage: $0 <extension-path> <package-id> <coverage-output-path>" >&2
  exit 2
fi

extension_path="${1%/}"
package_id="$2"
coverage_path_input="$3"
extension_name="$(basename "$extension_path")"
coverage_projects_file="$extension_path/coverage-projects.txt"
mkdir -p "$coverage_path_input"
coverage_path="$(cd -- "$coverage_path_input" && pwd -P)"

coverage_projects=("tests/$extension_name.Tests/$extension_name.Tests.csproj|$package_id")
if [[ -f "$coverage_projects_file" ]]; then
  coverage_projects=()
  while IFS= read -r coverage_project || [[ -n "$coverage_project" ]]; do
    coverage_project="${coverage_project%$'\r'}"
    if [[ -z "$coverage_project" || "$coverage_project" == \#* ]]; then
      continue
    fi
    coverage_projects+=("$coverage_project")
  done < "$coverage_projects_file"
  if [[ ${#coverage_projects[@]} -eq 0 ]]; then
    echo "The coverage project list is empty: $coverage_projects_file" >&2
    exit 1
  fi
fi

index=0
for coverage_project in "${coverage_projects[@]}"; do
  IFS='|' read -r relative_project assembly_filter extra <<< "$coverage_project"
  if [[ -z "$relative_project" || -z "$assembly_filter" || -n "$extra" ]]; then
    echo "Coverage entries must use <project>|<assembly-filter>: $coverage_project" >&2
    exit 1
  fi
  case "$relative_project" in
    /*|../*|*/../*|*/..)
      echo "Coverage project paths must stay within the extension: $relative_project" >&2
      exit 1
      ;;
  esac
  project="$extension_path/$relative_project"
  if [[ ! -f "$project" ]]; then
    echo "Coverage project does not exist: $project" >&2
    exit 1
  fi
  index=$((index + 1))
  report_path="$coverage_path/extension-$index.opencover.xml"
  dotnet test "$project" \
    --configuration Release --no-build --no-restore \
    -p:CollectCoverage=true \
    -p:CoverletOutput="$report_path" \
    -p:CoverletOutputFormat=opencover \
    "-p:Include=[$assembly_filter]*"
  if [[ ! -s "$report_path" ]]; then
    echo "Coverage project did not create $report_path" >&2
    exit 1
  fi
done
