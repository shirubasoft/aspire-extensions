# E2E tests for modular AppHosts

This sample runs one checkout scenario against either an in-process AppHost or an Aspire-deployed Docker Compose environment. Both modes produce `IDistributedApplicationTestingBuilder`, so lifecycle, health waits, clients, and assertions are shared.

The AppHost adds `catalog` and `orders` modules, supplies a secret API-key parameter, and exports the endpoints and values needed by external deployment tests. Both module repositories use local paths, so the sample does not require initialization.

## Run through the AppHost

From the repository root:

```bash
ESHOP_E2E_MODE=apphost \
  dotnet test extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj
```

Aspire starts both project resources, waits for health, and provides their HTTP clients.

## Run through Docker Compose

Install Aspire CLI 13.5.3 or later, start Docker or Podman, and run:

```bash
ESHOP_E2E_MODE=compose \
  dotnet test extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj
```

`DockerComposeDeploymentTestingBuilder.DeployAsync` deploys through Aspire, imports the generated environment file, and returns the same testing-builder contract. Disposing it destroys the deployment; failed cleanup retains the output directory for recovery.

Environment-specific deployment files can contain resolved secrets. The ignored `aspire-output` directory should not be published as a CI artifact.

See the [E2E testing guide](../../docs/e2e-testing.md) for endpoint requirements, externally managed deployments, retry behavior, and CI options.
