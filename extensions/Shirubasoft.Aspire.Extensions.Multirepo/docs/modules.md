# Modules

`Shirubasoft.Aspire.Extensions.Multirepo` lets a C# contract describe a reusable Aspire resource graph. AppHosts reference that contract directly; an imported module can obtain its projects and build inputs from a configured Git repository.

## Define a module

Install the item template in a project that references the core package:

```bash
dotnet new install Shirubasoft.Aspire.Extensions.Multirepo.Templates
dotnet new aspire-module --name OrdersModule --moduleName orders --namespace Orders.Modules
```

The generated contract contains a runnable container. Replace it with the resources owned by the module:

```csharp
using Aspire.Hosting;

namespace Orders.Modules;

[GenerateDistributedApplicationModule(Name, Version = "1", PackageId = "Orders.Modules")]
public static partial class OrdersModule
{
    public const string Name = "orders";
    public const string ApiResourceName = "orders-api";
    public const string CacheResourceName = "orders-cache";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "nginx", "alpine")
            .Configure((_, container) => container
                .WithHttpEndpoint(targetPort: 80, name: "http"));
        module.AddContainer(CacheResourceName, "redis", "8-alpine");
    }
}
```

The generator creates `AddOrdersModule()`, `ImportOrdersModule()`, and an `OrdersModule.Module` wrapper with typed `Api` and `Cache` properties. Add a local definition like any other AppHost resource:

```csharp
var orders = builder.AddOrdersModule();

builder.AddContainer("consumer", "example/consumer", "latest")
    .WithReference(orders.Api.GetEndpoint("http"))
    .WaitFor(orders.Api)
    .WaitFor(orders.Cache);
```

Keep the contract in a project or NuGet package referenced by every participating AppHost. Set `PackageId` to the publishing NuGet package ID when the contract is distributed as a package.

## Import from a repository

An importing AppHost references the producer's contract package and calls the generated import method:

```csharp
var orders = builder.ImportOrdersModule();
```

The container-only contract above needs no source checkout. When a contract declares a repository-relative project or another repository-backed resource, configure the producer repository in the consumer:

```csharp
module.AddProject(
        "orders-worker",
        "src/Orders.Worker/Orders.Worker.csproj",
        ModuleProjectPathBase.Repository)
    .ExportAsContainer("orders-worker");
```

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "Modules": {
        "orders": {
          "Repository": "https://github.com/example/orders.git"
        }
      }
    }
  }
}
```

Remote repository operations are explicit. From the AppHost directory, initialize once, then run normally:

```bash
aspire do initialize --apphost . --non-interactive
aspire run
```

If initialization has not run, AppHost startup fails with the state path and the exact recovery command. Module declaration itself does not clone, fetch, or build anything.

Remote imports require Git. GitHub HTTPS repositories also require GitHub CLI because it is used as a process-scoped credential provider.

| Repository declaration | Checkout behavior |
| --- | --- |
| Existing unpinned local path | Used directly; initialization does not manage it. |
| Unpinned remote | Uses a sibling named after the remote repository. An existing matching checkout is adopted and never updated; a newly cloned checkout may be fast-forwarded by later initialization. |
| Repository plus revision | Uses an isolated hashed sibling so a developer checkout is not moved or detached. |

Initialization records credential-free ownership state under `~/.aspire/deployments/<apphost-sha>/modular-apphosts.json`. Repository-name collisions and origin mismatches fail rather than selecting or overwriting a checkout; set `CheckoutDirectoryName` to a single safe directory name when an unpinned remote needs an explicit sibling. Repository-relative project and build paths cannot escape the selected checkout.

Repository values can come from `WithRepository(...)`, `DistributedApplicationModuleOptions`, or normal .NET configuration. Use `GetRepositoryConfigurationKey(moduleName)` when constructing the configuration key in code.

## Require host tools

Use a required-tool resource when another resource must wait for a command on the AppHost machine:

```csharp
var git = builder.AddRequiredTool("git-cli", "git")
    .WithWebsite("https://git-scm.com/downloads")
    .WithInstallCommand("brew", "install", "git");

