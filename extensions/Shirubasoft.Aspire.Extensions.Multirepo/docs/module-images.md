# Build and publish module images

This advanced guide covers image preparation, build, push, pull, and description. Start with the [module guide](modules.md) for contracts, imports, and configuration.

## Choose a publisher

Prefer Aspire-native publishers. `ExportAsContainer` delegates project image build and push behavior to Aspire:

```csharp
module.AddProject(
        "orders-api",
        "src/Orders.Api/Orders.Api.csproj",
        ModuleProjectPathBase.Repository)
    .ExportAsContainer("example/orders-api");
```

A module factory can return `AddDockerfile(...)` to keep Aspire's Dockerfile annotations and pipeline steps.

Use an advanced command only when the image cannot use a native publisher. `ExportAsContainerWithCommand` configures a project, `WithImagePublishCommand` configures a declared container, and the command-oriented `AddResource` overload configures a factory-created container:

```csharp
module.AddContainer("orders-static", "orders-static")
    .WithImagePublishCommand(new ModuleImageCommandOptions(
        imageName: "orders-static",
        publishCommand: ModuleImageCommandOptions.ContainerRuntimePlaceholder,
        publishArguments:
        [
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            "."
        ]));
```

The runtime placeholder resolves through Aspire's selected Docker or Podman runtime when the command runs. The AppHost model remains synchronous.

## Runtime preparation

An advanced image is prepared immediately before its container starts:

| Source state | Behavior |
| --- | --- |
| Clean, image already local | Reuse the image. |
| Clean, image missing, `PullBeforeBuild = true` | Try the registry, then build if the pull fails. |
| Dirty | Build every AppHost session with a `-dirty` tag; never push it. |
| Explicit-start resource | Defer preparation until that resource starts. |

Without an explicit tag, clean images use the effective build repository's sanitized branch plus the first 12 commit characters. Detached checkouts use `sha-<commit>`; definitions without repository source fall back to `latest`. The resource starts only after preparation succeeds.

`ImageRegistry` is a registry host such as `ghcr.io`; `ImageName` is its repository path, such as `example/orders-api`. Leave the registry unset for an unqualified local image.

## Run image pipelines

Module publishers contribute aggregate and resource-specific Aspire steps:

| Task | All resources | One resource |
| --- | --- | --- |
| Build | `aspire do build` | `aspire do build-orders-api` |
| Push | `aspire do push` | `aspire do push-orders-api` |
| Pull | `aspire do pull` | `aspire do pull-orders-api` |

Native project and Dockerfile publishers keep Aspire's build and push operations and reject dirty source before push. An advanced push depends on its build step, so a missing image is prepared first. Authenticate Aspire's selected container runtime to each registry before pushing or pulling.

Resources configured with `WithContainerRegistry` use Aspire's registry-aware image manager. Otherwise, an explicit `ImageRegistry` supplies the remote target. A clean advanced publisher also pushes a sanitized branch alias when a source branch or CI head/ref alias is available; workflow documents retain the exact canonical tag.

For validated module/resource selection and a workflow document in one AppHost invocation, use [`modular-apphosts images publish`](../src/Shirubasoft.Aspire.Extensions.Multirepo.Tool/README.md#producer-publish-images). It runs a separate `workflow-images` aggregate without changing Aspire's standard `push` behavior.

### Pull from another reference

Use `WithImagePullMapping` when the pull source differs from the resource's local image:

```csharp
module.AddContainer("api", "ghcr.io/api", "1-0")
    .WithImagePullMapping("mycustomregistry.io/images:api-1-0");
```

`aspire do pull-api` pulls the mapped reference and tags it as `ghcr.io/api:1-0`. The mapping affects pulls only; push still uses the configured push target. The local image must use a tag because the pipeline retags it.

## Describe images

Write a read-only inventory without pulling, building, or tagging images:

```bash
aspire do describe-images --output-path artifacts
```

The command writes schema-version-3 JSON to `artifacts/module-images.json`. It includes each module's contract package ID and each image's declared and effective resource names, kind, run/pull/push references, tag or digest, and build metadata. This module-aware inventory is separate from Aspire's application manifest.

## Advanced command options

Advanced commands receive `ASPIRE_MODULE_IMAGE` and can use these `ModuleImageCommandOptions` placeholders in their argument list:

| Placeholder | Value |
| --- | --- |
| `{container-runtime}` | Aspire's selected Docker or Podman executable. |
| `{image}` | Complete effective image reference. |
| `{image-registry}` | Explicit registry, or an empty string. |
| `{image-repository}` | Repository including the registry. |
| `{image-name}` | Repository path without the explicit registry. |
| `{image-tag}` | Effective tag. |

Commands are executed directly, without a shell. `WorkingDirectory` is relative to the effective build repository. It defaults to the project directory for a project built from the module repository, and otherwise to the build-repository root.

Use `ProducedImageReference` when a command creates a fixed image reference; the runtime retags it to the canonical and local run references after a successful build. Any output that varies with image identity should use placeholders. With `PublishImage: false` and a complete registry/name plus exactly one tag or digest, the resource becomes an external-image override with no build command.

## Build from another repository

Set `BuildRepository` when image inputs belong to a repository other than the module source:

```csharp
module.AddContainer("orders-database", "example/orders-database")
    .WithImagePublishCommand(new ModuleImageCommandOptions(
        imageName: "example/orders-database",
        publishCommand: ModuleImageCommandOptions.ContainerRuntimePlaceholder,
        publishArguments:
        [
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            "."
        ])
    {
        BuildRepository = "https://github.com/example/orders-database.git",
        CheckoutDirectoryName = "orders-database-images"
    });
```

The AppHost plans this checkout independently and uses its source state for the canonical tag. Acquire it with `aspire do initialize --apphost . --non-interactive`; the [module initialization rules](modules.md#import-from-a-repository) apply to ownership, naming, and pinned revisions.

When `PullBeforeBuild` and an explicit tag allow the registry image to satisfy preparation, normal run and build do not require an absent build checkout. Initialization still acquires it for future source builds. `RefreshBuildRepositoryOnRun` can opt a clean, unpinned build checkout into a run-time fast-forward; dirty checkouts are never moved.

For an advanced publisher, per-project or per-container AppHost configuration can override the build repository, revision, checkout name, image identity, publish command, and refresh policy without changing the shared contract. Native publishers retain Aspire's own build behavior; their configuration can override image identity and run mode, but it does not replace the native publisher with an arbitrary command.
