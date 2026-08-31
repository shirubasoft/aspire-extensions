using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var kafka = builder.AddKafka("kafka");
var schemaRegistry = kafka.AddSchemaRegistry("schema-registry");
var orders = kafka.AddTopic("orders", partitionCount: 3);

builder.AddContainer("consumer", "alpine", "3.22")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "sleep infinity")
    .WithReference(kafka)
    .WithReference(schemaRegistry)
    .WaitFor(schemaRegistry)
    .WaitFor(orders);

await builder.Build().RunAsync();
