#!/usr/bin/env bash
set -euo pipefail

script_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$script_path" rev-parse --show-toplevel)"
global_json="$repository_root/global.json"
packages_props="$repository_root/Directory.Packages.props"
tools_manifest="$repository_root/.config/dotnet-tools.json"
sdk_pin_pattern='("Aspire\.AppHost\.Sdk"[[:space:]]*:[[:space:]]*")([^"]+)(")'

if [[ -n "$(git -C "$repository_root" status --porcelain --untracked-files=all)" ]]; then
  echo "Commit or stash local changes first. The update restores files that aspire update rewrites." >&2
  exit 1
fi

previous_sdk_version="$(sed -nE "s/.*${sdk_pin_pattern}.*/\2/p" "$global_json")"
if [[ -z "$previous_sdk_version" ]]; then
  echo "global.json must pin Aspire.AppHost.Sdk under msbuild-sdks." >&2
  exit 1
fi

# Prints one "<name> <version>" line for each pin that an Aspire update can move.
aspire_version_pins() {
  printf 'Aspire.AppHost.Sdk %s\n' "$(sed -nE "s/.*${sdk_pin_pattern}.*/\2/p" "$global_json")"
  awk '
    /"aspire\.cli"[[:space:]]*:/ { in_cli = 1 }
    in_cli && /"version"[[:space:]]*:/ {
      sub(/.*"version"[[:space:]]*:[[:space:]]*"/, "")
      sub(/".*/, "")
      print "Aspire.Cli " $0
      exit
    }
  ' "$tools_manifest"
  sed -nE 's/.*<PackageVersion Include="(Aspire\.[^"]+)" Version="([^"]+)".*/\1 \2/p' "$packages_props"
}

start_log_group() {
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
    printf '::group::%s\n' "$1"
  else
    printf '%s\n' "$1"
  fi
}

end_log_group() {
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
    printf '::endgroup::\n'
  fi
}

log_warning() {
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
    printf '::warning::%s\n' "$1"
  else
    printf 'warning: %s\n' "$1" >&2
  fi
}

pins_before="$(aspire_version_pins)"
printf 'Current Aspire pins:\n'
sed 's/^/  /' <<<"$pins_before"

if [[ -n "${RUNNER_TEMP:-}" ]]; then
  scratch_path="$RUNNER_TEMP/aspire-update"
else
  scratch_path="$(mktemp -d)"
fi

report_path="${1:-${ASPIRE_UPDATE_REPORT:-$scratch_path/aspire-update-report.md}}"
cli_path="$scratch_path/cli"
config_path="$scratch_path/config"
logs_path="$scratch_path/logs"
mkdir -p "$cli_path" "$config_path" "$logs_path" "$(dirname "$report_path")"

if [[ -x "$cli_path/aspire" ]]; then
  dotnet tool update Aspire.Cli --tool-path "$cli_path"
else
  dotnet tool install Aspire.Cli --tool-path "$cli_path"
fi

aspire="$cli_path/aspire"
aspire_version="$($aspire --version)"
manifest_version="${aspire_version%%+*}"
printf 'Latest stable Aspire CLI: %s\n' "$manifest_version"

if [[ -n "${GITHUB_PATH:-}" ]]; then
  printf '%s\n' "$cli_path" >> "$GITHUB_PATH"
fi

dotnet tool update Aspire.Cli \
  --tool-manifest "$tools_manifest" \
  --version "$manifest_version"

contains_apphost_sdk() {
  local project_path="$1"

  if command -v rg >/dev/null 2>&1; then
    rg --quiet 'Aspire\.AppHost\.Sdk' "$project_path"
  else
    grep -q 'Aspire\.AppHost\.Sdk' "$project_path"
  fi
}

rewrite_file() {
  local file="$1"
  local expression="$2"

  sed -E "$expression" "$file" > "$file.tmp"
  mv "$file.tmp" "$file"
}

semantic_content() {
  sed -E \
    -e $'1s/^\xEF\xBB\xBF//' \
    -e 's|Aspire\.AppHost\.Sdk/[^"]+|Aspire.AppHost.Sdk|g' \
    "$1" | tr -d '[:space:]'
}

apphosts=()
while IFS= read -r -d '' candidate; do
  case "$candidate" in
    *.csproj)
      if contains_apphost_sdk "$candidate"; then
        apphosts+=("$candidate")
      fi
      ;;
    *)
      apphosts+=("$candidate")
      ;;
  esac
