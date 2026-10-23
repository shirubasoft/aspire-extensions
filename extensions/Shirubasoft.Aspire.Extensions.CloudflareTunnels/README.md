# Shirubasoft.Aspire.CloudflareTunnels

Add named Cloudflare Tunnels and account-free Quick Tunnels to an Aspire AppHost.

## Install

```bash
dotnet add package Shirubasoft.Aspire.CloudflareTunnels
```

The package targets .NET 10 and the Aspire version pinned in the repository's [Directory.Packages.props](https://github.com/Shirubasoft/aspire-extensions/blob/main/Directory.Packages.props). Each release's NuGet dependencies list the exact Aspire version it requires.

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

### Protected Quick Tunnels

`WithAllowedMail` limits a Quick Tunnel to visitors who verify an allowed email address:

```csharp
builder.AddCloudflareQuickTunnel("web-tunnel")
    .WithReference(web)
    .WithAllowedMail("alice@example.com", "*@example.org");
```

Each rule is an email address or a `*@domain` wildcard that allows every address in that domain. A value can also hold a comma-separated list, such as `"alice@example.com,bob@example.com"`. Repeated calls add rules. Aspire trims and lowercases each rule, ignores duplicates, and passes each rule to `cloudflared` as an `--allowed-mail` argument. Builder calls throw `ArgumentException` for an empty or malformed rule, for a rule longer than 320 bytes, and when the tunnel would have more than 100 rules. Internationalized domains must use their punycode form, such as `*@xn--bcher-kva.example`.

A protected tunnel still needs no Cloudflare account. Visitors open the public URL in a browser, enter an allowed email address, and confirm a one-time PIN that Cloudflare sends to it. Clients that cannot complete this interactive browser flow, such as webhook senders, API clients, and external uptime probes, cannot reach the target. The resource health check reads the local `cloudflared` metrics endpoint and is unaffected. The rules apply to the running Quick Tunnel only, and access ends when the `cloudflared` container stops. Changing the rules requires a new Quick Tunnel, which gets a new public URL.

Recipient rules are container arguments and are visible in the Aspire dashboard and container inspection tools. Protected Quick Tunnels require `cloudflared` 2026.9.2 or later.

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

## Externally managed routes

Opt into connector-only publish mode when a trusted onboarding process owns the
named tunnel, ingress and DNS:

```csharp
var tunnel = builder.AddCloudflareTunnelConnector("production");
web.WithCloudflareTunnel(tunnel, "app.example.com");
```

Publish mode requires only the secret `production-tunnel-token` parameter and
passes it to `cloudflared` as `TUNNEL_TOKEN`. It adds no Cloudflare route pipeline
step and makes no Cloudflare API calls. Provision the tunnel, its ingress to the
deployed service and target port, the catch-all 404, and the proxied DNS CNAME
before deploying. Reconfigure them through that onboarding process when routes
change. Route resources still record declared hostnames and target endpoints.

`AddCloudflareTunnel` keeps its default managed behavior.
`AddCloudflareTunnelConnector` changes publish mode only; named tunnels in run
mode still use account and API credentials. An AppHost can keep using a Quick
Tunnel in run mode and choose connector-only mode in publish mode.

The sample keeps its Quick Tunnel defaults. Set
`Cloudflare__ExternallyManagedRoutes=true` when publishing it to exercise the
connector-only declaration, after onboarding `web.example.com` and supplying
`Parameters__web_tunnel_tunnel_token`.

### Onboard a tunnel with the Cloudflare CLI

Run this setup once per tunnel, outside the AppHost, from a trusted operator
workstation or CI job. The AppHost only consumes the resulting connector token.
The connector-only AppHost does not update Cloudflare. Rerun the ingress step
whenever a published hostname, deployed service name, or target port changes,
and create a DNS record for each new hostname.

