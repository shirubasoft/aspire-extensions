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

    [Fact]
    public async Task ReportWarningCompletesAWarningTaskForTheRoute()
    {
        var builder = DistributedApplication.CreateBuilder();
        var target = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint();
        var route = new PublishedRouteResource(
            "route",
            "app.example.com",
            target.GetEndpoint("http"),
            target.Resource,
            new CloudflareTunnelResource("public"));
        var step = new RecordingReportingStep();

        await CloudflarePipelineSteps.ReportWarningAsync(
            step,
            route,
            "Set the endpoint's target port.",
            TestContext.Current.CancellationToken);

        var task = Assert.Single(step.Tasks);
        Assert.Equal("Resolve the service URL for app.example.com", task.StatusText);
        Assert.Equal("Set the endpoint's target port.", task.CompletionMessage);
#pragma warning disable ASPIREPIPELINES001
        Assert.Equal(Pipelines.CompletionState.CompletedWithWarning, task.CompletionState);
#pragma warning restore ASPIREPIPELINES001
        Assert.True(task.IsDisposed);
    }
}
