# Shirubasoft.Aspire.Hosting.TigerBeetle

`Shirubasoft.Aspire.Hosting.TigerBeetle` adds a TigerBeetle replica and optional RabbitMQ change data capture job to an Aspire AppHost as custom container resources. It handles first-run formatting, polyglot connection properties, a TCP health check, persistent storage helpers, TypeScript AppHost exports, and Aspire container publication.

The package uses TigerBeetle `0.17.9` by default. It targets .NET 10 and the Aspire version pinned in the repository's [Directory.Packages.props](https://github.com/Shirubasoft/aspire-extensions/blob/main/Directory.Packages.props).

> [!WARNING]
> `AddTigerBeetle` creates one replica in TigerBeetle development mode. This is useful for local development, tests, and samples. It is not a production TigerBeetle topology. TigerBeetle recommends six replicas on separate machines and disks for production. See [production deployment](#production-deployment) before publishing this resource.

## Requirements

- The .NET SDK pinned in the repository's [global.json](https://github.com/Shirubasoft/aspire-extensions/blob/main/global.json) or a later patch
- The Aspire CLI pinned in [.config/dotnet-tools.json](https://github.com/Shirubasoft/aspire-extensions/blob/main/.config/dotnet-tools.json)
- Docker, Podman, or another Aspire-supported container runtime
- Node.js 20.19 or later for the TypeScript sample

## Install

Add the hosting integration to a C# AppHost:

```bash
dotnet add package Shirubasoft.Aspire.Hosting.TigerBeetle
```

Applications that connect from .NET should also reference the matching official client:

```bash
dotnet add package TigerBeetle --version 0.17.9
```

For a TypeScript AppHost, add the package to `aspire.config.json`:

```json
{
  "appHost": {
    "path": "apphost.mts",
    "language": "typescript/nodejs"
  },
  "packages": {
    "Aspire.Hosting.JavaScript": "<aspire-version>",
    "Shirubasoft.Aspire.Hosting.TigerBeetle": "<package-version>"
  }
}
```

Replace the placeholders with your Aspire version and the latest package version, then restore the AppHost. Aspire generates the TypeScript modules under `.aspire/modules`:

```bash
aspire restore --apphost . --non-interactive
```

Do not edit generated `.aspire/modules` files.

## C# AppHost

The smallest useful resource has persistent data and a small development cache:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume()
    .WithCacheGrid("256MiB");

builder.AddProject<Projects.LedgerApi>("ledger-api")
    .WithReference(tigerBeetle)
    .WaitFor(tigerBeetle);

builder.Build().Run();
```

`WithReference` injects language-neutral connection properties. `WaitFor` waits for the TCP health check before Aspire starts the dependent resource.

To use a fixed host port, pass it to `AddTigerBeetle`:

```csharp
var tigerBeetle = builder.AddTigerBeetle("tigerbeetle", port: 3000)
    .WithDataVolume();
```

The complete C# AppHost sample is in [`samples/TigerBeetle.AppHost/Program.cs`](samples/TigerBeetle.AppHost/Program.cs).

## Change data capture

TigerBeetle can publish transfers and balance changes to a pre-existing AMQP 0.9.1 exchange. Add the CDC job from its TigerBeetle parent resource:

```csharp
var rabbitMq = builder.AddRabbitMQ("rabbitmq");

var client = builder.AddProject<Projects.LedgerApi>("ledger-api")
    .WithReference(tigerBeetle)
    .WithReference(rabbitMq)
    .WaitFor(tigerBeetle)
    .WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health");

tigerBeetle.AddChangeDataCapture(
        "tigerbeetle-cdc",
        rabbitMq,
        "tigerbeetle")
    .WaitFor(client)
    .WithPublishRoutingKey("transfers")
    .WithCdcArgs("--event-count-max=100", "--idle-interval-ms=250");
```

The CDC resource is a child of TigerBeetle and always waits for TigerBeetle. The RabbitMQ overload also waits for RabbitMQ. In this example the client declares the exchange and queue before its health check succeeds, so waiting for the client prevents CDC from racing the topology declaration.

Use any `IResourceWithConnectionString` when RabbitMQ is managed outside the AppHost:

```csharp
var rabbitMq = builder.AddConnectionString("rabbitmq");

tigerBeetle.AddChangeDataCapture(
    "tigerbeetle-cdc",
    rabbitMq,
    "tigerbeetle");
```

This overload adds a reference relationship but does not add `WaitFor`. The connection string must be an `amqp://` or `amqps://` URI. Missing credentials default to `guest`/`guest`, missing ports default to `5672` for AMQP and `5671` for AMQPS, and a missing virtual-host path defaults to `/`. URI-escaped credentials and virtual hosts are decoded. Query and fragment components are ignored.

TigerBeetle CDC does not implement AMQP TLS. An `amqps://` URI logs a warning and uses the parsed host and port without TLS. Put a TLS tunnel between the CDC container and RabbitMQ for secure connections.

The equivalent generated TypeScript API accepts either a RabbitMQ resource or a generic connection-string resource:

```typescript
const rabbitMq = builder.addRabbitMQ('rabbitmq');

await tigerBeetle
  .addChangeDataCapture('tigerbeetle-cdc', rabbitMq, 'tigerbeetle')
  .withTimestampLast('0')
  .withCdcArgs(['--event-count-max=100']);
```

CDC uses at-least-once delivery. Consumers must handle duplicate events. `WithTimestampLast("0")` replays all available events, while omitting it resumes from the timestamp last acknowledged by RabbitMQ.

## TypeScript AppHost

The package exports the same resource and builder methods to generated TypeScript modules:

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tigerBeetle = await builder
  .addTigerBeetle('tigerbeetle')
  .withDataVolume()
  .withCacheGrid('256MiB');

await builder
  .addNodeApp('client', './client', 'dist/index.js')
  .withReference(tigerBeetle)
  .waitFor(tigerBeetle)
  .withHttpEndpoint({ env: 'PORT' });

await builder.build().run();
```

The complete TypeScript AppHost is in [`samples/TigerBeetle.TypeScript/apphost.mts`](samples/TigerBeetle.TypeScript/apphost.mts).

Install the official Node client in the consuming application. Pin it to the server version unless you have checked the compatibility range in the TigerBeetle release notes:

```bash
npm install --save-exact tigerbeetle-node@0.17.9
```

## Connection properties

Aspire's polyglot resource contract exposes each value separately. For a resource named `tigerbeetle`, `WithReference` injects these variables into the consumer:

| Variable | Example | Purpose |
| --- | --- | --- |
| `TIGERBEETLE_HOST` | `127.0.0.1` | Primary endpoint host |
| `TIGERBEETLE_PORT` | `43127` | Primary endpoint port |
| `TIGERBEETLE_CLUSTERID` | `0` | TigerBeetle cluster ID |
| `TIGERBEETLE_ADDRESSES` | `127.0.0.1:43127` | Comma-separated client addresses |
| `TIGERBEETLE_ADDRESSES__0` | `127.0.0.1:43127` | First client address; subsequent addresses use `__1`, `__2`, and so on |

The prefix comes from the resource name. A resource named `ledger` produces `LEDGER_HOST`, `LEDGER_PORT`, `LEDGER_CLUSTERID`, and `LEDGER_ADDRESSES`.

TigerBeetle clients take a cluster ID and address array as separate constructor values. They do not define a native connection-string format, so applications should read `ClusterId` and `Addresses` directly instead of parsing a package-specific string.

`TIGERBEETLE_ADDRESSES` is the language-neutral value used by Node.js, Python, Go, and other consumers. The indexed companions are also emitted so .NET's configuration binder can materialize the same ordered list as a `string[]`. Both representations come from the same resource model.

The custom `WithReference` integration intentionally does not inject `ConnectionStrings__tigerbeetle`. TigerBeetle has no native value for that variable, and inventing one would force every client to implement a package-specific parser.

TigerBeetle `0.17.9` accepts numeric IPv4 addresses, bracketed IPv6 addresses, or a bare port. It does not accept DNS names. Aspire may publish a service address such as `tigerbeetle:3000`, so applications running from a published manifest must resolve the host to a numeric address before constructing an official TigerBeetle client.

### Connect from .NET

Read the injected properties through normal .NET configuration:

```csharp
using System.Globalization;
using TigerBeetle;

var clusterId = builder.Configuration["TIGERBEETLE_CLUSTERID"]
    ?? throw new InvalidOperationException("TIGERBEETLE_CLUSTERID is required.");
var addressConfiguration = builder.Configuration.GetSection("TIGERBEETLE_ADDRESSES");
var replicaAddresses = (addressConfiguration.GetChildren().Any()
    ? addressConfiguration.Get<string[]>()
    : addressConfiguration.Value?.Split(
        ',',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    ?? throw new InvalidOperationException("TIGERBEETLE_ADDRESSES is required.");

builder.Services.AddSingleton(new Client(
    clusterID: UInt128.Parse(clusterId, CultureInfo.InvariantCulture),
    addresses: replicaAddresses));
```

The indexed form uses .NET's environment-variable hierarchy convention: `__0` maps to configuration key `:0`. The example falls back to splitting the scalar when an external configuration provides no indexed children. The published-container case also needs a DNS-to-IP step before `new Client(...)`. The repository sample includes that step in [`samples/TigerBeetle.Client/Program.cs`](samples/TigerBeetle.Client/Program.cs).

Create one `Client` and share it. The official client is thread-safe and combines concurrent work into batches.

### Connect from TypeScript

```typescript
import { createClient } from 'tigerbeetle-node';

const clusterId = process.env.TIGERBEETLE_CLUSTERID;
const addresses = process.env.TIGERBEETLE_ADDRESSES;

if (!clusterId || !addresses) {
  throw new Error('TIGERBEETLE_CLUSTERID and TIGERBEETLE_ADDRESSES are required.');
}

const client = createClient({
  cluster_id: BigInt(clusterId),
  replica_addresses: addresses.split(',').map(value => value.trim())
});
```

The TypeScript sample resolves published DNS names before creating the client. See [`samples/TigerBeetle.TypeScript/client/src/index.ts`](samples/TigerBeetle.TypeScript/client/src/index.ts).

## Builder reference

The C# names appear in PascalCase. Aspire exports equivalent camelCase methods to TypeScript.

| Method | Default or example | Behavior |
| --- | --- | --- |
| `AddTigerBeetle(name, port: null)` | Dynamic host port, container port `3000` | Adds one `ghcr.io/tigerbeetle/tigerbeetle:0.17.9` container, enables development mode, adds the TCP health check, and adds `seccomp=unconfined` to local container runtime arguments |
| `WithClusterId(clusterId)` | `"0"` | Sets the unsigned 128-bit cluster ID used by `format` and exposed through the `ClusterId` connection property. C# accepts `UInt128` or a decimal string. TypeScript accepts a decimal string. Cluster ID `0` is for tests and development |
| `WithReplica(replicaIndex, replicaCount)` | `0, 1` | Sets this replica's zero-based index and cluster size. The integration accepts 1 through 6 replicas |
| `WithAddresses(addresses)` | `"0.0.0.0:3000"` for server startup | Sets one ordered numeric address per replica for `tigerbeetle start` and also uses them as client addresses. A numeric IP may omit its port, and a bare port may omit the default IP |
| `WithClientAddresses(addresses)` | Aspire allocated endpoint | Overrides only the client address list. Use it when listen and client addresses differ |
| `WithCacheGrid(size)` | TigerBeetle default | Passes a value such as `256MiB` or `12GiB` to `--cache-grid` |
| `WithDevelopmentMode(enabled)` | `true` | Adds or removes `--development` from both `format` and `start` |
| `WithDataFile(path)` | `/data/{clusterId}_{replicaIndex}.tigerbeetle` | Sets an absolute data-file path inside the container |
| `WithDataVolume(name, isReadOnly)` | Generated volume name, writable | Mounts a named volume at `/data`. TigerBeetle rejects a read-only data volume |
| `WithDataBindMount(source, isReadOnly)` | No bind mount | Mounts a host directory at `/data`. TigerBeetle rejects a read-only data bind mount |
| `WithStatsD(ipAddress, port)` | Port `8125` | Enables TigerBeetle's experimental StatsD output with `--experimental --statsd=...`. The host must be a numeric IP |
| `WithFormatArgument(argument)` | None | Appends one shell-quoted raw argument to `tigerbeetle format` |
| `WithStartArgument(argument)` | None | Appends one shell-quoted raw argument to `tigerbeetle start` |
| `AddChangeDataCapture(name, rabbitMq, publishExchange)` | Required exchange | Adds a child `tigerbeetle amqp` container. The RabbitMQ overload waits for RabbitMQ; the generic connection-string overload adds only a reference relationship |
| `WithVirtualHost(virtualHost)` | Parsed URI path or `/` | Overrides the AMQP virtual host parsed from the connection string |
| `WithPublishRoutingKey(routingKey)` | None | Adds `--publish-routing-key` |
| `WithTimestampLast(timestamp)` | Last acknowledged timestamp | Sets the unsigned 64-bit resume timestamp. C# accepts `ulong` or a decimal string; TypeScript accepts a decimal string |
| `WithCdcArgs(args)` | None | Appends shell-quoted raw arguments after the integration-owned `tigerbeetle amqp` options |

TypeScript represents optional .NET parameters as options objects. For example:

```typescript
const tigerBeetle = await builder
  .addTigerBeetle('tigerbeetle', { port: 3000 })
  .withDataVolume({ name: 'tigerbeetle-data' })
  .withDevelopmentMode({ enabled: false })
  .withStatsD('10.0.0.20', { port: 9125 });
```

TigerBeetle keeps most advanced server flags behind `--experimental`. Prefer the typed methods for stable options. Use raw arguments only after checking the CLI for the pinned TigerBeetle release.

Standard Aspire container methods remain available. For example, `WithImageTag` can select a different TigerBeetle image. Update the official application client at the same time and follow TigerBeetle's upgrade compatibility rules.

## Startup and persistence

The container starts through `/bin/sh` and performs this sequence:

1. Check whether the configured data file exists.
2. Run `tigerbeetle format` when it is absent.
3. Execute `tini` and `tigerbeetle start` as PID 1.

By default the file is `/data/0_0.tigerbeetle`. The image has no built-in Docker volume, and `AddTigerBeetle` does not add one automatically. Container replacement loses all data unless the AppHost calls `WithDataVolume` or `WithDataBindMount`.

Use a named volume for normal development:

```csharp
var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume("tigerbeetle-data");
```

Use a bind mount when the data file needs a known host path:

```csharp
var dataPath = Path.GetFullPath("./.tigerbeetle-data");

var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataBindMount(dataPath);
```

TigerBeetle needs a writable data file. A read-only mount cannot run a replica.

Cluster ID, replica index, and replica count are recorded when the file is formatted. Do not change those values while reusing a custom fixed data-file path. The exported connection properties could then describe a different cluster than the file contains.

The automatic format-if-missing behavior is intended for first startup and single-replica development. It is not a production recovery procedure. If a production replica loses its data file, use `tigerbeetle recover`. Formatting a replacement replica can violate durability guarantees.

## Health checks

The integration registers a five-second TCP health check. It connects to the resource's Aspire-managed primary endpoint and reports healthy when the socket accepts a connection. Client-address overrides do not change which local replica Aspire probes. Unhealthy results include the attempted host and port.

This proves that the process is listening. It does not prove that a replicated cluster can commit a request. TigerBeetle has no HTTP health endpoint. For production monitoring, enable StatsD and monitor at least:

- `tb.replica_status`, where `0` means normal
- `tb.replica_sync_stage`, where `0` means no state sync is in progress

Official TigerBeetle clients retry requests indefinitely. Application health checks that issue TigerBeetle requests need an external timeout and must close their client on timeout.

## Run the repository samples

Run these commands from the repository root. Start the C# sample, which also runs RabbitMQ and the CDC job:

```bash
dotnet tool restore
dotnet tool run aspire -- run --apphost extensions/Shirubasoft.Aspire.Extensions.TigerBeetle/samples/TigerBeetle.AppHost/TigerBeetle.AppHost.csproj
```

The client URL appears in the Aspire dashboard. `GET /` returns the injected cluster ID and addresses. `GET /accounts/1` performs a TigerBeetle account lookup. `POST /transfers` creates a transfer and `GET /cdc/events` returns the most recent 100 events consumed from the durable sample queue.

Prepare and start the TypeScript sample:

```bash
sample=extensions/Shirubasoft.Aspire.Extensions.TigerBeetle/samples/TigerBeetle.TypeScript
npm ci --prefix "$sample"
npm ci --prefix "$sample/client"
dotnet tool run aspire -- restore --apphost "$sample" --non-interactive
npm run build --prefix "$sample"
dotnet tool run aspire -- run --apphost "$sample"
```

`samples/test-samples.sh` starts both samples in the background, checks a TigerBeetle lookup and a CDC event through the sample clients, and verifies the published Docker Compose output. CI runs it on Linux.

## Publish a container manifest

The C# sample includes `Aspire.Hosting.Docker`. Configure the TigerBeetle service's seccomp option and add a Docker Compose environment in the AppHost:

```csharp
var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume()
    .PublishAsDockerComposeService((_, service) =>
    {
        service.SecurityOpt.Add("seccomp=unconfined");
    });

builder.AddDockerComposeEnvironment("docker");
```

Publish it with the Aspire CLI:

```bash
dotnet tool run aspire -- publish \
  --apphost extensions/Shirubasoft.Aspire.Extensions.TigerBeetle/samples/TigerBeetle.AppHost/TigerBeetle.AppHost.csproj \
  --output-path artifacts/docker \
  --non-interactive
```

Aspire writes the Compose project under `artifacts/docker`. The manifest contains the TigerBeetle image, startup command, TCP binding, volume, and injected connection values.

The integration adds `--security-opt seccomp=unconfined` to local Aspire container runs. The `PublishAsDockerComposeService` callback above adds the equivalent setting to the published Compose service. Container runtime arguments are not portable across every Aspire publisher. Check the generated deployment and target platform. Docker 25 and later blocks the `io_uring` calls TigerBeetle needs unless the container has a suitable seccomp profile. The generated service should contain:

```yaml
security_opt:
  - seccomp=unconfined
```

Production mode also needs permission to lock memory. TigerBeetle recommends `CAP_IPC_LOCK` or an unlimited memlock setting. `WithDevelopmentMode(false)` only removes TigerBeetle's development flag. It does not configure host networking, storage, Linux capabilities, seccomp, or replica placement for the deployment target.

## Production deployment

TigerBeetle's official production recommendation is a six-replica cluster. Each replica should have its own machine and data disk, with independent fault domains. The same ordered numeric address list must reach every replica and every client.

That model does not map cleanly to a generic scaled container service:

- TigerBeetle `0.17.9` rejects DNS names in replica and client addresses.
- The official Docker multi-replica recipe uses host networking so every replica has a stable numeric address.
- Production storage should use local NVMe where possible. Each replica needs a separate disk.
- Production replicas require Direct IO and locked memory. Do not use `--development` in production.
- TigerBeetle has no authentication. Keep its network and data files inaccessible to untrusted users and services.
- A supervisor must restart crashed replicas.
- Lost replicas require `tigerbeetle recover`, not a new formatted file.

Many managed container services cannot provide stable literal IP addresses, host networking, `seccomp=unconfined`, `CAP_IPC_LOCK`, or local disks with the required placement. Publishing this resource through Aspire confirms that the deployment model can represent the container. It does not prove that the target meets TigerBeetle's production requirements.

For production, design and review the six-replica deployment against TigerBeetle's operating guide. Use `WithDevelopmentMode(false)`, an explicit nonzero random cluster ID, persistent storage, numeric replica addresses, and a pinned image only after the target meets those requirements.

## Versioning and upgrades

The hosting package version and TigerBeetle version are independent. This package release pins the server image to TigerBeetle `0.17.9`.

TigerBeetle clients cannot be newer than the cluster. Each TigerBeetle release states the oldest supported client and oldest upgradable replica versions. Upgrade replicas first, then clients. Do not change the container tag without reading the release notes and upgrade guide.

## Build and test

```bash
./build.sh --extension extensions/Shirubasoft.Aspire.Extensions.TigerBeetle
```

## Authoritative references

- [TigerBeetle 0.17.9 release](https://github.com/tigerbeetle/tigerbeetle/releases/tag/0.17.9)
- [TigerBeetle quick start](https://docs.tigerbeetle.com/start/)
- [Change data capture](https://docs.tigerbeetle.com/operating/cdc/)
- [Official Docker image and container guidance](https://docs.tigerbeetle.com/operating/deploying/docker/)
- [Deployment procedure](https://docs.tigerbeetle.com/operating/deploying/)
- [Production cluster recommendations](https://docs.tigerbeetle.com/operating/cluster/)
- [Hardware and storage requirements](https://docs.tigerbeetle.com/operating/hardware/)
- [Monitoring](https://docs.tigerbeetle.com/operating/monitoring/)
- [Replica recovery](https://docs.tigerbeetle.com/operating/recovering/)
- [Upgrade compatibility](https://docs.tigerbeetle.com/operating/upgrading/)
- [TigerBeetle in an application architecture](https://docs.tigerbeetle.com/coding/system-architecture/)
- [Official .NET client](https://docs.tigerbeetle.com/coding/clients/dotnet/)
- [Official Node.js client](https://docs.tigerbeetle.com/coding/clients/node/)

## License

[MIT](https://github.com/Shirubasoft/aspire-extensions/blob/main/LICENSE)
