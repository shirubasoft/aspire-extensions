# Module image pipeline sample

This AppHost declares the supported module image publishers: a declared container, a project
exported as a container, a factory-created advanced publisher, and an Aspire-native Dockerfile
resource. A second module contributes another image publisher so the push sample proves named-resource isolation. It also declares a
consumed image with a pull mapping. A Docker Compose compute environment verifies that module and
`WithContainerRegistry` declarations retain their remote identities. Publisher resources use
explicit start, allowing the AppHost model to run against local fixtures:

```bash
aspire run --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/ImagePushE2E/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj
```

For maintainer validation, stop the AppHost and run the pipeline scripts from the repository root. Restore the pinned Aspire CLI first:

```bash
dotnet tool restore
bash extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/ImagePushE2E/test-image-describe.sh
bash extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/ImagePushE2E/test-image-push.sh
bash extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/ImagePushE2E/test-image-pull.sh
```

`test-image-describe.sh` verifies the deterministic `module-images.json` document, including distinct
run, pull, and push references, build metadata, a registry mapping, and a consumed image. The push
script proves named resource steps, including build isolation and the sanitized source-branch alias
for selected publishers. The push and pull scripts create temporary
local registries and validate the operations against them. Set `ASPIRE_CONTAINER_RUNTIME=podman` to
run them with Podman.
