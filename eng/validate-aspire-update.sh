#!/usr/bin/env bash
set -uo pipefail

script_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$script_path" rev-parse --show-toplevel)"
report_path="${1:?Usage: eng/validate-aspire-update.sh <report-path> [log-path]}"
log_path="${2:-${RUNNER_TEMP:-$(mktemp -d)}/aspire-update-build.log}"
warnings_path="${log_path}.warnings"
build_command="${ASPIRE_UPDATE_BUILD_COMMAND:-$repository_root/build.sh}"

mkdir -p "$(dirname "$log_path")"

(
  cd "$repository_root" || exit 1
  "$build_command"
) 2>&1 | tee "$log_path"
build_exit_code="${PIPESTATUS[0]}"

grep -E '(^|:) warning( [[:alnum:]]+)?[[:space:]]*:' "$log_path" \
  | sed -E $'s/\x1B\\[[0-9;]*[[:alpha:]]//g' \
  | sort -u > "$warnings_path"
read -r warning_count _ < <(wc -l "$warnings_path")

{
  printf '\n## Validation\n\n'
  if [[ "$build_exit_code" -eq 0 ]]; then
    if [[ "$warning_count" -eq 0 ]]; then
      printf -- "- Result: \`passed with no warnings\`\n"
    else
      printf -- "- Result: \`passed with %s warning(s)\`\n" "$warning_count"
    fi
  else
    printf -- "- Result: \`failed with exit code %s\`\n" "$build_exit_code"
  fi
  printf -- "- Command: \`./build.sh\`\n"

  if [[ "$warning_count" -gt 0 ]]; then
    printf '\n<details>\n'
    printf '<summary>Warnings (%s)</summary>\n\n' "$warning_count"
    printf '```text\n'
    sed "s|$repository_root/||g" "$warnings_path"
    printf '```\n\n'
    printf '</details>\n'
  fi

  if [[ "$build_exit_code" -ne 0 && -s "$log_path" ]]; then
    printf '\n<details>\n'
    printf '<summary>Last 80 lines of the failed build</summary>\n\n'
    printf '```text\n'
    tail -n 80 "$log_path" | sed -E $'s/\x1B\\[[0-9;]*[[:alpha:]]//g'
    printf '```\n\n'
    printf '</details>\n'
  fi
} >> "$report_path"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  printf 'exit_code=%s\n' "$build_exit_code" >> "$GITHUB_OUTPUT"
  printf 'warning_count=%s\n' "$warning_count" >> "$GITHUB_OUTPUT"
fi

exit "$build_exit_code"
