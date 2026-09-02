using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflarePipelineStepsTests
{
    [Fact]
    public void StepUsesAStableResourceScopedName()
    {
        var tunnel = new CloudflareTunnelResource("public");

        var step = CloudflarePipelineSteps.CreateConfigureRoutesStep(tunnel);

        Assert.Equal("configure-public-cloudflare-routes", step.Name);
        Assert.Equal([CloudflarePipelineSteps.Tag], step.Tags);
    }

    [Fact]
    public async Task ExecuteSkipsConfigurationWhenNoRoutesExist()
    {
        var configured = false;
        var summary = false;

        await CloudflarePipelineSteps.ExecuteAsync(
            new CloudflareTunnelResource("public"),
            Array.Empty<PublishedRouteResource>(),
            _ =>
            {
                configured = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            _ => summary = true,
            TestContext.Current.CancellationToken);

        Assert.False(configured);
        Assert.False(summary);
    }

    [Fact]
    public async Task ExecuteConfiguresAndSummarizesRoutes()
    {
        var builder = DistributedApplication.CreateBuilder();
        var target = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);
        var tunnel = new CloudflareTunnelResource("public");
        var route = new PublishedRouteResource(
            "route",
            "app.example.com",
            target.GetEndpoint("http"),
            target.Resource,
            tunnel);
        var configured = false;
        string? summary = null;

        await CloudflarePipelineSteps.ExecuteAsync(
            tunnel,
            [route],
            _ =>
            {
                configured = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            value => summary = value,
            TestContext.Current.CancellationToken);

        Assert.True(configured);
        Assert.Equal("app.example.com", summary);
    }
}
