#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
apphost_project="$script_directory/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj"
fixture_directory="$script_directory/ImageFixture"
container_runtime="${ASPIRE_CONTAINER_RUNTIME:-docker}"
image_tag="push-test"
module_image_tag="$image_tag"
branch_name="$(git -C "$repository_root" branch --show-current)"
branch_name="${branch_name:-${GITHUB_HEAD_REF:-${GITHUB_REF_NAME:-}}}"
branch_name="${branch_name:-detached-$(git -C "$repository_root" rev-parse --short=12 HEAD)}"
branch_image_tag="$(
    printf '%s' "$branch_name" |
        tr '[:upper:]' '[:lower:]' |
        sed -E 's/[^a-z0-9_.-]/-/g; s/-+/-/g; s/^[.-]+//; s/[.-]+$//' |
        cut -c1-128 |
        sed -E 's/[.-]+$//'
)"
if [[ -z "$branch_image_tag" ]]; then
    echo "Unable to determine the source branch tag for the image-push E2E test." >&2
    exit 1
fi
fixture_image="image-push-fixture:$image_tag"
registry_container_id=""
registry_endpoint=""
container_registries_configuration=""

if [[ -n "$(git -C "$repository_root" status --porcelain --untracked-files=normal)" ]]; then
    module_image_tag="$image_tag-dirty"
fi

cleanup() {
    if [[ -n "$registry_container_id" ]]; then
        "$container_runtime" rm --force "$registry_container_id" >/dev/null 2>&1 || true
    fi

    "$container_runtime" image rm --force \
        "$fixture_image" \
        "image-push-project:$image_tag" \
        "image-push-project:$module_image_tag" \
        "image-push-project:$branch_image_tag" \
        "${registry_endpoint:+$registry_endpoint/image-push/project:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/project:$branch_image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/declared:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/declared:$module_image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/declared:$branch_image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/dockerfile:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/factory:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/factory:$module_image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/factory:$branch_image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/extra:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/extra:$module_image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/extra:$branch_image_tag}" \
        >/dev/null 2>&1 || true

    if [[ -n "$container_registries_configuration" ]]; then
        rm --force "$container_registries_configuration"
    fi
}
trap cleanup EXIT

registry_container_id="$(
    "$container_runtime" run \
        --detach \
        --publish 127.0.0.1::5000 \
        registry:2
)"
registry_binding="$("$container_runtime" port "$registry_container_id" 5000/tcp | tail -n 1)"
registry_port="${registry_binding##*:}"
registry_endpoint="localhost:$registry_port"

if [[ "$container_runtime" == *podman* ]]; then
    container_registries_configuration="$(mktemp)"
    printf '[[registry]]\nlocation="%s"\ninsecure=true\n' "$registry_endpoint" \
        > "$container_registries_configuration"
    export CONTAINERS_REGISTRIES_CONF="$container_registries_configuration"
fi

for _ in {1..40}; do
    if curl --fail --silent --output /dev/null "http://$registry_endpoint/v2/"; then
        break
    fi
    sleep 0.25
done
curl --fail --silent --output /dev/null "http://$registry_endpoint/v2/"

"$container_runtime" build --tag "$fixture_image" "$fixture_directory"

assert_repository_has_tag() {
    local repository="$1"
    local expected_tag="${2:-$image_tag}"
    local response
    response="$(curl --fail --silent "http://$registry_endpoint/v2/$repository/tags/list")"
    if [[ "$response" != *"\"$expected_tag\""* ]]; then
        echo "Registry repository '$repository' did not contain tag '$expected_tag': $response" >&2
        return 1
    fi
}

assert_repository_absent() {
    local repository="$1"
    local status
    status="$(
        curl --silent --output /dev/null --write-out '%{http_code}' \
            "http://$registry_endpoint/v2/$repository/tags/list"
    )"
    if [[ "$status" != "404" ]]; then
        echo "Expected registry repository '$repository' to be absent, but received HTTP $status." >&2
        return 1
    fi
}

export ImagePush__RegistryEndpoint="$registry_endpoint"
export ASPIRE_CONTAINER_RUNTIME="$container_runtime"

cd "$repository_root"
dotnet tool run aspire -- "do" push-image-push-project \
    --apphost "$apphost_project" \
    --non-interactive

assert_repository_has_tag "image-push/project"
assert_repository_has_tag "image-push/project" "$branch_image_tag"
assert_repository_absent "image-push/declared"
assert_repository_absent "image-push/dockerfile"
assert_repository_absent "image-push/factory"
assert_repository_absent "image-push/extra"

dotnet tool run aspire -- "do" push-image-push-declared \
    --apphost "$apphost_project" \
    --non-interactive

dotnet tool run aspire -- "do" push-image-push-factory \
    --apphost "$apphost_project" \
    --non-interactive

assert_repository_has_tag "image-push/project"
assert_repository_has_tag "image-push/project" "$branch_image_tag"
assert_repository_has_tag "image-push/declared" "$module_image_tag"
assert_repository_has_tag "image-push/declared" "$branch_image_tag"
assert_repository_has_tag "image-push/factory" "$module_image_tag"
assert_repository_has_tag "image-push/factory" "$branch_image_tag"
assert_repository_absent "image-push/dockerfile"
assert_repository_absent "image-push/extra"
if "$container_runtime" image exists "$registry_endpoint/image-push/extra:$image_tag" ||
    "$container_runtime" image exists "$registry_endpoint/image-push/extra:$module_image_tag"; then
    echo "The unselected image-push-extra module was built." >&2
    exit 1
fi

dotnet tool run aspire -- "do" push-image-push-dockerfile \
    --apphost "$apphost_project" \
    --non-interactive

assert_repository_has_tag "image-push/dockerfile"
assert_repository_absent "image-push/extra"

dotnet tool run aspire -- "do" push-image-push-extra \
    --apphost "$apphost_project" \
    --non-interactive

assert_repository_has_tag "image-push/extra" "$image_tag"
assert_repository_has_tag "image-push/extra" "$branch_image_tag"

echo "Verified custom and Aspire-native named-resource image pushes against $registry_endpoint."
