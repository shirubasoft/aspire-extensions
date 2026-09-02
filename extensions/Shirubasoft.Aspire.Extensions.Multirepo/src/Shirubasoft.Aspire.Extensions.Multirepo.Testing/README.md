# Shirubasoft.Aspire.Extensions.Multirepo.Testing

Run the same Aspire E2E test suite against an in-process AppHost or an Aspire-managed Docker Compose deployment. The package implements Compose mode through `IDistributedApplicationTestingBuilder` and keeps testing and Docker dependencies out of regular AppHosts.

```bash
dotnet add package Shirubasoft.Aspire.Extensions.Multirepo.Testing
```

Export the endpoints and values an external deployment test needs:

```csharp
compose
    .WithTestEndpoint(
        "catalog-api",
        catalog.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestValue("Parameters:api-key", apiKey.Resource)
    .WithTestConnectionString("catalog", catalogDatabase);
```

Then pass the selected mode into a shared scenario:

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
    await app.ResourceNotifications.WaitForResourceHealthyAsync("catalog-api");
    // Run shared assertions.
}
```

`DeployAsync` deploys through Aspire, imports the resolved endpoints and configuration, and destroys the deployment when the builder is disposed. It prefers a restored local Aspire tool manifest, retries detected host-port bind conflicts once, and allows both behaviors to be overridden through `DockerComposeDeploymentOptions`. `Create` and `CreateFromEnvironment` import a deployment owned by another system.

Endpoint names and multiple endpoints per resource are preserved, so the same `CreateHttpClient(resourceName, endpointName)` calls work in both modes. Pass `DockerComposeDeploymentOptions` when CI needs an explicit environment, output path, Aspire CLI path, or deploy/cleanup timeouts. Aspire CLI output is streamed during deployment and cleanup.

Read the [E2E testing guide](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/e2e-testing.md) for endpoint requirements, lifecycle options, and CI configuration. The repository also includes a complete [catalog-and-orders sample](https://github.com/Shirubasoft/aspire-extensions/tree/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples/E2ETesting).
