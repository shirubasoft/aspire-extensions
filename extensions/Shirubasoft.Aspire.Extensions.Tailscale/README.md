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

`WithTailscale` adds a sidecar container named `{resource}-ts` that joins the tailnet as `hostname` and serves the resource endpoint. Pass `endpointName` when the resource does not use `http`, and `tags` to advertise different tags than the tailnet default. A tag is `tag:` followed by a letter and then letters, digits, or hyphens, which is the grammar Tailscale accepts. Tags form a set: the node advertises them sorted and without duplicates. The hostname must be a lowercase DNS label of at most 59 characters.

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
- `TS_STATE_DIR` points to the named volume `{resource}-ts-state`, so the node identity survives container recreation. Keep the volume to keep the identity.
- `TAILSCALE_TAGS` lists the tag set. At start the sidecar reads the node profile in `tailscaled.state` on the state volume and refuses to start when that profile registered with a different tag set, so a changed `tags` value never runs under the old identity. Fresh registration accepts a missing file, the pinned image's initial `{}` store, or a store containing only its machine key. Registered state must contain exactly one Linux profile: the `_profiles` map key, embedded `ID`, `Key`, `_current-profile`, and existing prefs key must agree. Legacy state, multiple profiles, orphan profile keys, incomplete registration and empty files require resetting the state volume. The sidecar validates the complete two-space-indented base64 store, compact profile metadata and tab-indented prefs written by the pinned image before comparing tags. Escaped or duplicate keys, malformed structure, and other serialization layouts stop the sidecar with an error naming the reason. Decoded prefs accept LF or CRLF line endings and preserve whitespace inside JSON strings.
- Startup requires the generated `TS_AUTH_ONCE=true`, `TS_USERSPACE=true` and `TS_EXTRA_ARGS=--advertise-tags=<TAILSCALE_TAGS>`. Alternate daemon arguments, config files and state stores stop the sidecar. Disk netmap cache replay is disabled so an unchecked cached node cannot supply startup tags.
- `TAILSCALE_START_SCRIPT` carries the start script and the entrypoint evaluates it. Compose doubles every `$` in the value, so the script reaches the shell unchanged.
- The serve configuration proxies HTTPS on port 443 to `http://{resource}:{target port}` on the Compose network. The sidecar writes it from an environment variable at start, so the generated Compose file needs no bind mounts and works with a remote `DOCKER_HOST`.
- The serve configuration names the node certificate domain with the Tailscale placeholder `${TS_CERT_DOMAIN}`, written as `$${TS_CERT_DOMAIN}` so Compose passes it through unchanged.
- The sidecar `depends_on` the resource service.
- One sidecar owns one state volume. The start script only reads the volume, but two daemons sharing one identity are unsupported.

The target port comes from the endpoint declaration. A container endpoint needs `targetPort`, or `port` when the container listens on the published port. A project resource uses the default container port `8080`. Publishing fails with an error that names the resource and endpoint when the endpoint has no fixed target port, and with an error that names the resource and compute environment when the resource publishes to anything other than Docker Compose.

### Changing tags on a deployed node

Tailscale assigns tags when it registers a node, and the node profile on the state volume keeps that tag set. A changed `tags` value in the AppHost therefore means a new registration. To change the tags of a deployed node:

1. Stop the sidecar, so a restart policy does not keep restarting the refused container:

   ```bash
   docker compose stop web-ts
   ```

2. Remove the node on the Machines page of the Tailscale admin console, or through the API (`DELETE /api/v2/device/{id}`).
3. Delete the state volume. Compose names it `{project}_web-ts-state`, where the project name defaults to the directory of the Compose file:

   ```bash
   docker compose rm -f web-ts
   docker volume rm myapp_web-ts-state
   ```

4. Set the new `tags` in the AppHost and deploy again. The sidecar registers a new node with the new tags.

[Applying a tag to a device](https://tailscale.com/docs/features/tags#apply-a-tag-to-a-device) in the admin console changes the device without a new registration. The AppHost `tags` then keep describing the registration, because the sidecar compares them with the registered profile, not with the tags the admin console shows.

### Removing a deployed node

Deleting the state volume does not remove the node from the tailnet. The next deployment registers a new node, Tailscale appends a numeric suffix to its hostname, and the old node remains until it expires or is removed. Stop the sidecar, remove the node in the Tailscale admin console or through the API, then delete the state volume so the next deployment registers a fresh node. See [`TS_STATE_DIR`](https://tailscale.com/docs/features/containers/docker/docker-params#ts_state_dir) in the Tailscale documentation.

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
- A deployed node stays in the tailnet until it is removed in the admin console or through the API. Run-mode nodes are ephemeral and disappear shortly after the container stops.
- Tailscale certificates and MagicDNS names are issued per node. Two nodes cannot share a hostname; Tailscale appends a numeric suffix to the second one.
- Publishing targets Docker Compose. Other Aspire compute environments are not supported.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/Tailscale.AppHost/Tailscale.AppHost.csproj
```

The sample starts `traefik/whoami` on port 8080 and exposes it as `aspire-whoami-dev` on the tailnet. Enter the OAuth client secret when the dashboard asks for `tailnet-oauth-client-secret`, then open `https://aspire-whoami-dev.<tailnet>.ts.net` from any device on the tailnet.

## Package contents

The NuGet package includes the runtime assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
