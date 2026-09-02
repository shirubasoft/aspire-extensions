# Samples

The main sample has two AppHosts. AppHost A exports a typed module containing projects, containers, parameters, and other Aspire resources; AppHost B imports the same contract and adds a gateway that references its three HTTP services.

Requirements: .NET 10 SDK, Aspire CLI 13.4.6 or later, and a running Docker or Podman runtime.

## Run the two-AppHost sample

Start the producer from the repository root:

```bash
aspire run --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/AppHostA/ModularSample.AppHostA.csproj
```

The default configuration runs `sample-api` as a project, `sample-static` from `busybox:1.37`, and `sample-generated-static` from its advanced Dockerfile publisher. The remaining resources use explicit start so the default run stays focused. See [`AppHostAModule.cs`](ModuleContract/AppHostAModule.cs) for the complete resource graph and generated typed contract.

Stop AppHost A, then start the consumer:

```bash
aspire run --project extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/AppHostB/ModularSample.AppHostB.csproj
```

AppHost B imports AppHost A from its existing local checkout, so initialization is not required. Its gateway waits for all three imported HTTP services and reports healthy after reaching them. Open the gateway endpoint from the dashboard; `/health` returns HTTP 200 while the upstreams are available.

To inspect readiness from another terminal, run these commands from `extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/AppHostB`:

```bash
aspire wait sample-api
aspire wait sample-static
aspire wait sample-generated-static
aspire wait dependency-gateway
aspire describe --include-hidden
```

### Try project/container switching

Stop AppHost B and change to `extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/AppHostA`. AppHost A has user secrets enabled, so you can build the native `sample-api` image, select container mode, and restart:

```bash
aspire do build-sample-api --apphost ModularSample.AppHostA.csproj --non-interactive
aspire do use-container-sample-api --apphost ModularSample.AppHostA.csproj --non-interactive
aspire run
```

Restore the checked-in selection afterward:

```bash
aspire do use-configured-sample-api --apphost ModularSample.AppHostA.csproj --non-interactive
```

See [developer-local mode switching](../docs/modules.md#developer-local-mode-switching) for global and per-resource choices.

### Run its E2E tests

From the repository root, after confirming the container runtime is available:

```bash
MODULAR_SAMPLES_E2E=true \
  dotnet test extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/ModularSamples.Tests/ModularSamples.Tests.csproj
```

The suite starts both AppHosts through Aspire's testing builder and verifies the project, native container export, declared container, advanced image, and gateway.

## More samples

| Sample | Demonstrates |
| --- | --- |
| [E2E testing](E2ETesting/README.md) | One scenario against an in-process AppHost or Docker Compose deployment. |
| [Image pipelines](ImagePushE2E/README.md) | Native and advanced publishers, image descriptions, push/pull, and registry mapping. |
| [Multi-repository handoff](MultiRepoE2E/README.md) | Independent build inputs and a local-registry producer-to-consumer workflow document. |
| [Remote initialization](RemoteInitialization/README.md) | A managed remote checkout and health-gated Git requirement. |
