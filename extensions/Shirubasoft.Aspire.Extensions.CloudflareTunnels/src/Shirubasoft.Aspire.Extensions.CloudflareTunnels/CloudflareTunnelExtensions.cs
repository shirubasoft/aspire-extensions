using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Adds Cloudflare Tunnel resources to an Aspire AppHost.
/// </summary>
public static class CloudflareTunnelResourceBuilderExtensions
{
    /// <summary>
    /// Adds a named Cloudflare Tunnel backed by a cloudflared container.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The Aspire resource name and Cloudflare tunnel name.</param>
    /// <param name="metricsPort">The optional host port for cloudflared metrics.</param>
    /// <returns>The Cloudflare Tunnel resource builder.</returns>
    public static IResourceBuilder<CloudflareTunnelResource> AddCloudflareTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? metricsPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        RegisterServices(builder);

        var tunnelResource = new CloudflareTunnelResource(name);
        var tunnelBuilder = ConfigureCloudflaredContainer(
                builder.AddResource(tunnelResource),
                metricsPort)
            .WithArgs(
            [
                "tunnel",
                "--no-autoupdate",
                "--metrics",
                $"0.0.0.0:{CloudflareTunnelContainerDefaults.MetricsPort}",
                "run",
            ]);

        var accountId = builder
            .AddParameter($"{name}-account-id", secret: false)
            .WithDescription("The Cloudflare account ID.");
        var apiToken = builder
            .AddParameter($"{name}-api-token", secret: true)
            .WithDescription("The Cloudflare API token with tunnel and DNS permissions.");

        tunnelBuilder.WithAnnotation(
            new CloudflareTunnelCredentialsAnnotation(
                apiToken.Resource,
                accountId.Resource));

#pragma warning disable ASPIREPIPELINES001
        tunnelBuilder.WithPipelineStepFactory(context =>
            CloudflarePipelineSteps.CreateConfigureRoutesStep(
                (CloudflareTunnelResource)context.Resource));
#pragma warning restore ASPIREPIPELINES001

