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

## Deployment pipeline

The named tunnel contributes a Cloudflare route step between Aspire's publish and deploy steps. Before deployment:

1. Create the named tunnel in Cloudflare.
2. Supply `{name}-account-id` and `{name}-api-token`.
3. Supply `{name}-tunnel-token` for the deployed `cloudflared` connector.
4. Assign every published target resource to an Aspire compute environment.

The pipeline resolves each target's deployed endpoint, upserts its DNS record, and updates the tunnel ingress configuration. It fails when the tunnel, Cloudflare zone, deployment target, or deployed endpoint cannot be resolved.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/CloudflareTunnels.AppHost/CloudflareTunnels.AppHost.csproj
```

The sample starts nginx and publishes it through a Quick Tunnel. Open the public URL from the Aspire dashboard.

## Package contents

The NuGet package includes the runtime assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
