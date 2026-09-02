#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
apphost_project="$script_directory/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj"
description_directory="$(mktemp -d)"
document="$description_directory/module-images.json"
effective_image_tag="push-test"

if [[ -n "$(git -C "$repository_root" status --porcelain --untracked-files=normal)" ]]; then
    effective_image_tag="$effective_image_tag-dirty"
fi

cleanup() {
    rm -r -- "$description_directory"
}
trap cleanup EXIT

export ImagePush__RegistryEndpoint="registry.example.test"
export ImagePush__RetagRegistryEndpoint="mirror.example.test"

cd "$repository_root"
dotnet tool run aspire -- "do" describe-images \
    --apphost "$apphost_project" \
    --output-path "$description_directory" \
    --non-interactive

jq --exit-status --arg effective_image_tag "$effective_image_tag" '
    .schemaVersion == 3 and
    ([.modules[] | {name, contractPackageId}] == [
        {
          "name": "contract-only",
          "contractPackageId": "Sample.ContractOnly"
        },
        {
          "name": "image-push-e2e",
          "contractPackageId": "Sample.ImagePush.Contract"
        },
        {
          "name": "image-push-extra",
          "contractPackageId": null
        }
    ]) and
    ([.images[].effectiveResource] == [
        "image-pull-mapped",
        "image-push-declared",
        "image-push-dockerfile",
        "image-push-extra",
        "image-push-factory",
        "image-push-project"
    ]) and
    (.images[] | select(.resource == "image-pull-mapped") |
        .reference == "mirror.example.test/image-pull/local:push-test" and
        .pullReference == "registry.example.test/image-pull/source:push-test" and
        .push == null and
        .build == null) and
    (.images[] | select(.resource == "image-push-declared") |
        .reference == "registry.example.test/image-push/declared:\($effective_image_tag)" and
        .pullReference == "registry.example.test/image-push/declared:push-test" and
        .push == {
          "registry": "registry.example.test",
          "repository": "image-push/declared",
          "tag": "push-test"
        } and
        .build.step == "build-image-push-declared") and
    (.images[] | select(.resource == "image-push-dockerfile") |
        .repository == "image-push-dockerfile" and
        (.tag | test("^[0-9a-f]{40}$")) and
        .reference == "image-push-dockerfile:\(.tag)" and
        .pullReference == "registry.example.test/image-push/dockerfile:push-test" and
        .push == {
          "registry": "registry.example.test",
          "repository": "image-push/dockerfile",
          "tag": "push-test"
        } and
        .build == null) and
    (.images[] | select(.resource == "image-push-factory") |
        .reference == "registry.example.test/image-push/factory:\($effective_image_tag)" and
        .pullReference == "registry.example.test/image-push/factory:push-test" and
        .push == {
          "registry": "registry.example.test",
          "repository": "image-push/factory",
          "tag": "push-test"
        } and
        .build.step == "build-image-push-factory") and
    (.images[] | select(.resource == "image-push-extra") |
        .module == "image-push-extra" and
        .reference == "registry.example.test/image-push/extra:\($effective_image_tag)" and
        .pullReference == "registry.example.test/image-push/extra:push-test" and
        .push == {
          "registry": "registry.example.test",
          "repository": "image-push/extra",
          "tag": "push-test"
        } and
        .build.step == "build-image-push-extra") and
    (.images[] | select(.resource == "image-push-project") |
        .reference == "image-push-project:\($effective_image_tag)" and
        .pullReference == "registry.example.test/image-push/project:push-test" and
        .push == {
          "registry": "registry.example.test",
          "repository": "image-push/project",
          "tag": "push-test"
        } and
        .build.step == "build-image-push-project")
' "$document" >/dev/null

echo "Verified deterministic run, pull, push, and build identities in $document."
