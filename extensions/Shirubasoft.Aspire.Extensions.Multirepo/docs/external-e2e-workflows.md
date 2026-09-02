# Cross-repository E2E image workflows

A producer repository can build the module images it owns and run a consumer repository's existing E2E AppHost against those exact images. The consumer receives a strict workflow document and ordinary .NET configuration; it does not clone or rebuild the producer's resources.

The checked-in workflows keep orchestration in the tool:

- [Consumer receiver](workflows/repo-a-e2e.yml) supports both `workflow_call` and `workflow_dispatch`.
- [Reusable producer call](workflows/repo-b-workflow-call.yml) passes compact document JSON directly.
- [Producer dispatch](workflows/repo-b-dispatch.yml) starts a separate consumer run, waits for it, and returns its status.

## Choose a handoff

| Handoff | Use it when | Behavior |
| --- | --- | --- |
| Reusable `workflow_call` | GitHub permits the producer to call the consumer's workflow directly. | The consumer workflow runs in the caller context and explicitly checks out the consumer repository. |
| `workflow_dispatch` | The consumer needs a separate workflow run, permission boundary, and UI history. | The tool dispatches the exact run, streams it, and returns its result. |

For a reusable call, copy the receiver and reusable producer examples. The `uses:` revision chooses the consumer workflow definition; the `repo-a-ref` input chooses the consumer source revision.

For a separate run, copy the receiver and dispatch examples. The dispatch command is shown below.

## Set up both repositories

The producer AppHost must expose pushable module images with a registry, repository, tag, and build/push plan. The consumer AppHost must import the same module contract identities. Workflow entries use declared `module/resource` names, not aliases assigned by the consumer.

Pin the same runtime package and workflow tool release in both repositories:

```bash
dotnet new tool-manifest
dotnet tool install Shirubasoft.Aspire.Extensions.Multirepo.Tool --version <VERSION>
git add .config/dotnet-tools.json
```

Restore the manifest with `dotnet tool restore`. Publishing also needs Aspire CLI 13.4.6 or later; dispatch needs GitHub CLI 2.87.0 or later.

See the [tool reference](../src/Shirubasoft.Aspire.Extensions.Multirepo.Tool/README.md) for command behavior, the strict workflow-document schema, tag precedence, limits, and exit codes. Use each command's `--help` output for its complete option list.

## Publish producer images

Authenticate the selected container runtime, then publish an explicit module/resource selection:

```bash
dotnet tool run modular-apphosts -- images publish \
  --apphost src/Producer.AppHost \
  --module orders \
  --resource api \
  --tag "$GITHUB_SHA" \
  --output module-image-workflow.json
```

Repeat `--module` and `--resource` as needed, or use `--all`. On GitHub Actions, the command emits `workflow-document` for a reusable workflow and `workflow-document-path` for dispatch. Use `aspire do describe-images` when only a read-only inventory is needed.

## Apply images in the consumer

Run the consumer's normal E2E command through `images apply`:

```bash
dotnet tool run modular-apphosts -- images apply \
  --json "$IMAGE_WORKFLOW" \
  -- \
  dotnet test tests/Consumer.E2E.Tests/Consumer.E2E.Tests.csproj --configuration Release
```

Use exactly one of `--json` or `--file`. `apply` starts the command after `--` directly, with the workflow document projected into `Aspire:ModularAppHosts:Modules` configuration. Listed projects use container mode, local publishing is disabled, and `ImagePullPolicy.Always` prevents stale local tags. Standard input/output and the child exit code pass through unchanged.

When every source-backed image is covered and the module has no other repository-dependent resources, the consumer does not need the producer checkout. Consumer-side `--tag` or `--resource-tags` can select other existing registry tags; they do not create or retag images.

## Dispatch a separate consumer run

The producer can create and wait for a specific consumer workflow run with one command:

```bash
dotnet tool run modular-apphosts -- workflow dispatch \
  --repository your-org/consumer \
  --workflow external-e2e.yml \
  --ref main \
  --workflow-document module-image-workflow.json \
  --input repo-a-ref=main
```

The tool passes JSON to `gh workflow run`, reads the created run URL, and streams `gh run watch --compact --exit-status`. It emits `run-id` and `run-url` as GitHub step outputs. Bound the wait with the calling job's `timeout-minutes`.

## Permissions and trust

| Operation | Requirement |
| --- | --- |
| Producer checkout | `contents: read`. |
| Push images | Registry write access; commonly `packages: write` for GHCR. |
| Dispatch consumer | `GH_TOKEN` that can access the consumer and trigger Actions. The producer's built-in token normally cannot dispatch another repository. |
| Watch consumer | Authentication that can read the run and checks. Fine-grained PATs cannot grant the Checks permission needed by `gh run watch`. |
| Pull images | Registry read access; commonly `packages: read` for GHCR. |
| Reusable call to a private consumer | A checkout token that can read that repository. |

For private GHCR packages, grant the consumer repository Actions access in the package settings or provide suitable registry credentials. Keep tokens in `GH_TOKEN` or action inputs, not command arguments or workflow documents.

Do not publish images from untrusted fork pull requests with privileged credentials. Use a trusted maintainer workflow or another isolated policy before building and executing fork-provided container inputs.

## Troubleshooting

- **No run URL:** upgrade `gh` to 2.87.0 or later. Do not select the latest run; concurrent dispatches make that ambiguous.
- **Apply has no effect:** keep the E2E command after `--` in the same `images apply` invocation.
- **An image cannot be pulled:** confirm the selected tag was pushed and the consumer runtime can authenticate to its registry.
- **A resource is unknown:** use its declared `module/resource` identity, not the consumer's alias or effective Aspire name.
- **Private GHCR returns unauthorized:** grant the consumer repository package access and verify the login token can read it.
- **`gh run watch` rejects authentication:** use a compatible GitHub App installation token or classic token instead of a fine-grained PAT.
- **Dispatch exceeds the payload limit:** publish fewer resources or shorten input values; the tool validates the complete payload before contacting GitHub.

The [MultiRepoE2E sample](../samples/MultiRepoE2E/README.md) runs the same producer-to-consumer handoff against a local registry. CI validates publish/apply behavior; tool tests cover deterministic dispatch responses.
