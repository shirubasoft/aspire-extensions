# Shirubasoft.Aspire.Extensions.Kafka

Add a Confluent Schema Registry container and idempotently register Kafka topics in an Aspire AppHost.

## Install

Add the package to an Aspire AppHost that already uses `Aspire.Hosting.Kafka`:

```bash
dotnet add package Shirubasoft.Aspire.Extensions.Kafka
```

The package targets .NET 10 and Aspire 13.5.3 or later within the Aspire 13.5 line.

## Add Schema Registry and topics

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var kafka = builder.AddKafka("kafka");
var schemaRegistry = kafka.AddSchemaRegistry("schema-registry");
var orders = kafka.AddTopic("orders", partitionCount: 3);

builder.AddProject<Projects.Worker>("worker")
    .WithReference(kafka)
    .WithReference(schemaRegistry)
    .WaitFor(schemaRegistry)
    .WaitFor(orders);

await builder.Build().RunAsync();
```

`AddSchemaRegistry` runs `confluentinc/cp-schema-registry:8.1.0`, waits for Kafka, and reports healthy after `GET /subjects` succeeds. Pass `port` to bind a fixed host port. Standard container builder methods can replace the image or add persistent configuration.

The Schema Registry `WithReference` overload injects all of these values into the destination:

| Variable | Value |
| --- | --- |
| `ConnectionStrings__schema-registry` | The full HTTP address. |
| `SCHEMA_REGISTRY_HTTP` | The context-aware HTTP endpoint. |
| `services__schema-registry__http__0` | The .NET service-discovery endpoint. |
| `SCHEMA_REGISTRY_ADDRESS` | The full HTTP address as a connection property. |
| `SCHEMA_REGISTRY_HOST` | The resolved Schema Registry host. |
| `SCHEMA_REGISTRY_PORT` | The resolved Schema Registry port. |

Use a custom connection name when the consumer expects another configuration key:

```csharp
worker.WithReference(schemaRegistry, connectionName: "schemas");
```

This writes `ConnectionStrings__schemas` and `SCHEMAS_ADDRESS`. Endpoint variables keep the Schema Registry resource name.

`AddTopic` waits for the broker, calls Kafka's admin API, and treats an existing topic as success. A dependent resource can call `WaitFor(topic)` and starts only after registration succeeds. `partitionCount` and `replicationFactor` apply only when Kafka creates the topic.

## Run the sample

From this extension folder, run:

```bash
aspire run --project samples/Kafka.AppHost/Kafka.AppHost.csproj
```

The dashboard shows Kafka, Schema Registry, the registered `orders` topic, and a consumer container with the injected references.

## Package contents

The NuGet package includes the assembly, XML API documentation, this README, repository metadata, Source Link data, and a matching `.snupkg` symbol package.
