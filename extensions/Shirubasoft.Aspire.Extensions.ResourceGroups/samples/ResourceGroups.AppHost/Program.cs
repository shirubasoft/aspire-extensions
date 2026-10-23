using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddResourceGroup("backend");
var database = backend
    .AddContainer("database", "docker.io/library/postgres", "18-alpine")
    .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust");

backend.AddContainer("api", "docker.io/library/alpine", "3.24")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "sleep infinity");

backend.AddContainer("migrations", "docker.io/library/alpine", "3.24")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "sleep infinity")
    .WithParentRelationship(database);

await builder.Build().RunAsync();
