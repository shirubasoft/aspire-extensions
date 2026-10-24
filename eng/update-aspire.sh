#!/usr/bin/env bash
set -euo pipefail

script_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$script_path" rev-parse --show-toplevel)"
global_json="$repository_root/global.json"
packages_props="$repository_root/Directory.Packages.props"
tools_manifest="$repository_root/.config/dotnet-tools.json"
# shellcheck source=eng/typescript-apphosts.sh
source "$script_path/typescript-apphosts.sh"
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
  local config=""
  while IFS= read -r config; do
    sed -nE "s|.*\"(Aspire\\.[^\"]+)\"[[:space:]]*:[[:space:]]*\"([^\"]+)\".*|${config}:\\1 \\2|p" \
      "$repository_root/$config"
  done < <(git -C "$repository_root" ls-files -- '*aspire.config.json')
  local apphost=""
  while IFS= read -r apphost; do
    package_runtime_dependencies "$repository_root/$apphost/package.json" | sed "s|^|$apphost/package.json:|"
  done < <(typescript_apphost_directories "$repository_root")
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
    case "$file" in
      *aspire.config.json)
        # aspire update also pins the SDK and replaces project references with the latest
        # published package. A TypeScript AppHost keeps only its Aspire package versions.
        keep_apphost_config_pins "$file" "$cli_output_path/$file"
        continue
        ;;
    esac

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

keep_apphost_config_pins() {
  local file="$1"
  local cli_output_file="$2"
  local name=""
  local version=""

  while read -r name version; do
    rewrite_file "$repository_root/$file" \
      "s|(\"${name//./\\.}\"[[:space:]]*:[[:space:]]*\")[^\"]*\"|\\1${version}\"|"
  done < <(
    sed -nE 's|.*"(Aspire\.[^"]+)"[[:space:]]*:[[:space:]]*"([^"]+)".*|\1 \2|p' "$cli_output_file"
  )
}

# aspire update moves the preview-only packages that an AppHost reaches, such as Aspire.Hosting.Dotnet.
# Move each preview pin it leaves behind, such as Aspire.Hosting.Kubernetes, to the newest preview of the
# AppHost SDK's release, and fail when that release has none. --exact-match reads package metadata
# rather than the feed's search index, so a release is visible as soon as the feed registers it.
move_preview_pins() {
  local sdk_version=""
  local name=""
  local version=""
  local preview=""

  sdk_version="$(sed -nE "s/.*${sdk_pin_pattern}.*/\2/p" "$global_json")"
  while read -r name version; do
    if [[ "${version%%-*}" == "$sdk_version" ]]; then
      continue
    fi

    preview="$(
      dotnet package search "$name" --exact-match --prerelease --format json \
        | node -e '
          const [release] = process.argv.slice(1);
          const result = JSON.parse(require("fs").readFileSync(0, "utf8"));
          const problems = [...result.problems, ...result.searchResult.flatMap(source => source.problems ?? [])];
          if (problems.length > 0) {
            console.error(problems.map(problem => problem.text).join("\n"));
            process.exit(1);
          }
          const versions = result.searchResult.flatMap(source => source.packages.map(p => p.version));
          console.log(versions.filter(v => v.startsWith(release + "-")).join("\n"));
        ' "$sdk_version" \
        | sort -V | tail -n 1
    )"
    if [[ -z "$preview" ]]; then
      printf '%s has no preview release for Aspire %s.\n' "$name" "$sdk_version" >&2
      exit 1
    fi

    printf 'Moving %s from %s to %s.\n' "$name" "$version" "$preview"
    rewrite_file "$packages_props" \
      "s|(<PackageVersion Include=\"${name//./\\.}\" Version=\")[^\"]*\"|\\1${preview}\"|"
  done < <(sed -nE 's/.*<PackageVersion Include="(Aspire\.[^"]+)" Version="([^"]+-[^"]+)".*/\1 \2/p' "$packages_props")
}

major_version() {
  sed -nE 's/^[^0-9]*([0-9]+).*/\1/p' <<<"$1"
}

# A TypeScript AppHost runs the SDK that aspire restore generates, and that SDK imports runtime
# packages such as vscode-jsonrpc. Dependabot proposes minor and patch updates for them; move a
# TypeScript AppHost to a new major only when Aspire's own TypeScript template requires it.
align_typescript_apphost_dependencies() {
  local apphosts=()
  local template_path="$scratch_path/typescript-template"
  local name=""
  local range=""
  local apphost=""
  local current=""
  local version=""

  mapfile -t apphosts < <(typescript_apphost_directories "$repository_root")
  if [[ ${#apphosts[@]} -eq 0 ]]; then
    return
  fi

  rm -rf "$template_path"
  (
    cd "$config_path"
    NO_COLOR=1 "$aspire" new aspire-ts-empty \
      --name aspire-typescript-template \
      --output "$template_path" \
      --channel stable \
      --suppress-agent-init \
      --skill-locations none \
      --non-interactive \
      --nologo >/dev/null
  )

  while read -r name range; do
    printf "Aspire's TypeScript template requires %s %s.\n" "$name" "$range"
    for apphost in "${apphosts[@]}"; do
      current="$(package_runtime_dependencies "$repository_root/$apphost/package.json" | sed -n "s|^$name ||p")"
      if [[ -z "$current" || "$(major_version "$current")" == "$(major_version "$range")" ]]; then
        continue
      fi

      version="$(npm view "$name@$range" version --json | node -e '
        const versions = [JSON.parse(require("fs").readFileSync(0, "utf8"))].flat();
        console.log(versions.at(-1));
      ')"
      printf 'Moving %s in %s from %s to %s.\n' "$name" "$apphost" "$current" "$version"
      npm install --prefix "$repository_root/$apphost" \
        --package-lock-only --save-exact --ignore-scripts --no-audit --no-fund \
        "$name@$version"
    done
  done < <(package_runtime_dependencies "$template_path/package.json")
}

# Logs whether the repository needed an update, so a run that changes nothing is
# distinguishable from one that failed to detect a release.
report_update_decision() {
  local pins_after=""
  local changes=""
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
move_preview_pins
align_typescript_apphost_dependencies
report_update_decision
