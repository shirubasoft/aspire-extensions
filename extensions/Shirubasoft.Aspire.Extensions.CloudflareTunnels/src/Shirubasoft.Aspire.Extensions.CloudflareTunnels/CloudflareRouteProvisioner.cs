using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class CloudflareRouteProvisioner(ICloudflareApiClientFactory clientFactory)
{
    private readonly SemaphoreSlim _configurationLock = new(1, 1);

    public Task ConfigureRoutesAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        Func<PublishedRouteResource, ILogger> loggerFactory,
        CancellationToken cancellationToken) =>
        ConfigureRoutesAsync(
            tunnel,
            routes,
            route => BuildRunModeServiceUrlAsync(route, cancellationToken),
            loggerFactory,
            cancellationToken);

    public Task ConfigureRoutesForPipelineAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger,
        CancellationToken cancellationToken) =>
        ConfigureRoutesForPipelineAsync(
            tunnel,
            routes,
            (route, token) => BuildPipelineServiceUrlAsync(
                tunnel,
                route,
                executionContext,
                token),
            logger,
            cancellationToken);

    internal async Task ConfigureRoutesForPipelineAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        Func<PublishedRouteResource, CancellationToken, Task<string>> serviceUrlResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await _configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = await clientFactory
                .CreateAsync(tunnel, cancellationToken)
                .ConfigureAwait(false);
            var tunnelInfo = RequireExistingTunnel(await client
                .FindTunnelByNameAsync(tunnel.Name, cancellationToken)
                .ConfigureAwait(false), tunnel.Name);

            tunnel.TunnelId = tunnelInfo.Id;
            await ConfigureRoutesAsync(
                client,
                tunnelInfo.Id,
                routes,
                route => serviceUrlResolver(route, cancellationToken),
                _ => logger,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationLock.Release();
        }
    }

    internal static string RequireTunnelId(CloudflareTunnelResource tunnel) =>
        !string.IsNullOrWhiteSpace(tunnel.TunnelId)
            ? tunnel.TunnelId
            : throw new InvalidOperationException(
                $"Cloudflare tunnel '{tunnel.Name}' has not been provisioned.");

    internal static CloudflareTunnelInfo RequireExistingTunnel(
        CloudflareTunnelInfo? tunnel,
        string tunnelName) =>
        tunnel ?? throw new InvalidOperationException(
            $"Cloudflare tunnel '{tunnelName}' was not found. Create it before deployment.");

    internal async Task ConfigureRoutesAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        Func<PublishedRouteResource, Task<string>> serviceUrlResolver,
        Func<PublishedRouteResource, ILogger> loggerFactory,
        CancellationToken cancellationToken)
    {
        var tunnelId = RequireTunnelId(tunnel);
        await _configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = await clientFactory
                .CreateAsync(tunnel, cancellationToken)
                .ConfigureAwait(false);
            await ConfigureRoutesAsync(
                client,
                tunnelId,
                routes,
                serviceUrlResolver,
                loggerFactory,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationLock.Release();
        }
    }

    internal static async Task<CloudflareZoneInfo?> FindZoneAsync(
        ICloudflareApiClient client,
        string hostname,
        CancellationToken cancellationToken)
    {
        var labels = hostname
            .TrimEnd('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < labels.Length - 1; index++)
        {
            var candidate = string.Join('.', labels[index..]);
            var zone = await client
                .FindZoneByNameAsync(candidate, cancellationToken)
                .ConfigureAwait(false);

            if (zone is not null)
            {
                return zone;
            }
        }

        return null;
    }

    private static async Task ConfigureRoutesAsync(
        ICloudflareApiClient client,
        string tunnelId,
        IReadOnlyList<PublishedRouteResource> routes,
        Func<PublishedRouteResource, Task<string>> serviceUrlResolver,
        Func<PublishedRouteResource, ILogger> loggerFactory,
        CancellationToken cancellationToken)
    {
        var configuration = await client
            .GetTunnelConfigurationAsync(tunnelId, cancellationToken)
            .ConfigureAwait(false)
            ?? new TunnelConfiguration();

        RemoveManagedIngressRules(configuration, routes);

        foreach (var route in routes)
        {
            var ingressRule = await ConfigureRouteAsync(
                client,
                tunnelId,
                route,
                await serviceUrlResolver(route).ConfigureAwait(false),
                loggerFactory(route),
                cancellationToken).ConfigureAwait(false);
            configuration.Ingress.Add(ingressRule);
        }

        configuration.Ingress.Add(new IngressRule
        {
            Service = "http_status:404",
        });

        await client
            .UpdateTunnelConfigurationAsync(tunnelId, configuration, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static void RemoveManagedIngressRules(
        TunnelConfiguration configuration,
        IReadOnlyList<PublishedRouteResource> routes)
    {
        var managedHostnames = routes
            .Select(route => route.Hostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        configuration.Ingress.RemoveAll(rule =>
            rule.Hostname is null || managedHostnames.Contains(rule.Hostname));
    }

    private static async Task<IngressRule> ConfigureRouteAsync(
        ICloudflareApiClient client,
        string tunnelId,
        PublishedRouteResource route,
        string serviceUrl,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var zone = await FindZoneAsync(client, route.Hostname, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cloudflare zone for '{route.Hostname}' was not found.");

        await client
            .UpsertTunnelDnsRecordAsync(
                zone.Id,
                route.Hostname,
                tunnelId,
                cancellationToken)
            .ConfigureAwait(false);

        route.DnsRecordCreated = true;
        logger.LogInformation(
            "Configured {Hostname} to route to {ServiceUrl}.",
            route.Hostname,
            serviceUrl);

        return new IngressRule
        {
            Hostname = route.Hostname,
            Service = serviceUrl,
        };
    }

    private static async Task<string> BuildRunModeServiceUrlAsync(
        PublishedRouteResource route,
        CancellationToken cancellationToken)
    {
        var serviceUrl = await route.TargetEndpoint
            .GetValueAsync(cancellationToken)
            .ConfigureAwait(false);

        return RequireServiceUrl(serviceUrl, route);
    }

    internal static string RequireServiceUrl(
        string? serviceUrl,
        PublishedRouteResource route) =>
        !string.IsNullOrWhiteSpace(serviceUrl)
            ? serviceUrl
            : throw new InvalidOperationException(
                $"Endpoint '{route.TargetEndpoint.EndpointName}' for resource " +
                $"'{route.TargetResource.Name}' could not be resolved.");

    private static async Task<string> BuildPipelineServiceUrlAsync(
        CloudflareTunnelResource tunnel,
        PublishedRouteResource route,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var deploymentTarget = route.TargetResource.GetDeploymentTargetAnnotation();
        ArgumentNullException.ThrowIfNull(deploymentTarget);
        var computeEnvironment = deploymentTarget.ComputeEnvironment;
        ArgumentNullException.ThrowIfNull(computeEnvironment);

        var expression = GetServiceUrlExpression(computeEnvironment, route);

        var serviceUrl = await expression.GetValueAsync(
            new ValueProviderContext
            {
                Caller = tunnel,
                ExecutionContext = executionContext,
            },
            cancellationToken).ConfigureAwait(false);

        return RequireServiceUrl(serviceUrl, route);
    }

#pragma warning disable ASPIRECOMPUTE002
    internal static ReferenceExpression GetServiceUrlExpression(
        IComputeEnvironmentResource computeEnvironment,
        PublishedRouteResource route) =>
        computeEnvironment is DockerComposeEnvironmentResource
            ? GetComposeServiceUrlExpression(computeEnvironment, route)
            : computeEnvironment.GetEndpointPropertyExpression(
                route.TargetEndpoint.Property(EndpointProperty.Url));

    // Docker Compose inherits the default endpoint expression, which assumes an
    // ingress on port 80 or 443. Inside the Compose network, cloudflared reaches
    // the service on the container port instead.
    private static ReferenceExpression GetComposeServiceUrlExpression(
        IComputeEnvironmentResource computeEnvironment,
        PublishedRouteResource route)
    {
        var host = computeEnvironment.GetHostAddressExpression(route.TargetEndpoint);
        var port = GetComposeTargetPortExpression(route);

        return ReferenceExpression.Create(
            $"{route.TargetEndpoint.Scheme}://{host}:{port}");
    }
#pragma warning restore ASPIRECOMPUTE002

    // Docker Compose resolves container ports with the same API.
    internal static ReferenceExpression GetComposeTargetPortExpression(
        PublishedRouteResource route)
    {
        var targetPort = route.TargetResource
            .ResolveEndpoints()
            .Single(endpoint => ReferenceEquals(
                endpoint.Endpoint,
                route.TargetEndpoint.EndpointAnnotation))
            .TargetPort;

        if (targetPort.IsAllocated)
        {
            throw new InvalidOperationException(
                $"Endpoint '{route.TargetEndpoint.EndpointName}' for resource " +
                $"'{route.TargetResource.Name}' has no target port. Set its target " +
                "port so the Cloudflare route can reach it.");
        }

        return targetPort.Value is { } port
            ? ReferenceExpression.Create($"{port.ToString(CultureInfo.InvariantCulture)}")
            : ReferenceExpression.Create($"{new ContainerPortReference(route.TargetResource)}");
    }
}
