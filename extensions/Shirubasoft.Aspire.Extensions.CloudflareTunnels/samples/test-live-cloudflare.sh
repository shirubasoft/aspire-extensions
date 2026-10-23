#!/usr/bin/env bash
# Publishes the sample through Cloudflare and checks each public URL: a Quick Tunnel, a named tunnel
# in run mode, and a named tunnel deployed with Docker Compose. The named tunnel steps create or
# reuse the Cloudflare tunnel "web-tunnel" and route CLOUDFLARE_TEST_HOSTNAME to it.
#
# Requires CLOUDFLARE_ACCOUNT_ID, CLOUDFLARE_API_TOKEN, and CLOUDFLARE_TEST_HOSTNAME. The API token
# needs Cloudflare Tunnel: Edit on the account, and Zone: Read and DNS: Edit on the hostname's zone.
set -euo pipefail

samples_path="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(git -C "$samples_path" rev-parse --show-toplevel)"
apphost="$samples_path/CloudflareTunnels.AppHost/CloudflareTunnels.AppHost.csproj"
deployment_environment="CloudflareLiveTest"
tunnel_name="web-tunnel"
output_path="$(mktemp -d)"

for variable in CLOUDFLARE_ACCOUNT_ID CLOUDFLARE_API_TOKEN CLOUDFLARE_TEST_HOSTNAME; do
  if [[ -z "${!variable:-}" ]]; then
    echo "$variable is required." >&2
    exit 1
  fi
done

hostname="$CLOUDFLARE_TEST_HOSTNAME"
route_resource="$tunnel_name-route-${hostname//./-}"

aspire() {
  (cd "$repository_root" && dotnet tool run aspire -- "$@")
}

# Named tunnel runs read the hostname and Cloudflare parameters from the environment.
aspire_named() {
  Cloudflare__Hostname="$hostname" \
    Parameters__web_tunnel_account_id="$CLOUDFLARE_ACCOUNT_ID" \
    Parameters__web_tunnel_api_token="$CLOUDFLARE_API_TOKEN" \
    aspire "$@"
}

assert_nginx() {
  local url="$1"
  local response="$output_path/response.html"

  # A new tunnel, a moved CNAME, or an ingress update takes a few minutes to reach every edge.
  curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 10 --max-time 30 \
    --output "$response" "$url" || return
  if ! grep -q 'Welcome to nginx!' "$response"; then
    echo "$url did not return the nginx welcome page." >&2
    return 1
  fi
  printf 'Reached nginx through %s.\n' "$url"
}

public_url() {
  aspire describe "$tunnel_name" --apphost "$apphost" --format Json --non-interactive \
    | jq -er '.resources[0].urls[] | select(.name == "public") | .url'
}

check_quick_tunnel() {
  local url=""

  for _ in {1..30}; do
    url="$(public_url)" && break
    sleep 2
  done
  assert_nginx "$url"
}

check_named_tunnel() {
  aspire wait "$route_resource" --apphost "$apphost" --timeout 180 --non-interactive || return
  assert_nginx "https://$hostname"
}

# Starts the sample, runs a check, and always stops it. Returns the first failure.
with_running_sample() {
  local start="$1"
  local check="$2"
  local status=0
  local stop_status=0

  "$start" start --apphost "$apphost" --isolated --non-interactive
  aspire wait "$tunnel_name" --apphost "$apphost" --timeout 180 --non-interactive || status=$?
  if [[ $status -eq 0 ]]; then
    "$check" || status=$?
  fi
  if [[ $status -ne 0 ]]; then
    aspire logs "$tunnel_name" --apphost "$apphost" --tail 100 --non-interactive || true
  fi
  aspire stop --apphost "$apphost" --non-interactive || stop_status=$?

  if [[ $status -ne 0 ]]; then
    return "$status"
  fi
  return "$stop_status"
}

cloudflare_api() {
  curl --fail --silent --show-error --header "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
    "https://api.cloudflare.com/client/v4/accounts/$CLOUDFLARE_ACCOUNT_ID/$1"
}

# The run-mode check creates the tunnel when it does not exist, so its connector token is available
# to the deployment without storing it as another secret.
check_deployment() {
  local tunnel_id
  local tunnel_token
  local status=0

  tunnel_id="$(cloudflare_api "cfd_tunnel?name=$tunnel_name&is_deleted=false" | jq -er '.result[0].id')" || return
  tunnel_token="$(cloudflare_api "cfd_tunnel/$tunnel_id/token" | jq -er '.result')" || return

  Parameters__web_tunnel_tunnel_token="$tunnel_token" aspire_named deploy \
    --apphost "$apphost" --environment "$deployment_environment" --non-interactive || status=$?
  if [[ $status -eq 0 ]]; then
    assert_nginx "https://$hostname" || status=$?
  fi
  Parameters__web_tunnel_tunnel_token="$tunnel_token" aspire_named destroy \
    --apphost "$apphost" --environment "$deployment_environment" --yes --non-interactive || true

  return "$status"
}

command -v curl >/dev/null
command -v jq >/dev/null
(cd "$repository_root" && dotnet tool restore)

with_running_sample aspire check_quick_tunnel
with_running_sample aspire_named check_named_tunnel
check_deployment