var orders = builder.ImportOrdersModule();
orders.Api.WaitFor(git);
```

The resource's health reflects whether the command resolves. Its website and installer appear as dashboard commands, and `aspire do initialize` runs the installer before repository steps. Installer commands receive an executable and argument list directly; they do not run through a shell. Required-tool resources are excluded from deployment manifests.

See the [remote initialization sample](../samples/RemoteInitialization/README.md) for a runnable, platform-aware setup.

## Generated API

| Contract declaration | Generated API |
| --- | --- |
| `[GenerateDistributedApplicationModule("orders")]` on `OrdersModule` | Typed `OrdersModule.Module` wrapper and identity validation. |
| Conventional `Define(IDistributedApplicationModuleBuilder)` | `AddOrdersModule()` and `ImportOrdersModule(...)` overloads for AppHosts and module builders. |
| `ApiResourceName` used in a supported resource call | `OrdersModule.Module.Api` with the declared Aspire resource type. |
| `OrdersModule.Reference(module)` | Version-checked typed reference from another module definition. |

The annotated type must be a top-level, non-generic, static partial class. Resource names must be compile-time strings in recognized `AddProject`, `AddContainer`, or `AddResource<TResource>` calls. The generator reports invalid declarations, unsupported names, generated-member collisions, and inaccessible custom resource types as build diagnostics.

`Version` is an exact contract identifier. Change it when resource names, exposed types, required configuration, endpoints, or materialization behavior become incompatible; a source revision or image rebuild alone does not change the contract. The generator requires .NET SDK 10.0.100 or later.

For dynamic contracts, use `DefineModule` or `ExportModule` and the untyped `AddModule`/`ImportModule` APIs. The raw import does not register a definition for you.

### Compose modules

Add a dependency from the dependent module definition:

```csharp
public static void Define(IDistributedApplicationModuleBuilder module)
{
    var catalog = module.AddCatalogModule();

    module.AddResource<ProjectResource>(ApiResourceName, context =>
        context.ApplicationBuilder
            .AddProject(context.ResourceName, "src/Orders.Api/Orders.Api.csproj")
            .WithEnvironment("Catalog__Endpoint", catalog.Api.GetEndpoint("http"))
            .WaitFor(catalog.Api));
}
```

The AppHost only needs to add the parent. Dependencies are materialized before its own resources, so callbacks can use their typed resources normally:

```csharp
var orders = builder.AddOrdersModule();
```

Use `module.ImportCatalogModule()` to materialize the dependency as an import, or pass `ModuleImportOptions` to prefix or alias its resources. Import options are captured when the parent is defined. Composition-only modules are valid, and transitive dependencies are materialized once in dependency order. Adding or importing the same dependency elsewhere with conflicting local/import or naming options fails explicitly.

Dynamic definitions can compose an existing definition with `module.AddModule(dependency)` or `module.ImportModule(dependency, options)`. They can also define and compose a dependency in one call by supplying its name, version, package ID, and definition callback. `CatalogModule.Reference(module)` remains available for version-checked access when the AppHost materializes the dependency separately.

## Configure a module

Definitions receive the AppHost's `IConfiguration` through `module.Configuration`. `module.ConfigurationSection` points to `Aspire:ModularAppHosts:Modules:<module-name>`, and `module.GetOptions<T>()` binds that section while preserving property defaults:

```csharp
public sealed class OrdersModuleOptions
{
    public string Region { get; set; } = "local";
}

public static void Define(IDistributedApplicationModuleBuilder module)
{
    var options = module.GetOptions<OrdersModuleOptions>().Value;
    module.AddContainer("orders-api", "example/orders-api")
        .Configure((_, container) =>
            container.WithEnvironment("REGION", options.Region));
}
```

Configure the builder before adding or importing the module. Use `GetModuleConfigurationKey(moduleName)` when constructing the section key programmatically.

## Prefix resource names on import

Imports can adapt resource names without changing typed lookups:

```csharp
var import = new ModuleImportOptions { ResourcePrefix = "sales-" };
import.ResourceAliases[OrdersModule.CacheResourceName] = "shared-cache";

var orders = builder.ImportOrdersModule(import);
// orders.Api resolves "sales-orders-api"; orders.Cache resolves "shared-cache".
```

Unknown aliases, duplicate results, and collisions with existing AppHost resources fail before any module resource is added.

## Resource kinds

### Containers

Use `AddContainer` for an existing image. Its callback can resolve resources declared earlier in the same module:

```csharp
module.AddResource<ParameterResource>("cache-password", context =>
    context.ApplicationBuilder.AddParameter(context.ResourceName, secret: true));

module.AddContainer("orders-cache", "redis", "8-alpine")
    .Configure((context, container) => container
        .WithEnvironment(
            "REDIS_PASSWORD",
            context.GetResource<ParameterResource>("cache-password"))
        .WithEndpoint(targetPort: 6379, name: "tcp"));
