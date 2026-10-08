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
repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
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
  IFS='|' read -r relative_project assembly_filter line_threshold extra <<< "$coverage_project"
  if [[ -z "$relative_project" || -z "$assembly_filter" || -n "$extra" ]]; then
    echo "Coverage entries must use <project>|<assembly-filter>[|<line-threshold>]: $coverage_project" >&2
    exit 1
  fi
  if [[ -n "$line_threshold" ]] &&
    { [[ ! "$line_threshold" =~ ^[0-9]+$ ]] || (( 10#$line_threshold > 100 )); }; then
    echo "Coverage line thresholds must be whole percentages from 0 through 100: $coverage_project" >&2
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
  test_assembly="$(dotnet msbuild "$project" -getProperty:TargetPath -p:Configuration=Release -nologo)"
  if [[ ! -f "$test_assembly" ]]; then
    echo "Coverage project has not been built: $test_assembly" >&2
    exit 1
  fi
  # Deterministic builds record sources under /_/. Map that root back to the repository so Coverlet
  # finds the sources it instruments and reports real file paths.
  source_mapping_path="$coverage_path/extension-$index.source-roots.txt"
  printf '%s|%s=/_/\n' "$project" "$repository_root/" > "$source_mapping_path"
  threshold_arguments=()
  if [[ -n "$line_threshold" ]]; then
    threshold_arguments+=(--threshold "$line_threshold" --threshold-type line --threshold-stat total)
  fi
  dotnet coverlet "$test_assembly" \
    --target dotnet \
    --targetargs "test --project \"$project\" --configuration Release --no-build --no-restore" \
    --format opencover \
    --output "$report_path" \
    --include "[$assembly_filter]*" \
    --source-mapping-file "$source_mapping_path" \
    "${threshold_arguments[@]}"
  if [[ ! -s "$report_path" ]]; then
    echo "Coverage project did not create $report_path" >&2
    exit 1
  fi
done
