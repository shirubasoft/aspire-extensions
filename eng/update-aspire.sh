#!/usr/bin/env bash
set -euo pipefail

script_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$script_path" rev-parse --show-toplevel)"
global_json="$repository_root/global.json"
packages_props="$repository_root/Directory.Packages.props"
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

if [[ -n "${GITHUB_PATH:-}" ]]; then
  printf '%s\n' "$cli_path" >> "$GITHUB_PATH"
fi

dotnet tool update Aspire.Cli \
  --tool-manifest "$repository_root/.config/dotnet-tools.json" \
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

  printf 'Updating %s\n' "$relative_apphost"
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
mapfile -t cli_changes < <(
  git -C "$repository_root" diff --name-only -- . ':(exclude).config/dotnet-tools.json'
)
untracked_files="$(git -C "$repository_root" ls-files --others --exclude-standard)"
if [[ -n "$untracked_files" ]]; then
  printf 'aspire update created files that this script does not keep:\n%s\n' "$untracked_files" >&2
  exit 1
fi

if [[ ${#cli_changes[@]} -eq 0 ]]; then
  exit 0
fi

cli_output_path="$scratch_path/cli-output"
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
