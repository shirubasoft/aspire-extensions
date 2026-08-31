#!/usr/bin/env bash
set -euo pipefail

script_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$script_path" rev-parse --show-toplevel)"

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

dotnet tool update Aspire.Cli \
  --tool-manifest "$repository_root/.config/dotnet-tools.json" \
  --version "$manifest_version"

apphosts=()
while IFS= read -r -d '' candidate; do
  case "$candidate" in
    *.csproj)
      if rg --quiet 'Aspire\.AppHost\.Sdk' "$candidate"; then
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
  printf -- "- Validation: \`./build.sh\`\n\n"
  printf "The workflow records each \`aspire update\` result below.\n"
} > "$report_path"

for index in "${!apphosts[@]}"; do
  apphost="${apphosts[$index]}"
  relative_apphost="${apphost#"$repository_root"/}"
  command_log="$logs_path/$index.log"

  printf 'Updating %s\n' "$relative_apphost"
  (
    cd "$config_path"
    NO_COLOR=1 "$aspire" update \
      --apphost "$apphost" \
      --channel stable \
      --yes \
      --non-interactive \
      --nologo
  ) 2>&1 | tee "$command_log"

  {
    printf '\n<details>\n'
    printf '<summary><code>%s</code></summary>\n\n' "$relative_apphost"
    printf '```text\n'
    sed -E $'s/\x1B\\[[0-9;]*[[:alpha:]]//g' "$command_log"
    printf '```\n\n'
    printf '</details>\n'
  } >> "$report_path"
done
