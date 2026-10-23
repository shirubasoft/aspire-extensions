#!/usr/bin/env bash
# Runs every extension CI workflow on the Aspire update branch and merges the pull request only when
# all of them pass. Pull requests created with the workflow token do not trigger pull_request CI, so
# this script dispatches the same workflows explicitly.
set -euo pipefail

pull_request="${1:?Usage: eng/merge-aspire-update.sh <pull-request-number>}"
script_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$script_path" rev-parse --show-toplevel)"
poll_seconds="${ASPIRE_UPDATE_POLL_SECONDS:-30}"

read -r branch head_sha < <(
  gh pr view "$pull_request" --json headRefName,headRefOid --jq '"\(.headRefName) \(.headRefOid)"'
)

workflows=()
for workflow in "$repository_root"/.github/workflows/*-ci.yml; do
  name="$(basename "$workflow")"
  if [[ "$name" != _* ]]; then
    workflows+=("$name")
  fi
done

printf 'Running %s extension CI workflows on %s at %s.\n' "${#workflows[@]}" "$branch" "$head_sha"
for workflow in "${workflows[@]}"; do
  gh workflow run "$workflow" --ref "$branch"
done

dispatched_run() {
  gh run list --workflow "$1" --branch "$branch" --event workflow_dispatch --limit 20 \
    --json databaseId,headSha --jq "map(select(.headSha == \"$head_sha\"))[0].databaseId // empty"
}

failed=()
for workflow in "${workflows[@]}"; do
  run_id=""
  for _ in {1..20}; do
    run_id="$(dispatched_run "$workflow")"
    if [[ -n "$run_id" ]]; then
      break
    fi
    sleep "$poll_seconds"
  done

  if [[ -z "$run_id" ]]; then
    printf '%s did not start a run for %s.\n' "$workflow" "$head_sha" >&2
    failed+=("$workflow")
    continue
  fi

  if gh run watch "$run_id" --exit-status --interval "$poll_seconds" >/dev/null; then
    printf '%s passed: run %s.\n' "$workflow" "$run_id"
  else
    printf '%s failed: run %s.\n' "$workflow" "$run_id" >&2
    failed+=("$workflow")
  fi
done

if [[ ${#failed[@]} -gt 0 ]]; then
  printf 'Leaving pull request #%s open because %s failed.\n' "$pull_request" "${failed[*]}" >&2
  exit 1
fi

gh pr merge "$pull_request" --merge --delete-branch --match-head-commit "$head_sha"
printf 'Merged pull request #%s after every extension CI passed.\n' "$pull_request"
