using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddResourceGroup("backend");
var database = backend
    .AddContainer("database", "postgres", "18-alpine")
    .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust");

backend.AddContainer("api", "alpine", "3.22")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "sleep infinity");

backend.AddContainer("migrations", "alpine", "3.22")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "sleep infinity")
    .WithParentRelationship(database);

await builder.Build().RunAsync();
