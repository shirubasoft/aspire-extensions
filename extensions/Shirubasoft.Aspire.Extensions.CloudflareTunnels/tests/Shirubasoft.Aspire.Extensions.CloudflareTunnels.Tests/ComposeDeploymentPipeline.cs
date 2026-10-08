using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES002

// Runs a publish-mode pipeline for a Docker Compose AppHost with a whoami target routed
// through a named tunnel. The container runtime and deployment state are in-memory, so
// the run never starts containers or writes deployment state.
internal sealed class ComposeDeploymentPipeline
{
    public required TestCloudflareApiClient Api { get; init; }

    public required string TunnelName { get; init; }

    // Null declares the tunnel without routes.
    public string? Hostname { get; init; }

    public int? TargetPort { get; init; } = 8080;

    public Func<Task> ComposeUp { get; init; } = () => Task.CompletedTask;

    public Action<IDistributedApplicationBuilder> Configure { get; init; } = _ => { };

    public TestCloudflareApiClientFactory ClientFactory { get; private set; } = null!;

    public string RouteStepName => $"configure-{TunnelName}-cloudflare-routes";

    public async Task RunAsync(string step)
    {
        var outputPath = Directory.CreateTempSubdirectory();
        try
        {
            var builder = DistributedApplication.CreateBuilder(
            [
                "--operation", "publish",
                "--step", step,
                "--output-path", outputPath.FullName,
            ]);
            builder.Configuration[$"Parameters:{TunnelName}-account-id"] = "account-id";
            builder.Configuration[$"Parameters:{TunnelName}-api-token"] = "api-token";
            builder.Configuration[$"Parameters:{TunnelName}-tunnel-token"] = "tunnel-token";
            builder.AddDockerComposeEnvironment("env");
            var web = builder
                .AddContainer("web", "docker.io/traefik/whoami", "v1.10")
                .WithHttpEndpoint(targetPort: TargetPort, name: "http");
            var tunnel = builder.AddCloudflareTunnel(TunnelName);
            if (Hostname is not null)
            {
                web.WithCloudflareTunnel(tunnel, Hostname);
            }

            Configure(builder);

            ClientFactory = new TestCloudflareApiClientFactory(Api);
            builder.Services.AddSingleton<ICloudflareApiClientFactory>(ClientFactory);
            builder.Services.AddSingleton(CreateContainerRuntimeResolver());
            builder.Services.AddSingleton(CreateDeploymentStateManager());

            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            outputPath.Delete(recursive: true);
        }
    }

    private IContainerRuntimeResolver CreateContainerRuntimeResolver()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.Name.Returns("test-runtime");
        runtime
            .ComposeUpAsync(Arg.Any<ComposeOperationContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => ComposeUp());

        var resolver = Substitute.For<IContainerRuntimeResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(runtime);
        return resolver;
    }

    private static IDeploymentStateManager CreateDeploymentStateManager()
    {
        var stateManager = Substitute.For<IDeploymentStateManager>();
        stateManager
            .AcquireSectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new DeploymentStateSection(call.ArgAt<string>(0), null, 0));
        return stateManager;
    }
}

internal sealed class PipelineProbeResource(string name) : Resource(name);

#pragma warning restore ASPIREPIPELINES002
#pragma warning restore ASPIRECONTAINERRUNTIME001
