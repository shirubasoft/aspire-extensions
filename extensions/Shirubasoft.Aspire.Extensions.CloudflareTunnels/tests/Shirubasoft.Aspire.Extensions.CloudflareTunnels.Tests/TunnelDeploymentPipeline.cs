using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

// Runs a publish-mode pipeline for an AppHost with a whoami target routed through a named
// tunnel. The tunnel and the target deploy to the compute environment that AddEnvironment
// adds, Docker Compose by default. The container runtime and deployment state are in-memory,
// so the run never starts containers or writes deployment state.
internal sealed class TunnelDeploymentPipeline
{
    public required TestCloudflareApiClient Api { get; init; }

    public string TunnelName { get; init; } = "public";

    public string Hostname { get; init; } = "app.example.com";

    public bool ExternallyManagedRoutes { get; init; }

    public IReadOnlyList<string> StepNames { get; private set; } = [];

    public int? TargetPort { get; init; } = 8080;

    public Action<IDistributedApplicationBuilder> AddEnvironment { get; init; } =
        builder => builder.AddDockerComposeEnvironment("env");

    public Func<Task> ComposeUp { get; init; } = () => Task.CompletedTask;

    public Action<IResourceBuilder<ContainerResource>> ConfigureTarget { get; init; } = _ => { };

    public TestCloudflareApiClientFactory ClientFactory { get; private set; } = null!;

    // The route step's dependencies after the tunnel's pipeline configuration ran.
    public IReadOnlyList<string> RouteStepDependencies { get; private set; } = [];

    // The exception that stopped the pipeline, or null when every step succeeded.
    public Exception? Failure { get; private set; }

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
            if (!ExternallyManagedRoutes)
            {
                builder.Configuration[$"Parameters:{TunnelName}-account-id"] = "account-id";
                builder.Configuration[$"Parameters:{TunnelName}-api-token"] = "api-token";
            }
            builder.Configuration[$"Parameters:{TunnelName}-tunnel-token"] = "tunnel-token";
            AddEnvironment(builder);
            var web = builder
                .AddContainer("web", "docker.io/traefik/whoami", "v1.10")
                .WithHttpEndpoint(targetPort: TargetPort, name: "http");
            var tunnel = ExternallyManagedRoutes
                ? builder.AddCloudflareTunnelConnector(TunnelName)
                : builder.AddCloudflareTunnel(TunnelName);
            web.WithCloudflareTunnel(tunnel, Hostname);
            ConfigureTarget(web);

            // Configuration callbacks run in resource order, so this probe sees the
            // dependencies that the tunnel's callback added.
            builder
                .AddResource(new PipelineProbeResource("route-step-probe"))
                .WithPipelineConfiguration(context =>
                {
                    StepNames = [.. context.Steps.Select(pipelineStep => pipelineStep.Name)];
                    RouteStepDependencies =
                    [
                        .. context.Steps.Where(routeStep => routeStep.Name == RouteStepName)
                            .SelectMany(routeStep => routeStep.DependsOnSteps),
                    ];
                });

            ClientFactory = new TestCloudflareApiClientFactory(Api);
            builder.Services.AddSingleton<ICloudflareApiClientFactory>(ClientFactory);
            builder.Services.AddSingleton(CreateContainerRuntimeResolver());
            builder.Services.AddSingleton(CreateDeploymentStateManager());

            using var app = builder.Build();
            // A failed pipeline stops the host without failing RunAsync. The pipeline
            // executor's background task keeps the failure.
            var backgroundServices = app.Services
                .GetServices<IHostedService>()
                .OfType<BackgroundService>()
                .ToArray();
            await app.RunAsync(TestContext.Current.CancellationToken);
            Failure = backgroundServices
                .Select(service => service.ExecuteTask?.Exception?.InnerException)
                .OfType<Exception>()
                .SingleOrDefault();
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
#pragma warning restore ASPIREPIPELINES001
#pragma warning restore ASPIRECONTAINERRUNTIME001
