using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddContainer("web", "nginx", "1.29-alpine")
    .WithHttpEndpoint(targetPort: 80);

if (builder.ExecutionContext.IsPublishMode &&
    builder.Configuration.GetValue<bool>("Cloudflare:ExternallyManagedRoutes"))
{
    var tunnel = builder.AddCloudflareTunnelConnector("web-tunnel");
    web.WithCloudflareTunnel(tunnel, "web.example.com");
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
