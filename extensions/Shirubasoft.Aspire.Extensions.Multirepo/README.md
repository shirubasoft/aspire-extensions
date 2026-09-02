# Shirubasoft.Aspire.Extensions.Multirepo

Define an Aspire resource graph once, expose it as a typed C# contract, and reuse it across AppHosts. A consumer references the producer-owned contract package; repository configuration supplies source and build context when the module is imported from another checkout.

## Packages

| Package | Purpose |
| --- | --- |
| `Shirubasoft.Aspire.Extensions.Multirepo` | Define, export, import, and consume modules. |
| `Shirubasoft.Aspire.Extensions.Multirepo.Testing` | Run the same E2E tests against an AppHost or Docker Compose deployment. |
| `Shirubasoft.Aspire.Extensions.Multirepo.Templates` | Scaffold a module contract with `dotnet new aspire-module`. |
| `Shirubasoft.Aspire.Extensions.Multirepo.Tool` | Publish and apply module image workflow documents or dispatch cross-repository E2E workflows. |

The runtime packages target .NET 10, the source generator supports .NET SDK 10.0.100 or later, and the Aspire-facing packages require Aspire 13.4.6 or later. The project uses the repository's [MIT License](https://github.com/Shirubasoft/aspire-extensions/blob/main/LICENSE).

## Quick start

Prerequisites are .NET SDK 10.0.100 or later, Aspire CLI 13.4.6 or later, and a running Docker or Podman runtime. Add the core package to an AppHost or shared contract project, install the template, and scaffold a module:

```bash
dotnet add package Shirubasoft.Aspire.Extensions.Multirepo
dotnet new install Shirubasoft.Aspire.Extensions.Multirepo.Templates
dotnet new aspire-module --name CatalogModule --moduleName catalog --namespace Catalog.Modules
```

The generated contract is immediately runnable and can be replaced with the module's real resource graph:

```csharp
using Aspire.Hosting;

namespace Catalog.Modules;

[GenerateDistributedApplicationModule(Name, Version = "1", PackageId = "Catalog.Modules")]
public static partial class CatalogModule
{
    public const string Name = "catalog";
    public const string ApiResourceName = "catalog-api";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "nginx", "alpine")
            .Configure((_, container) => container
                .WithHttpEndpoint(targetPort: 80, name: "http"));
    }
}
```

Reference the contract project or package from each consumer. The source generator creates `AddCatalogModule`, `ImportCatalogModule`, and typed resource properties:

```csharp
using Catalog.Modules;

var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddCatalogModule();

builder.AddContainer("storefront", "nginx", "alpine")
    .WithReference(catalog.Api.GetEndpoint("http"))
    .WaitFor(catalog.Api);

await builder.Build().RunAsync();
```

From the AppHost directory, run `aspire run` and open the dashboard URL. Use `ImportCatalogModule()` instead when repository configuration should supply the module's source tree. Generated add/import methods also work inside module definitions, so a parent module can own and materialize its local or imported dependencies.

## Next steps

- [Define and import modules](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/modules.md), including repository initialization, configuration, resource aliases, required host tools, and local project/container switching.
- [Build and publish module images](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/module-images.md).
- [Test an AppHost and Docker Compose deployment with one suite](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/e2e-testing.md).
- [Hand images between repositories](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/external-e2e-workflows.md) with the [workflow tool](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/src/Shirubasoft.Aspire.Extensions.Multirepo.Tool/README.md).
- [Run the samples](https://github.com/Shirubasoft/aspire-extensions/tree/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/samples).

For repository setup, validation, and releases, see [Contributing](https://github.com/Shirubasoft/aspire-extensions/blob/main/CONTRIBUTING.md).
