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
        Func<IResource, ILogger> loggerFactory,
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
        Func<PublishedRouteResource, RouteWarning, CancellationToken, Task> reportWarning,
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
            reportWarning,
            logger,
            cancellationToken);

    internal async Task ConfigureRoutesForPipelineAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        Func<PublishedRouteResource, CancellationToken, Task<string>> serviceUrlResolver,
        Func<PublishedRouteResource, RouteWarning, CancellationToken, Task> reportWarning,
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
            await ReconcileAsync(
                new RouteReconciliationContext
                {
                    Client = client,
                    Tunnel = tunnel,
                    TunnelId = tunnelInfo.Id,
                    Routes = routes,
                    ResolveServiceUrl = route => serviceUrlResolver(route, cancellationToken),
                    Logger = _ => logger,
                    ReportWarning = reportWarning,
                },
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
        Func<IResource, ILogger> loggerFactory,
        CancellationToken cancellationToken)
    {
        var tunnelId = RequireTunnelId(tunnel);
        await _configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = await clientFactory
                .CreateAsync(tunnel, cancellationToken)
                .ConfigureAwait(false);
            await ReconcileAsync(
                new RouteReconciliationContext
                {
                    Client = client,
                    Tunnel = tunnel,
                    TunnelId = tunnelId,
                    Routes = routes,
                    ResolveServiceUrl = serviceUrlResolver,
                    Logger = loggerFactory,
                    ReportWarning = (route, warning, _) => LogWarningAsync(loggerFactory(route), warning),
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationLock.Release();
        }
    }

    internal static Task LogWarningAsync(ILogger logger, RouteWarning warning)
    {
        logger.LogWarning("{Warning}", warning.Message);
        return Task.CompletedTask;
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

    // Writes DNS records first, deletes stale owned records next, and replaces the
    // ingress last. Until the ingress update succeeds, it still lists every hostname
    // removed from the AppHost, so the next run finds the zones to clean up.
    private static async Task ReconcileAsync(
        RouteReconciliationContext context,
        CancellationToken cancellationToken)
    {
        var current = await context.Client
            .GetTunnelConfigurationAsync(context.TunnelId, cancellationToken)
            .ConfigureAwait(false)
            ?? new TunnelConfiguration();
        var zones = await FindZonesAsync(
            context.Client,
            GetCandidateHostnames(context.Routes, current),
            cancellationToken).ConfigureAwait(false);

        var rules = await ConfigureDnsRecordsAsync(context, zones, cancellationToken).ConfigureAwait(false);
        await DeleteStaleRecordsAsync(context, zones, cancellationToken).ConfigureAwait(false);
        await UpdateIngressAsync(context, current, rules, cancellationToken).ConfigureAwait(false);
    }

    internal static IEnumerable<string> GetCandidateHostnames(
        IEnumerable<PublishedRouteResource> routes,
        TunnelConfiguration current) =>
        routes
            .Select(route => route.Hostname)
            .Concat(current.Ingress.Select(rule => rule.Hostname).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, CloudflareZoneInfo?>> FindZonesAsync(
        ICloudflareApiClient client,
        IEnumerable<string> hostnames,
        CancellationToken cancellationToken)
    {
        var zones = new Dictionary<string, CloudflareZoneInfo?>(StringComparer.OrdinalIgnoreCase);
        foreach (var hostname in hostnames)
        {
            zones[hostname] = await FindZoneAsync(client, hostname, cancellationToken).ConfigureAwait(false);
        }

        return zones;
    }

    internal static CloudflareZoneInfo RequireZone(
        IReadOnlyDictionary<string, CloudflareZoneInfo?> zones,
        PublishedRouteResource route) =>
        zones[route.Hostname]
            ?? throw new InvalidOperationException(
                $"Cloudflare zone for '{route.Hostname}' was not found.");

    private static async Task<List<IngressRule>> ConfigureDnsRecordsAsync(
        RouteReconciliationContext context,
        IReadOnlyDictionary<string, CloudflareZoneInfo?> zones,
        CancellationToken cancellationToken)
    {
        var targets = context.Routes
            .Select(route => (Route: route, Zone: RequireZone(zones, route)))
            .ToArray();
        var rules = new List<IngressRule>(targets.Length);
        foreach (var (route, zone) in targets)
        {
            rules.Add(await ConfigureRouteAsync(context, route, zone, cancellationToken).ConfigureAwait(false));
        }

        return rules;
    }

    private static async Task<IngressRule> ConfigureRouteAsync(
        RouteReconciliationContext context,
        PublishedRouteResource route,
        CloudflareZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var serviceUrl = await context.ResolveServiceUrl(route).ConfigureAwait(false);
        var desired = CloudflareRouteOwnership.DesiredRecord(context.Tunnel, context.TunnelId, route.Hostname);
        var existing = await context.Client
            .FindDnsRecordsAsync(zone.Id, desired.Name, cancellationToken)
            .ConfigureAwait(false);

        await CloudflareRouteOwnership.Plan(desired, existing).ApplyAsync(
            new DnsRecordChangeContext
            {
                Client = context.Client,
                ZoneId = zone.Id,
                Route = route,
                Desired = desired,
                ReportWarning = context.ReportWarning,
            },
            cancellationToken).ConfigureAwait(false);

        context.Logger(route).LogInformation(
            "Configured {Hostname} to route to {ServiceUrl}.",
            route.Hostname,
            serviceUrl);

        return new IngressRule
        {
            Hostname = route.Hostname,
            Service = serviceUrl,
        };
    }

    private static async Task DeleteStaleRecordsAsync(
        RouteReconciliationContext context,
        IReadOnlyDictionary<string, CloudflareZoneInfo?> zones,
        CancellationToken cancellationToken)
    {
        var zoneIds = zones.Values
            .OfType<CloudflareZoneInfo>()
            .Select(zone => zone.Id)
            .Distinct(StringComparer.Ordinal);

        foreach (var zoneId in zoneIds)
        {
            await DeleteStaleRecordsAsync(context, zoneId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteStaleRecordsAsync(
        RouteReconciliationContext context,
        string zoneId,
        CancellationToken cancellationToken)
    {
        var ownedRecords = await context.Client
            .FindDnsRecordsByCommentAsync(
                zoneId,
                CloudflareRouteOwnership.OwnerComment(context.Tunnel),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var record in CloudflareRouteOwnership.FindStaleRecords(ownedRecords, context.Routes))
        {
            await context.Client
                .DeleteDnsRecordAsync(zoneId, record.Id, cancellationToken)
                .ConfigureAwait(false);
            context.Logger(context.Tunnel).LogInformation(
                "Deleted the DNS record for {Hostname} because no route declares it.",
                record.Name);
        }
    }

    private static Task UpdateIngressAsync(
        RouteReconciliationContext context,
        TunnelConfiguration current,
        IEnumerable<IngressRule> rules,
        CancellationToken cancellationToken)
    {
        var desired = CloudflareRouteOwnership.DesiredConfiguration(rules);

        return CloudflareRouteOwnership.IsUnchanged(current, desired)
            ? Task.CompletedTask
            : context.Client.UpdateTunnelConfigurationAsync(
                context.TunnelId,
                desired,
                cancellationToken);
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
        Func<PublishedRouteResource, RouteWarning, CancellationToken, Task> reportWarning,
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
        Func<PublishedRouteResource, RouteWarning, CancellationToken, Task> reportWarning,
        CancellationToken cancellationToken) =>
        expression.HasUnknownTargetPort
            ? reportWarning(
                route,
                new RouteWarning(
                    $"Resolve the service URL for {route.Hostname}",
                    "Docker Compose assigns the container port of endpoint " +
                    $"'{route.TargetEndpoint.EndpointName}' on resource " +
                    $"'{route.TargetResource.Name}', and the route step cannot discover it. " +
                    $"{route.Hostname} routes to {serviceUrl} instead. Set the endpoint's " +
                    "target port if the service listens on another port."),
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

internal sealed record RouteReconciliationContext
{
    public required ICloudflareApiClient Client { get; init; }

    public required CloudflareTunnelResource Tunnel { get; init; }

    public required string TunnelId { get; init; }

    public required IReadOnlyList<PublishedRouteResource> Routes { get; init; }

    public required Func<PublishedRouteResource, Task<string>> ResolveServiceUrl { get; init; }

    public required Func<IResource, ILogger> Logger { get; init; }

    public required Func<PublishedRouteResource, RouteWarning, CancellationToken, Task> ReportWarning { get; init; }
}

internal readonly record struct ServiceUrlExpression(
    ReferenceExpression Url,
    bool HasUnknownTargetPort = false);