The recipe uses the [Cloudflare CLI](https://developers.cloudflare.com/cf/) at
the pinned version `1.0.0-beta.14`, Node.js 22.18 or later, and `jq`. The `cf`
CLI is in beta, and its commands can change between versions. Pin the version
deliberately and move the pin only after you check these commands against the
new version. Run the Bash blocks in the same shell session. Strict error handling
stops the recipe if a Cloudflare command or JSON extraction fails. Bash runs the
pinned package without a global install:

```bash
set -euo pipefail
cf() { npm exec --yes --package=cf@1.0.0-beta.14 -- cf "$@"; }
export CF_SEND_TELEMETRY=false
```

Create an API token in the Cloudflare dashboard with these permissions, then
supply it without placing it in shell history:

| Scope | Permission |
| --- | --- |
| Account | Cloudflare Tunnel: Edit |
| Zone | Zone: Read |
| Zone | DNS: Edit |

```bash
read -rsp 'Cloudflare API token: ' CLOUDFLARE_API_TOKEN
export CLOUDFLARE_API_TOKEN
```

Select the account that owns the zone. When the token can access several accounts
and no account is selected, `cf` prompts in an interactive terminal and fails in a
non-interactive job:

```bash
cf accounts list | jq -r '.[] | "\(.id)\t\(.name)"'
read -rp 'Cloudflare account ID: ' CLOUDFLARE_ACCOUNT_ID
export CLOUDFLARE_ACCOUNT_ID
```

Create the remotely managed tunnel and capture its ID:

```bash
tunnel_id="$(cf tunnels create --name production --config-source cloudflare \
  | jq -er '.id | select(type == "string" and length > 0)')"
```

Replace the tunnel ingress with every published hostname, followed by the
catch-all 404 rule. The service is the address at which the deployed
`cloudflared` container reaches the target. In Docker Compose, that is the
service name and container port:

```bash
cf tunnels config update "$tunnel_id" --body "$(jq -cn \
  --arg hostname app.example.com \
  --arg service http://web:8080 \
  '{config: {ingress: [{hostname: $hostname, service: $service}, {service: "http_status:404"}]}}')"
```

`cf tunnels config update` replaces the whole configuration, so the body must
list every route each time it runs.

Create a proxied CNAME record for each hostname in its zone:

```bash
zone_id="$(cf zones list --name example.com --account-id "$CLOUDFLARE_ACCOUNT_ID" \
  | jq -er 'if length == 1 then .[0].id | select(type == "string" and length > 0)
    else error("Expected exactly one matching Cloudflare zone") end')"
cf dns records create --zone "$zone_id" --body "$(jq -cn \
  --arg name app.example.com \
  --arg content "$tunnel_id.cfargotunnel.com" \
  '{type: "CNAME", name: $name, content: $content, proxied: true}')"
```

In `cf` 1.0.0-beta.14, `cf tunnels config update` and `cf dns records create`
accept these payloads only through `--body`. To preview an API request without
sending it, run that `cf` command directly with `--dry-run`, without piping its
preview into the ID or token extraction. A route that changes its hostname needs
a new CNAME record; edit or delete the old record in the Cloudflare dashboard.

Retrieve the connector token straight into the secret Aspire parameter, then
deploy. The token stays out of the terminal and the process arguments:

```bash
Parameters__production_tunnel_token="$(cf tunnels token get "$tunnel_id" \
  | jq -er 'select(type == "string" and length > 0)')"
export Parameters__production_tunnel_token
aspire deploy --apphost path/to/AppHost.csproj
```

For CI, store the token in the pipeline's secret store and expose it as
`Parameters__production_tunnel_token` in the deployment job.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/CloudflareTunnels.AppHost/CloudflareTunnels.AppHost.csproj
```

The sample starts nginx and publishes it through a Quick Tunnel. Open the public URL from the Aspire dashboard.

Set the `Cloudflare:AllowedMail` array in the sample's `appsettings.json` to protect its Quick Tunnel. Each entry holds one email address or domain wildcard. The checked-in empty array keeps the sample unprotected:

```json
{
  "Cloudflare": {
    "AllowedMail": ["alice@example.com", "*@example.org"]
  }
}
```

Supply array entries through indexed environment variables:

```bash
Cloudflare__AllowedMail__0='alice@example.com' \
Cloudflare__AllowedMail__1='*@example.org' \
  aspire run --project samples/CloudflareTunnels.AppHost/CloudflareTunnels.AppHost.csproj
```

## Package contents

The NuGet package includes the runtime assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
