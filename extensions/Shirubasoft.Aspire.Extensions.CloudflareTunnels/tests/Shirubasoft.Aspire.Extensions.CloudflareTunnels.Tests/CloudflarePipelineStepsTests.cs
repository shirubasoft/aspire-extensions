using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
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
        string[] dependencies = [];
        var pipeline = new ComposeDeploymentPipeline
        {
            Api = CreateApi(),
            TunnelName = "public",
            Hostname = "app.example.com",
            Configure = builder => builder
                .AddResource(new PipelineProbeResource("probe"))
                .WithPipelineConfiguration(context => dependencies =
                [
                    .. context.Steps
                        .Single(step => step.Name == "configure-public-cloudflare-routes")
                        .DependsOnSteps,
                ]),
        };

        await pipeline.RunAsync("publish");

        Assert.Contains("docker-compose-up-env", dependencies);
    }

    [Fact]
    public async Task RouteStepRunsAfterComposeUpSucceeds()
    {
        var api = CreateApi();
        var dnsUpsertsDuringComposeUp = new List<int>();
        var pipeline = new ComposeDeploymentPipeline
        {
            Api = api,
            TunnelName = "public",
            Hostname = "app.example.com",
            ComposeUp = () =>
            {
                dnsUpsertsDuringComposeUp.Add(api.DnsUpserts.Count);
                return Task.CompletedTask;
            },
        };

        await pipeline.RunAsync(pipeline.RouteStepName);

        Assert.Equal([0], dnsUpsertsDuringComposeUp);
        Assert.Single(api.DnsUpserts);
        Assert.NotNull(api.UpdatedConfiguration);
    }

    [Fact]
    public async Task FailedComposeUpLeavesTunnelConfigurationAndDnsUntouched()
    {
        var api = CreateApi();
        var composeUpCalls = 0;
        var pipeline = new ComposeDeploymentPipeline
        {
            Api = api,
            TunnelName = "public",
            Hostname = "app.example.com",
            ComposeUp = () =>
            {
                composeUpCalls++;
                return Task.FromException(new InvalidOperationException("compose up failed"));
            },
        };

        await pipeline.RunAsync("deploy");

        Assert.Equal(1, composeUpCalls);
        Assert.Equal(0, pipeline.ClientFactory.CallCount);
        Assert.Empty(api.DnsUpserts);
        Assert.Null(api.UpdatedConfiguration);
    }

    [Fact]
    public async Task RouteStepUpdatesATunnelWithoutRoutes()
    {
        var api = CreateApi();
        api.Configuration = new TunnelConfiguration
        {
            Ingress =
            [
                new IngressRule { Hostname = "app.example.com", Service = "http://web:8080" },
                new IngressRule { Service = "http_status:404" },
            ],
        };
        var pipeline = new ComposeDeploymentPipeline
        {
            Api = api,
            TunnelName = "public",
        };

        await pipeline.RunAsync(pipeline.RouteStepName);

        var configuration = Assert.IsType<TunnelConfiguration>(api.UpdatedConfiguration);
        var rule = Assert.Single(configuration.Ingress);
        Assert.Null(rule.Hostname);
        Assert.Equal("http_status:404", rule.Service);
    }

    [Fact]
    public void DeploymentStepsIncludeComputeDeploymentsOfDeployedResources()
    {
        var environment = new TestComputeEnvironmentResource("aca");
        var deploymentTarget = new PipelineProbeResource("web-app");
        var web = new ContainerResource("web");
        web.Annotations.Add(new DeploymentTargetAnnotation(deploymentTarget)
        {
            ComputeEnvironment = environment,
        });
        var deployWeb = CreateStep("deploy-web", deploymentTarget, WellKnownPipelineTags.DeployCompute);
        var context = new PipelineConfigurationContext
        {
            Services = new ServiceCollection().BuildServiceProvider(),
            Steps =
            [
                deployWeb,
                CreateStep("provision-aca", environment, WellKnownPipelineTags.ProvisionInfrastructure),
                CreateStep("build-web", web, WellKnownPipelineTags.BuildCompute),
            ],
            Model = new DistributedApplicationModel([environment, deploymentTarget, web]),
        };

        var steps = CloudflarePipelineSteps.GetDeploymentSteps(
            context,
            [web, new PipelineProbeResource("not-deployed")]);

        Assert.Equal([deployWeb], steps);
    }

    [Fact]
    public async Task ExecuteConfiguresATunnelWithoutRoutes()
    {
        var configured = false;
        string? summary = null;

        await CloudflarePipelineSteps.ExecuteAsync(
            Array.Empty<PublishedRouteResource>(),
            _ =>
            {
                configured = true;
                return Task.CompletedTask;
            },
            value => summary = value,
            TestContext.Current.CancellationToken);

        Assert.True(configured);
        Assert.Equal("No routes", summary);
    }

    [Fact]
    public async Task ExecuteConfiguresAndSummarizesRoutes()
    {
        var builder = DistributedApplication.CreateBuilder();
        var target = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);
        var route = new PublishedRouteResource(
            "route",
            "app.example.com",
            target.GetEndpoint("http"),
            target.Resource,
            new CloudflareTunnelResource("public"));
        var configured = false;
        string? summary = null;

        await CloudflarePipelineSteps.ExecuteAsync(
            [route],
            _ =>
            {
                configured = true;
                return Task.CompletedTask;
            },
            value => summary = value,
            TestContext.Current.CancellationToken);

        Assert.True(configured);
        Assert.Equal("app.example.com", summary);
    }

    [Fact]
    public async Task ReportWarningCompletesAWarningTask()
    {
        var step = new RecordingReportingStep();

        await CloudflarePipelineSteps.ReportWarningAsync(
            step,
            new RouteWarning(
                "Resolve the service URL for app.example.com",
                "Set the endpoint's target port."),
            TestContext.Current.CancellationToken);

        var task = Assert.Single(step.Tasks);
        Assert.Equal("Resolve the service URL for app.example.com", task.StatusText);
        Assert.Equal("Set the endpoint's target port.", task.CompletionMessage);
        Assert.Equal(CompletionState.CompletedWithWarning, task.CompletionState);
        Assert.True(task.IsDisposed);
    }

    private static TestCloudflareApiClient CreateApi() =>
        new()
        {
            ExistingTunnel = new("deployed-tunnel-id", "public", "healthy", null, null),
        };

    private static PipelineStep CreateStep(string name, IResource resource, string tag) =>
        new()
        {
            Name = name,
            Action = _ => Task.CompletedTask,
            Tags = [tag],
            Resource = resource,
        };

    private sealed class TestComputeEnvironmentResource(string name)
        : Resource(name), IComputeEnvironmentResource;
}
