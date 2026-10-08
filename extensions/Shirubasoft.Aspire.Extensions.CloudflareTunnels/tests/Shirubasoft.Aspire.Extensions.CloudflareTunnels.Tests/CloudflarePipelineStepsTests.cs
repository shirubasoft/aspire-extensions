using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREPIPELINES001

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
    public async Task RouteStepDependsOnTheComposeDeployment()
    {
        var pipeline = new TunnelDeploymentPipeline { Api = CreateApi() };

        await pipeline.RunAsync("publish");

        Assert.Contains("docker-compose-up-env", pipeline.RouteStepDependencies);
    }

    // Running the route step by name, as `aspire do` does, also runs compose up.
    [Theory]
    [InlineData("deploy")]
    [InlineData("configure-public-cloudflare-routes")]
    public async Task RouteStepRunsAfterComposeUpSucceeds(string step)
    {
        var api = CreateApi();
        var cloudflareWritesDuringComposeUp = new List<(int DnsUpserts, bool IngressUpdated)>();
        var pipeline = new TunnelDeploymentPipeline
        {
            Api = api,
            ComposeUp = () =>
            {
                cloudflareWritesDuringComposeUp.Add(
                    (api.DnsUpserts.Count, api.UpdatedConfiguration is not null));
                return Task.CompletedTask;
            },
        };

        await pipeline.RunAsync(step);

        Assert.Null(pipeline.Failure);
        Assert.Equal([(0, false)], cloudflareWritesDuringComposeUp);
        Assert.Single(api.DnsUpserts);
        Assert.NotNull(api.UpdatedConfiguration);
    }

    [Fact]
    public async Task FailedComposeUpLeavesTunnelConfigurationAndDnsUntouched()
    {
        var api = CreateApi();
        var composeUpCalls = 0;
        var pipeline = new TunnelDeploymentPipeline
        {
            Api = api,
            ComposeUp = () =>
            {
                composeUpCalls++;
                return Task.FromException(new InvalidOperationException("compose up failed"));
            },
        };

        await pipeline.RunAsync("deploy");

        Assert.Equal(1, composeUpCalls);
        Assert.Contains("compose up failed", pipeline.Failure?.Message);
        Assert.Equal(0, pipeline.ClientFactory.CallCount);
        Assert.Empty(api.DnsUpserts);
        Assert.Null(api.UpdatedConfiguration);
    }

    // A compute environment declares its deployment step with the deploy-compute tag,
    // on the environment or, like Azure Container Apps, on each deployment target.
    [Theory]
    [InlineData(DeploymentStepOwner.Environment, "deploy-custom")]
    [InlineData(DeploymentStepOwner.DeploymentTarget, "deploy-web-custom")]
    public async Task RouteStepRunsAfterADeclaredDeploymentStepSucceeds(
        DeploymentStepOwner owner,
        string deploymentStep)
    {
        var api = CreateApi();
        var dnsUpsertsDuringDeployment = new List<int>();
        var pipeline = new TunnelDeploymentPipeline
        {
            Api = api,
            AddEnvironment = builder => builder.AddTestComputeEnvironment(
                "custom",
                WellKnownPipelineTags.DeployCompute,
                owner,
                () =>
                {
                    dnsUpsertsDuringDeployment.Add(api.DnsUpserts.Count);
                    return Task.CompletedTask;
                }),
        };

        await pipeline.RunAsync("deploy");

        Assert.Null(pipeline.Failure);
        Assert.Contains(deploymentStep, pipeline.RouteStepDependencies);
        Assert.All(dnsUpsertsDuringDeployment, count => Assert.Equal(0, count));
        Assert.Single(api.DnsUpserts);
        Assert.NotNull(api.UpdatedConfiguration);
    }

    [Theory]
    [InlineData(DeploymentStepOwner.Environment)]
    [InlineData(DeploymentStepOwner.DeploymentTarget)]
    public async Task FailedDeclaredDeploymentStepLeavesTunnelConfigurationAndDnsUntouched(
        DeploymentStepOwner owner)
    {
        var api = CreateApi();
        var pipeline = new TunnelDeploymentPipeline
        {
            Api = api,
            AddEnvironment = builder => builder.AddTestComputeEnvironment(
                "custom",
                WellKnownPipelineTags.DeployCompute,
                owner,
                () => Task.FromException(new InvalidOperationException("deployment failed"))),
        };

        await pipeline.RunAsync("deploy");

        Assert.Contains("deployment failed", pipeline.Failure?.Message);
        Assert.Equal(0, pipeline.ClientFactory.CallCount);
        Assert.Empty(api.DnsUpserts);
        Assert.Null(api.UpdatedConfiguration);
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
        Assert.Equal(Pipelines.CompletionState.CompletedWithWarning, task.CompletionState);
        Assert.True(task.IsDisposed);
    }

    private static TestCloudflareApiClient CreateApi() =>
        new()
        {
            ExistingTunnel = new("deployed-tunnel-id", "public", "healthy", null, null),
        };
}
