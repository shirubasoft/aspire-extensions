# Shirubasoft.Aspire.CloudflareTunnels

Add named Cloudflare Tunnels and account-free Quick Tunnels to an Aspire AppHost.

## Install

```bash
dotnet add package Shirubasoft.Aspire.CloudflareTunnels
```

The package targets .NET 10 and Aspire 13.5.3 or later within the Aspire 13.5 line.

## Quick Tunnels

A Quick Tunnel publishes one endpoint through a temporary `trycloudflare.com` URL. It needs no Cloudflare account or token.

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddContainer("web", "nginx", "1.29-alpine")
    .WithHttpEndpoint(targetPort: 80);

builder.AddCloudflareQuickTunnel("web-tunnel")
    .WithReference(web);

await builder.Build().RunAsync();
```

The public URL appears on the Quick Tunnel resource in the Aspire dashboard after `cloudflared` connects. Aspire excludes Quick Tunnels from deployment manifests. Each Quick Tunnel accepts one target endpoint.

Pass an endpoint name when the target does not use `http`:

```csharp
quickTunnel.WithReference(web, endpointName: "admin");
```

## Named tunnels

A named tunnel can publish several resources under hostnames in a Cloudflare account.

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var tunnel = builder.AddCloudflareTunnel("development");

var api = builder.AddContainer("api", "nginx", "1.29-alpine")
    .WithHttpEndpoint(targetPort: 80);

var frontend = builder.AddContainer("frontend", "nginx", "1.29-alpine")
    .WithHttpEndpoint(targetPort: 80);

api.WithCloudflareTunnel(tunnel, "api.example.com");
frontend.WithCloudflareTunnel(tunnel, "app.example.com");

await builder.Build().RunAsync();
```

In run mode, Aspire prompts for these parameters:

| Parameter | Secret | Purpose |
| --- | --- | --- |
| `development-account-id` | No | Selects the Cloudflare account. |
| `development-api-token` | Yes | Creates the tunnel, writes DNS records, and updates ingress rules. |

The integration reuses a tunnel with the requested name. It creates the tunnel when none exists, retrieves its connector token, upserts each CNAME record, and replaces the AppHost-managed ingress rules. Ingress rules that use other hostnames remain unchanged.

Aspire keeps the connector token inside the tunnel resource and passes it directly to `cloudflared` through the secret `TUNNEL_TOKEN` environment variable. Application code uses the resource builder without handling the token.

The API token needs these Cloudflare permissions:

| Scope | Permission |
| --- | --- |
| Account | Cloudflare Tunnel: Edit |
| Zone | Zone: Read |
| Zone | DNS: Edit |

Use `metricsPort` to bind the `cloudflared` metrics endpoint to a fixed host port:

```csharp
var tunnel = builder.AddCloudflareTunnel(
    "development",
    metricsPort: 60123);
```

Each published hostname appears as a route resource named `{tunnel}-route-{hostname}`, with dots in the hostname replaced by hyphens. Aspire resource names have at most 64 characters. Pass `routeName` when the generated name is too long:

```csharp
web.WithCloudflareTunnel(
    tunnel,
    "my-application.staging.example.com",
    routeName: "web-route");
```

## Deployment pipeline

The named tunnel contributes a Cloudflare route step, `configure-{name}-cloudflare-routes`, to Aspire's deploy pipeline. The step runs after the tunnel and its targets finish deploying. If a deployment step fails, the route step does not run, and the existing DNS records and ingress rules stay unchanged. Running the route step by name, with `aspire do configure-{name}-cloudflare-routes` or the AppHost's `--step` argument, also runs the deployment steps that it waits for.

The route step waits for the deployment steps of the compute environments that host the tunnel and its targets:

| Compute environment | Deployment step tag |
| --- | --- |
| Docker Compose | `docker-compose-up` |
| Kubernetes | `helm-deploy` |
| Azure Container Apps and Azure App Service | `deploy-compute` |

A custom compute environment declares its deployment step by tagging it with `WellKnownPipelineTags.DeployCompute`. The step must belong to the environment, to the deployment target that the environment assigns to each resource, or to the resource itself. An AppHost declares the deployment step of an environment from another package by tagging that step in a pipeline configuration callback:

```csharp
#pragma warning disable ASPIREPIPELINES001
builder.Pipeline.AddPipelineConfiguration(context =>
{
    foreach (var step in context.GetSteps(environment.Resource, "environment-deploy"))
    {
        step.Tags.Add(WellKnownPipelineTags.DeployCompute);
    }

    return Task.CompletedTask;
});
#pragma warning restore ASPIREPIPELINES001
```

If the tunnel or a target deploys to a compute environment without such a step, the route step fails before it contacts Cloudflare. Its error names the compute environment and the tags it looked for.

Before deployment:

1. Create the named tunnel in Cloudflare.
2. Supply `{name}-account-id` and `{name}-api-token`.
3. Supply `{name}-tunnel-token` for the deployed `cloudflared` connector.
4. Assign every published target resource to an Aspire compute environment.

The pipeline resolves each target's deployed endpoint, upserts its DNS record, and updates the tunnel ingress configuration. It fails when the tunnel, Cloudflare zone, deployment target, or deployed endpoint cannot be resolved.

Route configuration is not transactional. The step upserts the DNS record of each route in turn and then writes the ingress configuration once. If a route or the ingress update fails, the DNS records that the step already upserted stay in place.

In a Docker Compose environment, `cloudflared` reaches each target on the Compose network, so the ingress rule uses the container port, such as `http://web:8080`. Give each published endpoint a fixed container port, for example `WithHttpEndpoint(targetPort: 8080)`. If an endpoint has no fixed port, Compose assigns one that the route step cannot discover. The ingress rule then uses the endpoint's default port, and the pipeline completes with a warning that names the endpoint.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/CloudflareTunnels.AppHost/CloudflareTunnels.AppHost.csproj
```

The sample starts nginx and publishes it through a Quick Tunnel. Open the public URL from the Aspire dashboard.

## Package contents

The NuGet package includes the runtime assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