        if (builder.ExecutionContext.IsRunMode)
        {
            ConfigureInteractiveInput(accountId, apiToken);
            var installer = AddTunnelInstaller(builder, tunnelBuilder);

            tunnelBuilder
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["TUNNEL_TOKEN"] =
                        tunnelResource.TunnelToken
                        ?? throw new InvalidOperationException(
                            $"Cloudflare tunnel '{name}' has no tunnel token.");
                })
                .WaitForCompletion(installer)
                .OnResourceReady(ConfigureRunModeRoutesAsync);
        }
        else
        {
            var tunnelToken = builder
                .AddParameter($"{name}-tunnel-token", secret: true)
                .WithDescription("The token for a pre-provisioned Cloudflare tunnel.");
            tunnelBuilder.WithEnvironment("TUNNEL_TOKEN", tunnelToken.Resource);
        }

        return tunnelBuilder;
    }

    /// <summary>
    /// Adds an account-free Cloudflare Quick Tunnel for local development.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The tunnel resource name.</param>
    /// <param name="metricsPort">The optional host port for cloudflared metrics.</param>
    /// <returns>The Quick Tunnel resource builder.</returns>
    public static IResourceBuilder<CloudflareQuickTunnelResource> AddCloudflareQuickTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? metricsPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = new CloudflareQuickTunnelResource(name);
        var tunnel = ConfigureCloudflaredContainer(
                builder.AddResource(resource),
                metricsPort)
            .WithArgs(context =>
                AddQuickTunnelArguments(context, resource))
            .ExcludeFromManifest()
            .OnResourceReady(async (_, @event, cancellationToken) =>
            {
                var publisher = @event.Services
                    .GetRequiredService<CloudflareQuickTunnelUrlPublisher>();
                await publisher.PublishAsync(resource, cancellationToken).ConfigureAwait(false);
            });

        builder.Services.TryAddSingleton<CloudflareQuickTunnelUrlPublisher>();
        return tunnel;
    }

    /// <summary>
    /// Selects the endpoint exposed by a Cloudflare Quick Tunnel.
    /// </summary>
    /// <typeparam name="T">The target resource type.</typeparam>
    /// <param name="tunnel">The Quick Tunnel resource builder.</param>
    /// <param name="target">The resource whose endpoint receives traffic.</param>
    /// <param name="endpointName">The endpoint name. The default is <c>http</c>.</param>
    /// <returns>The Quick Tunnel resource builder.</returns>
    public static IResourceBuilder<CloudflareQuickTunnelResource> WithReference<T>(
        this IResourceBuilder<CloudflareQuickTunnelResource> tunnel,
        IResourceBuilder<T> target,
        string endpointName = "http")
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(tunnel);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        if (tunnel.Resource.TargetEndpoint is not null)
        {
            throw new InvalidOperationException(
                $"Cloudflare Quick Tunnel '{tunnel.Resource.Name}' already references an endpoint.");
        }

        var endpoint = target.GetEndpoint(
            endpointName,
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
        tunnel.Resource.TargetEndpoint = endpoint;

        return tunnel.WithReference(endpoint);
    }

    /// <summary>
    /// Publishes a resource endpoint through a named Cloudflare Tunnel.
    /// </summary>
    /// <typeparam name="T">The target resource type.</typeparam>
    /// <param name="builder">The target resource builder.</param>
    /// <param name="tunnel">The Cloudflare Tunnel resource builder.</param>
    /// <param name="hostname">The public hostname.</param>
    /// <param name="endpointName">The endpoint name. The default is <c>http</c>.</param>
    /// <returns>The target resource builder.</returns>
    public static IResourceBuilder<T> WithCloudflareTunnel<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        string hostname,
        string endpointName = "http")
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tunnel);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        var endpoint = builder.GetEndpoint(
            endpointName,
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

        tunnel.WithReference(endpoint);
        tunnel.WithUrl($"https://{hostname}", hostname);
        AddPublishedRoute(
            builder.ApplicationBuilder,
            tunnel,
            builder.Resource,
            hostname,
            endpoint);

        return builder;
    }

    private static void RegisterServices(IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<ICloudflareApiClientFactory, CloudflareApiClientFactory>();
        builder.Services.TryAddSingleton<CloudflareTunnelProvisioner>();
        builder.Services.TryAddSingleton<CloudflareRouteProvisioner>();
    }

    private static IResourceBuilder<CloudflareTunnelInstallerResource> AddTunnelInstaller(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel)
    {
        var resource = new CloudflareTunnelInstallerResource(
            $"{tunnel.Resource.Name}-installer",
            tunnel.Resource);

        return builder
            .AddResource(resource)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Cloudflare tunnel installer",
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "Cloudflare API"),
                ],
            })
            .OnInitializeResource(async (installer, @event, cancellationToken) =>
            {
                var provisioner = @event.Services
                    .GetRequiredService<CloudflareTunnelProvisioner>();

                await CloudflareResourceLifecycle.RunAsync(
                    token => provisioner.ProvisionAsync(installer, @event.Logger, token),
                    state => PublishStateAsync(@event.Notifications, installer, state),
                    KnownResourceStates.Finished,
                    @event.Logger,
                    $"Failed to provision Cloudflare tunnel '{installer.Tunnel.Name}'.",
                    cancellationToken).ConfigureAwait(false);
            });
    }

    private static IResourceBuilder<T> ConfigureCloudflaredContainer<T>(
        IResourceBuilder<T> builder,
        int? metricsPort)
        where T : ContainerResource =>
        builder
            .WithImage(
                CloudflareTunnelContainerImageTags.Image,
                CloudflareTunnelContainerImageTags.Tag)
            .WithImageRegistry(CloudflareTunnelContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflareTunnelContainerDefaults.MetricsPort,
                name: CloudflareTunnelContainerDefaults.MetricsEndpointName)
            .WithHttpHealthCheck(
                "/ready",
                endpointName: CloudflareTunnelContainerDefaults.MetricsEndpointName);

    private static void ConfigureInteractiveInput(
        IResourceBuilder<ParameterResource> accountId,
        IResourceBuilder<ParameterResource> apiToken)
    {
#pragma warning disable ASPIREINTERACTION001
        accountId.WithCustomInput(parameter => new()
        {
            InputType = InputType.Text,
            Name = parameter.Name,
            Placeholder = "Enter your Cloudflare account ID",
            Description = parameter.Description,
            Required = true,
        });
        apiToken.WithCustomInput(parameter => new()
        {
            InputType = InputType.Text,
            Name = parameter.Name,
            Placeholder = "Enter your Cloudflare API token",
            Description = parameter.Description,
            Required = true,
        });
#pragma warning restore ASPIREINTERACTION001
    }

    private static async Task ConfigureRunModeRoutesAsync(
        CloudflareTunnelResource tunnel,
        ResourceReadyEvent @event,
        CancellationToken cancellationToken)
    {
        var model = @event.Services.GetRequiredService<DistributedApplicationModel>();
        var routes = model.Resources
            .OfType<PublishedRouteResource>()
            .Where(route => ReferenceEquals(route.Tunnel, tunnel))
            .ToArray();
        var loggerService = @event.Services.GetRequiredService<ResourceLoggerService>();
        var notifications = @event.Services.GetRequiredService<ResourceNotificationService>();
        var provisioner = @event.Services.GetRequiredService<CloudflareRouteProvisioner>();
        var logger = loggerService.GetLogger(tunnel);

        await CloudflareResourceLifecycle.RunIfAnyAsync(
            routes,
            token => ConfigureRoutesAsync(
                provisioner,
                tunnel,
                routes,
                loggerService,
                token),
            state => PublishStatesAsync(notifications, routes, state),
            KnownResourceStates.Running,
            logger,
            $"Failed to configure routes for Cloudflare tunnel '{tunnel.Name}'.",
            cancellationToken).ConfigureAwait(false);
    }

    private static Task ConfigureRoutesAsync(
        CloudflareRouteProvisioner provisioner,
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        ResourceLoggerService loggerService,
        CancellationToken cancellationToken) =>
        provisioner.ConfigureRoutesAsync(
            tunnel,
            routes,
            loggerService.GetLogger,
            cancellationToken);

    private static void AddPublishedRoute(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        IResource target,
        string hostname,
        EndpointReference endpoint)
    {
        var routeName = $"{tunnel.Resource.Name}-route-{MakeResourceName(hostname)}";
        var route = new PublishedRouteResource(
            routeName,
            hostname,
            endpoint,
            target,
            tunnel.Resource);

        builder.AddResource(route)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Cloudflare route",
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "Cloudflare API"),
                    new("Hostname", hostname),
                ],
            });
    }

    internal static string MakeResourceName(string hostname) =>
        hostname.Replace('.', '-').Replace(':', '-');

    internal static void AddQuickTunnelArguments(
        CommandLineArgsCallbackContext context,
        CloudflareQuickTunnelResource tunnel)
    {
        var endpoint = tunnel.TargetEndpoint
            ?? throw new InvalidOperationException(
                $"Cloudflare Quick Tunnel '{tunnel.Name}' must reference a target endpoint.");

        context.Args.Add("tunnel");
        context.Args.Add("--no-autoupdate");
        context.Args.Add("--metrics");
        context.Args.Add($"0.0.0.0:{CloudflareTunnelContainerDefaults.MetricsPort}");
        context.Args.Add("--url");
        context.Args.Add(endpoint);
    }

    private static Task PublishStatesAsync(
        ResourceNotificationService notifications,
        IEnumerable<PublishedRouteResource> routes,
        string state) =>
        Task.WhenAll(routes.Select(route => PublishStateAsync(notifications, route, state)));

    private static Task PublishStateAsync(
        ResourceNotificationService notifications,
        IResource resource,
        string state) =>
        notifications.PublishUpdateAsync(
            resource,
            snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(state, null),
            });
}
