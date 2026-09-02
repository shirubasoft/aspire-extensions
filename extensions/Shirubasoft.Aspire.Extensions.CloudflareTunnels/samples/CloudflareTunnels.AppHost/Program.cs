using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddContainer("web", "nginx", "1.29-alpine")
    .WithHttpEndpoint(targetPort: 80);

builder.AddCloudflareQuickTunnel("web-tunnel")
    .WithReference(web);

await builder.Build().RunAsync();
