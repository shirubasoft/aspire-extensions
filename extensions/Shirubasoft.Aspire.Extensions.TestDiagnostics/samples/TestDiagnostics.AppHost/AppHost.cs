using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddProject<Projects.TestDiagnostics_Api>("api")
    .WithHttpEndpoint()
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "100")
    .WithEnvironment("OTEL_BLRP_SCHEDULE_DELAY", "100");
await builder.Build().RunAsync();
