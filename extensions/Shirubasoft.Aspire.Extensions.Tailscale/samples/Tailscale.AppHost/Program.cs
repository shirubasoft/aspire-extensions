using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

var tailnet = builder.AddTailnet("tailnet");

builder.AddContainer("whoami", "docker.io/traefik/whoami", "v1.10")
    .WithArgs("--port", "8080")
    .WithHttpEndpoint(targetPort: 8080)
    .WithTailscale(tailnet, hostname: "aspire-whoami");

await builder.Build().RunAsync();
