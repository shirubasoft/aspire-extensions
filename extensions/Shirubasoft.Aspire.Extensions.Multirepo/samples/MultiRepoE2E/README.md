# Multi-repository image handoff

This sample separates three concerns:

- `Spire.ModuleContract` is the producer-owned typed module contract.
- `ResourceBuildRepository` contains the Dockerfile and build inputs for `multi-repo-api`.
- Producer and consumer AppHosts hand the built image identity across a workflow document.

Requirements: .NET 10 SDK, Aspire CLI 13.5.3 or later, Git, and Docker or Podman.

## Run either AppHost

The checked-in configuration uses the local build fixture, so each AppHost runs without initialization or configuration changes:

```bash
aspire run --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/MultiRepoE2E/Spire.Consumer.AppHost/Spire.Consumer.AppHost.csproj
```

```bash
aspire run --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/MultiRepoE2E/Spire.Producer.AppHost/Spire.Producer.AppHost.csproj
```

To use another checkout or a pinned build, set `BuildRepository` and `BuildRepositoryRevision` under `Aspire:ModularAppHosts:Modules:multi-repo-resource-build:Containers:multi-repo-api`. Set the module source with the module-level `Repository`. If either repository needs initialization, run the exact AppHost-aware command reported by preflight.

## Reproduce the image handoff

From the repository root, start a local registry:

```bash
docker run --detach --rm \
  --name modular-apphosts-sample-registry \
  --publish 5000:5000 \
  registry:2
```

Restore the consumer, then publish the producer image and workflow document:

```bash
dotnet tool restore
dotnet restore extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/MultiRepoE2E/Spire.Consumer.Tests/Spire.Consumer.Tests.csproj
dotnet run \
  --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/src/Shirubasoft.Aspire.Extensions.Multirepo.Tool/Shirubasoft.Aspire.Extensions.Multirepo.Tool.csproj \
  -- images publish \
  --apphost extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/MultiRepoE2E/Spire.Producer.AppHost \
  --all \
  --tag manual-e2e \
  --output artifacts/manual-module-image-workflow.json
```

Run the consumer test through `images apply`:

```bash
dotnet run \
  --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/src/Shirubasoft.Aspire.Extensions.Multirepo.Tool/Shirubasoft.Aspire.Extensions.Multirepo.Tool.csproj \
  -- images apply \
  --file artifacts/manual-module-image-workflow.json \
  -- \
  dotnet test \
  extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/MultiRepoE2E/Spire.Consumer.Tests/Spire.Consumer.Tests.csproj \
  --configuration Release
```

The consumer uses the published image even when the producer source and build checkout are unavailable. Inspect `artifacts/manual-module-image-workflow.json` for the exact handoff contract, then stop the registry:

```bash
docker stop modular-apphosts-sample-registry
```

For initialization ownership, isolation, runtime-proxy, cleanup, and CI coverage, see the [E2E test harness](../../tests/Spire.MultiRepo.E2E.Tests/README.md). For the production workflow pattern, see [Cross-repository E2E image workflows](../../docs/external-e2e-workflows.md).
