# Shirubasoft.Aspire.Extensions.ResourceGroups

Group related Aspire resources under logical parents without replacing their standard registration methods.

## Install

Add the package to an Aspire AppHost:

```bash
dotnet add package Shirubasoft.Aspire.Extensions.ResourceGroups
```

The package targets .NET 10 and Aspire 13.5.3 or later within the Aspire 13.5 line.

## Group resources

`AddResourceGroup` returns an `IResourceGroupBuilder`, which implements `IDistributedApplicationBuilder`. Call the usual Aspire registration methods on it:

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddResourceGroup("backend");
var api = backend.AddContainer("api", "alpine");
var worker = backend.AddContainer("worker", "alpine");

await builder.Build().RunAsync();
```

The dashboard shows `api` and `worker` under `backend`. The group is a logical resource, so Aspire omits it from published manifests and does not start a process or container for it.

A resource's own parent takes precedence over the group. This preserves child relationships created by Aspire integrations and explicit `WithParentRelationship` calls:

```csharp
var data = builder.AddResourceGroup("data");
var database = data.AddContainer("database", "postgres", "18-alpine");

data.AddContainer("migrations", "alpine")
    .WithParentRelationship(database);
```

Here, `database` belongs to `data`, while `migrations` belongs to `database`.

Groups can also contain groups:

```csharp
var platform = builder.AddResourceGroup("platform");
var observability = platform.AddResourceGroup("observability");

observability.AddContainer("collector", "otel/opentelemetry-collector-contrib");
```

## Run the sample

From this extension folder, run:

```bash
aspire run --project samples/ResourceGroups.AppHost/ResourceGroups.AppHost.csproj
```

The dashboard shows a backend group with an API, database, and a migrations child under the database.

## Package contents

The NuGet package includes the assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
