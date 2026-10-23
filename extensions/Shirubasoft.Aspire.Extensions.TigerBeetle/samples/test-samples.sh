#!/usr/bin/env bash
# Builds the TypeScript sample, runs both samples against a container runtime, and verifies the
# published Docker Compose project. Run from any directory; paths resolve from this script.
set -euo pipefail

samples_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$samples_path" rev-parse --show-toplevel)"
csharp_apphost="$samples_path/TigerBeetle.AppHost/TigerBeetle.AppHost.csproj"
typescript_apphost="$samples_path/TigerBeetle.TypeScript"

aspire() {
  (cd "$repository_root" && dotnet tool run aspire -- "$@")
}

client_url() {
  local apphost="$1"
  local resource="$2"

  aspire describe "$resource" --apphost "$apphost" --format Json --non-interactive \
    | jq -er '.resources[0].urls[] | select(.name == "http") | .url'
}

# Starts an AppHost, runs a check against it, and always stops it. Returns the first failure.
with_running_apphost() {
  local apphost="$1"
  local wait_resource="$2"
  local check="$3"
  local status=0
  local stop_status=0

  aspire start --apphost "$apphost" --isolated --non-interactive
  aspire wait "$wait_resource" --apphost "$apphost" --timeout 180 --non-interactive || status=$?
  if [[ $status -eq 0 ]]; then
    "$check" "$apphost" || status=$?
  fi
  aspire stop --apphost "$apphost" --non-interactive || stop_status=$?

  if [[ $status -ne 0 ]]; then
    return "$status"
  fi
  return "$stop_status"
}

check_account_lookup() {
  local url
  local response

  url="$(client_url "$1" client)" || return
  response="$(curl --fail --silent --show-error --retry 10 --retry-connrefused --retry-delay 1 "${url%/}/accounts/1")" || return
  if ! jq -e '. == []' <<<"$response" >/dev/null; then
    echo "Expected client /accounts/1 to return [], received: $response" >&2
    return 1
  fi
}

check_cdc_event() {
  local url
  local transfer_id
  local events="[]"

  url="$(client_url "$1" client)" || return
  transfer_id="$(curl --fail --silent --show-error --request POST "${url%/}/transfers" | jq -er '.id')" || return

  for _ in {1..30}; do
    events="$(curl --fail --silent --show-error "${url%/}/cdc/events")" || return
    if jq -e --arg transfer_id "$transfer_id" 'any(.[]; (.transfer.id | tostring) == $transfer_id)' <<<"$events" >/dev/null; then
      return 0
    fi
    sleep 1
  done

  echo "CDC did not publish transfer $transfer_id. Last events response: $events" >&2
  return 1
}

verify_compose_publish() {
  local output_path
  local compose_file

  output_path="$(mktemp -d)"
  compose_file="$output_path/docker-compose.yaml"
  aspire publish --apphost "$csharp_apphost" --output-path "$output_path" --non-interactive || return

  for expected in \
    'ghcr.io/tigerbeetle/tigerbeetle:' \
    'tigerbeetle format' \
    'tigerbeetle start' \
    'tigerbeetle amqp' \
    'rabbitmq:' \
    'seccomp=unconfined' \
    'volumes:'; do
    if ! grep -qF "$expected" "$compose_file"; then
      echo "The published Compose file does not contain '$expected': $compose_file" >&2
      return 1
    fi
  done
}

command -v curl >/dev/null
command -v jq >/dev/null

(cd "$repository_root" && dotnet tool restore)

npm ci --prefix "$typescript_apphost"
npm ci --prefix "$typescript_apphost/client"
aspire restore --apphost "$typescript_apphost" --non-interactive
npm run build --prefix "$typescript_apphost"

with_running_apphost "$csharp_apphost" tigerbeetle-cdc check_cdc_event
with_running_apphost "$typescript_apphost" client check_account_lookup
verify_compose_publish
