# E2E testing with AppHost and Docker Compose

`Shirubasoft.Aspire.Extensions.Multirepo.Testing` lets one test suite use Aspire's standard `IDistributedApplicationTestingBuilder` contract in two modes:

- start the AppHost in the test process with `DistributedApplicationTestingBuilder`;
- deploy the AppHost to Docker Compose with Aspire and represent the deployed services as a testing application.

The testing package carries `Aspire.Hosting.Testing` and Docker hosting dependencies. Install it in both the AppHost that declares the Docker Compose test environment and the test project that creates or deploys that environment.

```bash
dotnet add path/to/AppHost.csproj package Shirubasoft.Aspire.Extensions.Multirepo.Testing
dotnet add path/to/AppHost.Tests.csproj package Shirubasoft.Aspire.Extensions.Multirepo.Testing
```

## Export test configuration from the AppHost

Add a Docker Compose environment and export only the endpoints and values that external tests require:

```csharp
using Aspire.Hosting.Testing;

var compose = builder.AddDockerComposeEnvironment("e2e")
    .WithDashboard(false);
var apiKey = builder.AddParameter("orders-api-key", secret: true);

compose
    .WithTestEndpoint(
        "catalog-api",
        catalog.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestEndpoint(
        "orders-api",
        orders.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestValue("Parameters:orders-api-key", apiKey.Resource)
    .WithTestConnectionString("catalog", catalogDatabase);
```

- `WithTestEndpoint` requires an external endpoint and a root-relative health path. It preserves endpoint names and allocates a loopback host port when one is not declared.
- `WithTestValue` accepts any Aspire `IValueProvider`, including secret parameters.
- `WithTestConnectionString` imports a resource as `ConnectionStrings:<name>`.

## Use one test lifecycle

Pass the configured mode into a shared scenario and select only the builder:

```csharp
static async Task RunScenarioAsync(string mode)
{
    await using IDistributedApplicationTestingBuilder builder = mode switch
    {
        "apphost" => await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.EShop_E2E_AppHost>(),
        "compose" => await DockerComposeDeploymentTestingBuilder
            .DeployAsync<Projects.EShop_E2E_AppHost>(),
        _ => throw new InvalidOperationException($"Unknown E2E mode '{mode}'.")
    };

    await using var app = await builder.BuildAsync();
    await app.StartAsync();
    await app.ResourceNotifications.WaitForResourceHealthyAsync("orders-api");

    using var orders = app.CreateHttpClient("orders-api", "http");
    // Run shared assertions.
}
```

In AppHost mode, Aspire starts the project resources and allocates their endpoints. In Compose mode, `DeployAsync` first runs `aspire deploy`, imports the resolved endpoints and configuration, and returns a builder for the already deployed services. Disposing that builder runs `aspire destroy`.

## Deployment options

`DeployAsync<TEntryPoint>()` uses these optional environment variables:

| Variable | Purpose | Default |
| --- | --- | --- |
| `ASPIRE_TEST_DEPLOYMENT_ENVIRONMENT` | Aspire deployment environment name. | A unique `Tests-<process>-<id>` name. |
| `ASPIRE_TEST_DEPLOYMENT_OUTPUT_PATH` | Directory for generated Compose files and environment configuration. | A temporary directory removed during disposal. |

Use an explicit output path in CI when an emergency teardown step needs to locate deployment state after a cancelled test process.

For code-owned configuration, pass `DockerComposeDeploymentOptions`:

```csharp
var builder = await DockerComposeDeploymentTestingBuilder
    .DeployAsync<Projects.EShop_E2E_AppHost>(new DockerComposeDeploymentOptions
    {
        EnvironmentName = "CI",
        OutputPath = artifactsPath,
        AspireCliPath = toolPath,
        PortConflictRetryCount = 1,
        DeploymentTimeout = TimeSpan.FromMinutes(15),
        CleanupTimeout = TimeSpan.FromMinutes(3)
    });
```

With the default `AspireCliPath`, the builder prefers a restored Aspire command from the nearest .NET tool manifest, then falls back to `aspire` on `PATH`. It streams deploy/destroy output, attempts cleanup after a timeout or port conflict, and retains the output directory when cleanup fails. Compose port conflicts are retried once by default.

When another system owns deployment, use `DockerComposeDeploymentTestingBuilder.Create<TEntryPoint>(filePath)` or set `ASPIRE_TEST_CONFIGURATION_FILE` and call `CreateFromEnvironment<TEntryPoint>()`. Both import a dotenv configuration file and reject malformed, duplicate, or inconsistent endpoint data.

Aspire's environment-specific file can contain resolved secrets. Keep it out of source control and do not publish it as a CI artifact.

See the [eShop E2E sample](../samples/E2ETesting/README.md) for a complete AppHost, test suite, and CI workflow.
