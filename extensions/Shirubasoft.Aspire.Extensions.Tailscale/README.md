# Shirubasoft.Aspire.Tailscale

Expose Aspire resources privately on a Tailscale tailnet with HTTPS. Each exposed resource gets a Tailscale sidecar node that serves `https://<hostname>.<tailnet>.ts.net` and proxies to the resource over the container network.

## Install

```bash
dotnet add package Shirubasoft.Aspire.Tailscale
```

The package targets .NET 10 and Aspire 13.6.1 or later within the Aspire 13.6 line.

## Usage

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var tailnet = builder.AddTailnet("tailnet");

var admin = builder.AddProject<Projects.Admin>("admin");
admin.WithTailscale(tailnet, hostname: "quadra-admin");

var grafana = builder.AddContainer("grafana", "grafana/grafana", "12.3.1")
    .WithHttpEndpoint(targetPort: 3000);
grafana.WithTailscale(tailnet, hostname: "quadra-grafana", tags: ["tag:observability"]);

await builder.Build().RunAsync();
```

`AddTailnet` adds the secret parameter `{name}-oauth-client-secret` and defines the tags that nodes advertise. The default tag list is `tag:apps`.

`WithTailscale` adds a sidecar container named `{resource}-ts` that joins the tailnet as `hostname` and serves the resource endpoint. Pass `endpointName` when the resource does not use `http`, and `tags` to advertise different tags than the tailnet default. Every tag must start with `tag:`. The hostname must be a lowercase DNS label of at most 59 characters.

The sidecar waits for the resource and appears under it in the Aspire dashboard.

## Run mode

`aspire run` starts each sidecar as an ephemeral node named `{hostname}-dev`, so a workstation never takes the production hostname. The node uses in-memory state and leaves the tailnet when the container stops.

Aspire prompts for `{tailnet}-oauth-client-secret` when it has no value. Supply it through user secrets or configuration to skip the prompt:

```bash
dotnet user-secrets set Parameters:tailnet-oauth-client-secret tskey-client-...
```

A project resource runs on the host, so its sidecar proxies to the host address that Aspire exposes to containers. A container resource is reached by name on the Aspire container network.

## Docker Compose deployment

`aspire publish` and `aspire deploy` with `AddDockerComposeEnvironment` generate a Compose service for each sidecar:

- The image is `docker.io/tailscale/tailscale` pinned to one version.
- `TS_AUTHKEY` is the OAuth client secret from the `.env` file with `?ephemeral=false&preauthorized=true`, so the node persists and needs no manual approval.
- `TS_HOSTNAME` is the requested hostname without a suffix.
- `TS_STATE_DIR` points to the named volume `{resource}-ts-state`, so the node identity survives container recreation.
- The serve configuration proxies HTTPS on port 443 to `http://{resource}:{target port}` on the Compose network. The sidecar writes it from an environment variable at start, so the generated Compose file needs no bind mounts and works with a remote `DOCKER_HOST`.
- The sidecar `depends_on` the resource service.

The target port comes from the endpoint declaration. A container endpoint needs `targetPort`, or `port` when the container listens on the published port. A project resource uses the default container port `8080`. Publishing fails with an error that names the resource and endpoint when the endpoint has no fixed target port.

## Prerequisites

1. In the tailnet policy file, declare an owner for every tag that nodes advertise:

   ```json
   {
     "tagOwners": {
       "tag:apps": ["autogroup:admin"]
     }
   }
   ```

2. Create an OAuth client in the Tailscale admin console with the `auth_keys` scope (write) restricted to those tags. Use its secret as `{tailnet}-oauth-client-secret`. The secret acts as an auth key and registers tag-owned nodes.
3. Enable MagicDNS and HTTPS certificates for the tailnet. `tailscale serve` obtains a certificate for each node name.
4. Let the sidecar reach the Tailscale control plane and DERP relays over outbound HTTPS.

## Caveats

- The sidecar uses userspace networking (`TS_USERSPACE=true`), so it runs without `/dev/net/tun` or extra capabilities, including inside unprivileged LXC containers. Only the configured endpoint is reachable; the sidecar is not a subnet router or exit node.
- A deployed node stays in the tailnet until it is removed from the admin console or its state volume is deleted. Run-mode nodes are ephemeral and disappear shortly after the container stops.
- Tailscale certificates and MagicDNS names are issued per node. Two nodes cannot share a hostname; Tailscale appends a numeric suffix to the second one.
- Compose generation requires the serve configuration to escape `${TS_CERT_DOMAIN}` as `$${TS_CERT_DOMAIN}`. The sidecar receives the literal placeholder and Tailscale replaces it with the node's MagicDNS name.
- Publishing targets Docker Compose. Other compute environments receive the same configuration and are not supported.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/Tailscale.AppHost/Tailscale.AppHost.csproj
```

The sample starts `traefik/whoami` on port 8080 and exposes it as `aspire-whoami-dev` on the tailnet. Enter the OAuth client secret when the dashboard asks for `tailnet-oauth-client-secret`, then open `https://aspire-whoami-dev.<tailnet>.ts.net` from any device on the tailnet.

## Package contents

The NuGet package includes the runtime assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
