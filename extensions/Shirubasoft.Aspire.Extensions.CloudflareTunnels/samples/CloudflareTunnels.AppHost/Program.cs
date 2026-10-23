using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddContainer("web", "docker.io/library/nginx", "1.31-alpine")
    .WithHttpEndpoint(targetPort: 80);

if (builder.ExecutionContext.IsPublishMode &&
    builder.Configuration.GetValue<bool>("Cloudflare:ExternallyManagedRoutes"))
{
    var tunnel = builder.AddCloudflareTunnelConnector("web-tunnel");
    web.WithCloudflareTunnel(tunnel, "web.example.com");
}
else if (builder.Configuration["Cloudflare:Hostname"] is { Length: > 0 } hostname)
{
    builder.AddDockerComposeEnvironment("compose");

    var tunnel = builder.AddCloudflareTunnel("web-tunnel");
    web.WithCloudflareTunnel(tunnel, hostname);
}
else
{
    var quickTunnel = builder.AddCloudflareQuickTunnel("web-tunnel")
        .WithReference(web);

    if (builder.Configuration.GetSection("Cloudflare:AllowedMail").Get<string[]>() is { Length: > 0 } allowedMail)
    {
        quickTunnel.WithAllowedMail(allowedMail);
    }
}

await builder.Build().RunAsync();