```

### Projects

For a packaged contract, resolve project paths from the producer repository so consumers do not need the same source-tree layout:

```csharp
module.AddProject(
        "orders-api",
        "src/Orders.Api/Orders.Api.csproj",
        ModuleProjectPathBase.Repository)
    .ExportAsContainer("orders-api");
```

The two-argument `AddProject(name, projectPath)` and generated `AddProject<TProject>` metadata remain relative to the defining AppHost. `ConfigureProject` applies project-mode configuration, while the optional `ExportAsContainer` callback applies container-only configuration.

Use a generic factory when the project does not need a portable container representation:

```csharp
module.RequiresRepository();
module.AddResource<ProjectResource>("orders-api", context =>
    context.ApplicationBuilder.AddProject(
        context.ResourceName,
        Path.Combine(context.RepositoryPath, "src", "Orders.Api", "Orders.Api.csproj")));
```

Factories may compose paths during AppHost model construction but should not read repository content. Preflight validates the checkout and files after initialization can construct the pipeline.

### Other Aspire resources

`AddResource<TResource>` supports first-party integrations, community integrations, and custom resource types. For example, after referencing the `Aspire.Hosting.PostgreSQL` integration:

```csharp
module.AddResource<PostgresServerResource>("postgres", context =>
    context.ApplicationBuilder.AddPostgres(context.ResourceName));
```

Factories run in declaration order and must return a resource named `context.ResourceName`. Use `context.GetResource<TResource>()` only for earlier resources. Prefixes and aliases do not rewrite literal configuration or fixed host ports, so derive related names from the context and prefer dynamically allocated ports.

See [Module images](module-images.md) for native project and Dockerfile publishing, advanced image commands, pull mappings, and separate image-build repositories.

## AppHost configuration

Options bind from `Aspire:ModularAppHosts`. Resource values override module values, which override global defaults:

```json
{
  "Aspire": {
    "ModularAppHosts": {
      "ProjectMode": "Auto",
      "UpdateRepositoriesOnInitialize": true,
      "Modules": {
        "orders": {
          "Repository": "https://github.com/example/orders.git",
          "ProjectMode": "Container",
          "Projects": {
            "orders-api": {
              "ProjectMode": "Project"
            }
          }
        }
      }
    }
  }
}
```

- Global options include `GitExecutablePath`, `GitHubCliPath`, `RepositoryCommandTimeout`, `ImageBuildTimeout`, `ImageTransferTimeout`, `UpdateRepositoriesOnInitialize`, `RefreshBuildRepositoriesOnRun`, and `ProjectMode`.
- Module options include `Repository`, `RepositoryRevision`, `CheckoutDirectoryName`, `UpdateRepositoryOnInitialize`, `ProjectMode`, `Projects`, and `Containers`.
- Project and container options control image identity and publishing; project options also control launch-profile and endpoint behavior. See [Module images](module-images.md) for exact publisher settings and constraints.

`ProjectMode.Auto` runs specialized exported projects from local modules as projects and those from imported modules as containers. Generic resource factories are unchanged. Publish mode always uses the declared container representation. A complete external image identity with publishing disabled can remove an image resource's source dependency.

The same options can be configured in code before any module is defined or materialized:

```csharp
builder.ConfigureModularAppHosts(options =>
{
    options.Modules[OrdersModule.Name] = new DistributedApplicationModuleOptions
    {
        Repository = "https://github.com/example/orders.git",
        RepositoryRevision = "v2.0.0"
    };
});
```

### Developer-local mode switching

Specialized module projects add pipeline steps that persist a developer's next run mode in the AppHost's user secrets. Initialize user secrets once, then choose a global or resource-specific mode:

```bash
dotnet user-secrets init --project src/MyApp.AppHost/MyApp.AppHost.csproj
aspire do use-containers --apphost src/MyApp.AppHost --non-interactive
aspire do use-project-orders-api --apphost src/MyApp.AppHost --non-interactive
```

Stop and restart the AppHost after changing a mode; the step does not restart resources. Use `use-configured-<resource>` to remove one resource override or `use-configured-modes` to clear all temporary overrides. Native `ExportAsContainer(...)` projects need their image built before selecting container mode when it is not already available to Docker or Podman.

For checked-in, AppHost-wide intent, configure `ProjectMode` or call `UseLocalModuleProjects()` / `UseModuleContainers()` before materializing modules.
