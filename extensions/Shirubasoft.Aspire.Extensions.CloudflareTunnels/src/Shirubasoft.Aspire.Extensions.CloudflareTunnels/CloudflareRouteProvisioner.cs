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
        Func<PublishedRouteResource, string, CancellationToken, Task> reportWarning,
        ILogger logger,
        CancellationToken cancellationToken) =>
        ConfigureRoutesForPipelineAsync(
            tunnel,
            routes,
            (route, token) => BuildPipelineServiceUrlAsync(
                tunnel,
                route,
                executionContext,
                reportWarning,
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
        Func<PublishedRouteResource, string, CancellationToken, Task> reportWarning,
        CancellationToken cancellationToken)
    {
        var deploymentTarget = route.TargetResource.GetDeploymentTargetAnnotation();
        ArgumentNullException.ThrowIfNull(deploymentTarget);
        var computeEnvironment = deploymentTarget.ComputeEnvironment;
        ArgumentNullException.ThrowIfNull(computeEnvironment);

        var expression = GetServiceUrlExpression(computeEnvironment, route);

        var serviceUrl = RequireServiceUrl(
            await expression.Url.GetValueAsync(
                new ValueProviderContext
                {
                    Caller = tunnel,
                    ExecutionContext = executionContext,
                },
                cancellationToken).ConfigureAwait(false),
            route);

        await ReportUnknownTargetPortAsync(
            expression,
            route,
            serviceUrl,
            reportWarning,
            cancellationToken).ConfigureAwait(false);

        return serviceUrl;
    }

    internal static Task ReportUnknownTargetPortAsync(
        ServiceUrlExpression expression,
        PublishedRouteResource route,
        string serviceUrl,
        Func<PublishedRouteResource, string, CancellationToken, Task> reportWarning,
        CancellationToken cancellationToken) =>
        expression.HasUnknownTargetPort
            ? reportWarning(
                route,
                "Docker Compose assigns the container port of endpoint " +
                $"'{route.TargetEndpoint.EndpointName}' on resource " +
                $"'{route.TargetResource.Name}', and the route step cannot discover it. " +
                $"{route.Hostname} routes to {serviceUrl} instead. Set the endpoint's " +
                "target port if the service listens on another port.",
                cancellationToken)
            : Task.CompletedTask;

#pragma warning disable ASPIRECOMPUTE002
    internal static ServiceUrlExpression GetServiceUrlExpression(
        IComputeEnvironmentResource computeEnvironment,
        PublishedRouteResource route) =>
        computeEnvironment is DockerComposeEnvironmentResource
            ? GetComposeServiceUrlExpression(computeEnvironment, route)
            : new(GetDefaultServiceUrlExpression(computeEnvironment, route));

    private static ReferenceExpression GetDefaultServiceUrlExpression(
        IComputeEnvironmentResource computeEnvironment,
        PublishedRouteResource route) =>
        computeEnvironment.GetEndpointPropertyExpression(
            route.TargetEndpoint.Property(EndpointProperty.Url));

    // Docker Compose inherits the default endpoint expression, which assumes an
    // ingress on port 80 or 443. Inside the Compose network, cloudflared reaches
    // the service on the container port that Compose resolves with the same API.
    // A port that Compose allocates depends on resource order, so the route keeps
    // the default expression for it.
    private static ServiceUrlExpression GetComposeServiceUrlExpression(
        IComputeEnvironmentResource computeEnvironment,
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
            return new(
                GetDefaultServiceUrlExpression(computeEnvironment, route),
                HasUnknownTargetPort: true);
        }

        var host = computeEnvironment.GetHostAddressExpression(route.TargetEndpoint);
        var port = targetPort.Value is { } value
            ? ReferenceExpression.Create($"{value.ToString(CultureInfo.InvariantCulture)}")
            : ReferenceExpression.Create($"{new ContainerPortReference(route.TargetResource)}");

        return new(ReferenceExpression.Create(
            $"{route.TargetEndpoint.Scheme}://{host}:{port}"));
    }
#pragma warning restore ASPIRECOMPUTE002
}

internal readonly record struct ServiceUrlExpression(
    ReferenceExpression Url,
    bool HasUnknownTargetPort = false);