done < <(
  find "$repository_root" \
    -type d \( -name .git -o -name .aspire -o -name bin -o -name node_modules -o -name obj \) -prune -o \
    -type f \( -name '*.csproj' -o -name 'apphost.cs' -o -name 'apphost.ts' -o -name 'apphost.mts' \) \
    -print0 | sort -z
)

if [[ ${#apphosts[@]} -eq 0 ]]; then
  echo "No Aspire AppHosts found under $repository_root." >&2
  exit 1
fi

{
  printf 'This pull request updates every discovered Aspire AppHost and integration to the latest stable version.\n\n'
  printf -- "- Aspire CLI: \`%s\`\n" "$aspire_version"
  printf -- "- AppHosts processed: \`%s\`\n" "${#apphosts[@]}"
  printf "\nThe workflow records each \`aspire update\` result below.\n"
} > "$report_path"

for index in "${!apphosts[@]}"; do
  apphost="${apphosts[$index]}"
  relative_apphost="${apphost#"$repository_root"/}"
  command_log="$logs_path/$index.log"

  start_log_group "Updating $relative_apphost"
  update_exit_code=0
  (
    cd "$config_path"
    NO_COLOR=1 "$aspire" update \
      --apphost "$apphost" \
      --channel stable \
      --yes \
      --non-interactive \
      --nologo
  ) 2>&1 | tee "$command_log" || update_exit_code=$?
  end_log_group

  if [[ "$update_exit_code" -ne 0 ]]; then
    # aspire update reports a failed restore without NuGet's diagnostics.
    case "$apphost" in
      *.csproj | *.cs)
        printf 'Restoring %s to show the NuGet errors behind the failed update.\n' "$relative_apphost" >&2
        dotnet restore "$apphost" || true
        ;;
    esac
    exit "$update_exit_code"
  fi

  {
    printf '\n<details>\n'
    printf '<summary><code>%s</code></summary>\n\n' "$relative_apphost"
    printf '```text\n'
    sed -E $'s/\x1B\\[[0-9;]*[[:alpha:]]//g' "$command_log"
    printf '```\n\n'
    printf '</details>\n'
  } >> "$report_path"
done

# aspire update pins the AppHost SDK in each project and reformats every file it
# rewrites. Keep only its version decisions, in the files that own those pins.
keep_version_decisions() {
  local cli_changes=()
  local untracked_files=""
  local cli_output_path="$scratch_path/cli-output"
  local sdk_versions=()
  local sdk_version=""
  local package_pins=()
  local pin=""
  local pin_prefix=""
  local file=""

  mapfile -t cli_changes < <(
    git -C "$repository_root" diff --name-only -- . ':(exclude).config/dotnet-tools.json'
  )
  untracked_files="$(git -C "$repository_root" ls-files --others --exclude-standard)"
  if [[ -n "$untracked_files" ]]; then
    printf 'aspire update created files that this script does not keep:\n%s\n' "$untracked_files" >&2
    exit 1
  fi

  if [[ ${#cli_changes[@]} -eq 0 ]]; then
    return
  fi

  rm -rf "$cli_output_path"
  for file in "${cli_changes[@]}"; do
    mkdir -p "$cli_output_path/$(dirname "$file")"
    cp "$repository_root/$file" "$cli_output_path/$file"
  done

  mapfile -t sdk_versions < <(
    grep -rhoE 'Aspire\.AppHost\.Sdk/[^"]+' "$cli_output_path" | sed 's|.*/||' | sort -u
  )
  case ${#sdk_versions[@]} in
    0) sdk_version="$previous_sdk_version" ;;
    1) sdk_version="${sdk_versions[0]}" ;;
    *)
      printf 'aspire update selected different AppHost SDK versions: %s\n' "${sdk_versions[*]}" >&2
      exit 1
      ;;
  esac

  mapfile -t package_pins < <(
    grep -oE '<PackageVersion Include="Aspire\.[^"]+" Version="[^"]+"' \
      "$cli_output_path/Directory.Packages.props" 2>/dev/null
  )

  git -C "$repository_root" checkout -- "${cli_changes[@]}"

  for pin in "${package_pins[@]}"; do
    pin_prefix="${pin%Version=*}"
    rewrite_file "$packages_props" "s|${pin_prefix//./\\.}Version=\"[^\"]*\"|${pin}|"
  done

  for file in "${cli_changes[@]}"; do
    if [[ "$(semantic_content "$repository_root/$file")" != "$(semantic_content "$cli_output_path/$file")" ]]; then
      printf 'aspire update changed %s beyond package and SDK versions:\n' "$file" >&2
      diff -u "$repository_root/$file" "$cli_output_path/$file" >&2 || true
      exit 1
    fi
  done

  if [[ "$sdk_version" != "$previous_sdk_version" ]]; then
    rewrite_file "$global_json" "s/${sdk_pin_pattern}/\\1${sdk_version}\\3/"
    # Aspire packages that no AppHost references, such as Aspire.Hosting.Testing,
    # move with the SDK when they were pinned to its previous version.
    rewrite_file "$packages_props" \
      "s|(<PackageVersion Include=\"Aspire\\.[^\"]+\" Version=\")${previous_sdk_version//./\\.}\"|\\1${sdk_version}\"|"
  fi
}

# Logs whether the repository needed an update, so a run that changes nothing is
# distinguishable from one that failed to detect a release.
report_update_decision() {
  local pins_after=""
  local changes=""
  local sdk_version=""
  local name=""
  local version=""
  local updated=false

  pins_after="$(aspire_version_pins)"
  changes="$(
    LC_ALL=C join -j 1 <(LC_ALL=C sort <<<"$pins_before") <(LC_ALL=C sort <<<"$pins_after") \
      | awk '$2 != $3 { printf "%s %s -> %s\n", $1, $2, $3 }'
  )"
  if [[ -n "$(git -C "$repository_root" status --porcelain --untracked-files=all)" ]]; then
    updated=true
  fi

  if [[ "$updated" == true ]]; then
    printf 'Aspire update needed: the latest stable release is %s.\n' "$manifest_version"
    printf 'Changed pins:\n'
    sed 's/^/  /' <<<"${changes:-(none; see the git diff)}"
    {
      printf '\n## Version changes\n\n'
      while IFS= read -r change; do
        [[ -n "$change" ]] && printf -- "- \`%s\`\n" "$change"
      done <<<"$changes"
    } >> "$report_path"
  else
    printf 'No Aspire update needed: every pin already matches the latest stable release %s.\n' "$manifest_version"
  fi

  # Preview-only packages have no stable release for aspire update to select.
  sdk_version="$(sed -nE "s/.*${sdk_pin_pattern}.*/\2/p" "$global_json")"
  while read -r name version; do
    if [[ "$version" == *-* && "${version%%-*}" != "$sdk_version" ]]; then
      log_warning "$name $version is a preview pin behind Aspire $sdk_version. Move it to the matching preview release."
    fi
  done <<<"$pins_after"

  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf 'updated=%s\n' "$updated" >> "$GITHUB_OUTPUT"
    printf 'aspire_version=%s\n' "$manifest_version" >> "$GITHUB_OUTPUT"
  fi

  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      printf '## Aspire update\n\n'
      printf -- "- Latest stable Aspire: \`%s\`\n" "$manifest_version"
      if [[ "$updated" == true ]]; then
        printf -- '- Result: update needed\n'
        while IFS= read -r change; do
          [[ -n "$change" ]] && printf -- "  - \`%s\`\n" "$change"
        done <<<"$changes"
      else
        printf -- '- Result: no update needed\n'
      fi
    } >> "$GITHUB_STEP_SUMMARY"
  fi
}

keep_version_decisions
report_update_decision
