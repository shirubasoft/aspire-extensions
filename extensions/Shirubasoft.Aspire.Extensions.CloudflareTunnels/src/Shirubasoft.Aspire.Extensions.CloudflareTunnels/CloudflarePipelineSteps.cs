using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

#pragma warning disable ASPIREPIPELINES001

internal static class CloudflarePipelineSteps
{
    internal const string Tag = "cloudflare";

    // Docker Compose tags its compose-up step with docker-compose-up, and Kubernetes tags
    // its Helm deployment step with helm-deploy. Azure Container Apps, Azure App Service,
    // and custom compute environments tag their deployment steps with
    // WellKnownPipelineTags.DeployCompute.
    private static readonly string[] DeploymentTags =
    [
        "docker-compose-up",
        "helm-deploy",
        WellKnownPipelineTags.DeployCompute,
    ];

    public static PipelineStep CreateConfigureRoutesStep(CloudflareTunnelResource tunnel) =>
        new ConfigureRoutesStep(tunnel);

    public static string GetConfigureRoutesStepName(CloudflareTunnelResource tunnel) =>
        $"configure-{tunnel.Name}-cloudflare-routes";

    // Routes switch public traffic to the deployment, so the route step waits until
    // the tunnel connector and every target have deployed. A failed deployment step
    // stops the route step, and DNS and ingress keep serving the previous deployment.
    public static void ConfigureDependencies(
        PipelineConfigurationContext context,
        CloudflareTunnelResource tunnel)
    {
        ResourceDeployment[] deployments =
        [
            .. GetDeployedResources(context.Model, tunnel)
                .Select(resource => GetDeployment(context, resource)),
        ];

        foreach (var step in context.GetSteps(tunnel, Tag).OfType<ConfigureRoutesStep>())
        {
            step.WaitFor(deployments);
        }
    }

    private static ResourceDeployment GetDeployment(
        PipelineConfigurationContext context,
        IResource resource) =>
        GetDeployment(context, resource, resource.GetDeploymentTargetAnnotation());

    private static ResourceDeployment GetDeployment(
        PipelineConfigurationContext context,
        IResource resource,
        DeploymentTargetAnnotation? target) =>
        target is null
            ? new() { Resource = resource, Steps = GetDeploymentSteps(context, [resource]) }
            : new()
            {
                Resource = resource,
                ComputeEnvironment = target.ComputeEnvironment ?? target.DeploymentTarget,
                Steps = GetDeploymentSteps(
                    context,
                    [target.ComputeEnvironment, target.DeploymentTarget, resource]),
            };

    private static PipelineStep[] GetDeploymentSteps(
        PipelineConfigurationContext context,
        IEnumerable<IResource?> owners) =>
        owners
            .OfType<IResource>()
            .SelectMany(owner => DeploymentTags.SelectMany(tag => context.GetSteps(owner, tag)))
            .Distinct()
            .ToArray();

    private static IEnumerable<IResource> GetDeployedResources(
        DistributedApplicationModel model,
        CloudflareTunnelResource tunnel) =>
        GetRoutes(model, tunnel)
            .Select(route => route.TargetResource)
            .Prepend(tunnel)
            .Distinct();

    internal static PublishedRouteResource[] GetRoutes(
        DistributedApplicationModel model,
        CloudflareTunnelResource tunnel) =>
        model.Resources
            .OfType<PublishedRouteResource>()
            .Where(route => ReferenceEquals(route.Tunnel, tunnel))
            .ToArray();

    // The route step cannot wait for a deployment whose step it did not find, so it
    // refuses to point public hostnames at that deployment.
    private static void RequireDeploymentSteps(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<ResourceDeployment> unresolvedDeployments)
    {
        if (unresolvedDeployments.Count > 0)
        {
            throw new InvalidOperationException(
                DescribeUnresolvedDeployments(tunnel, unresolvedDeployments));
        }
    }

    private static string DescribeUnresolvedDeployments(
        CloudflareTunnelResource tunnel,
        IEnumerable<ResourceDeployment> unresolvedDeployments) =>
        $"Cloudflare tunnel '{tunnel.Name}' did not configure routes because it found no " +
        "deployment step to wait for: " +
        string.Join("; ", unresolvedDeployments
            .GroupBy(deployment => deployment.ComputeEnvironment!.Name)
            .Select(environment =>
                $"compute environment '{environment.Key}' deploys " +
                Quote(environment.Select(deployment => deployment.Resource.Name)))) +
        $". The route step waits for steps tagged one of {Quote(DeploymentTags)} that " +
        "belong to the compute environment, the deployment target, or the resource. Tag " +
        "the step that deploys the compute environment with " +
        $"'{WellKnownPipelineTags.DeployCompute}'.";

    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{value}'"));

    internal static Task ExecuteContextAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<ResourceDeployment> unresolvedDeployments,
        PipelineStepContext context)
    {
        var routes = GetRoutes(context.Model, tunnel);

        return ExecuteAsync(
            tunnel,
            routes,
            token => ConfigureRoutesAsync(tunnel, routes, unresolvedDeployments, context, token),
            context.Logger,
            summary => context.Summary.Add(
                $"Cloudflare tunnel '{tunnel.Name}'",
                summary),
            context.CancellationToken);
    }

    private static Task ConfigureRoutesAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        IReadOnlyList<ResourceDeployment> unresolvedDeployments,
        PipelineStepContext context,
        CancellationToken cancellationToken)
    {
        RequireDeploymentSteps(tunnel, unresolvedDeployments);

        return context.Services
            .GetRequiredService<CloudflareRouteProvisioner>()
            .ConfigureRoutesForPipelineAsync(
                tunnel,
                routes,
                context.ExecutionContext,
                (route, warning, warningToken) => ReportWarningAsync(
                    context.ReportingStep,
                    route,
                    warning,
                    warningToken),
                context.Logger,
                cancellationToken);
    }

    internal static async Task ExecuteAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<PublishedRouteResource> routes,
        Func<CancellationToken, Task> configure,
        ILogger logger,
        Action<string> addSummary,
        CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
        {
            logger.LogInformation(
                "Tunnel '{TunnelName}' has no published routes to configure.",
                tunnel.Name);
            return;
        }

        await configure(cancellationToken).ConfigureAwait(false);

        addSummary(string.Join(", ", routes.Select(route => route.Hostname)));
    }

    // A warning task marks the step, and the pipeline, as completed with warnings.
    internal static async Task ReportWarningAsync(
        IReportingStep step,
        PublishedRouteResource route,
        string warning,
        CancellationToken cancellationToken)
    {
        var task = await step.CreateTaskAsync(
            $"Resolve the service URL for {route.Hostname}",
            cancellationToken).ConfigureAwait(false);
        await using var configuredTask = task.ConfigureAwait(false);
        await task.WarnAsync(warning, cancellationToken).ConfigureAwait(false);
    }
}

// The route step of one pipeline resolution. Aspire creates the step, passes it to
// pipeline configuration, and then runs it, so UnresolvedDeployments describes the
// deployment that this run performs.
internal sealed class ConfigureRoutesStep : PipelineStep
{
    [SetsRequiredMembers]
    public ConfigureRoutesStep(CloudflareTunnelResource tunnel)
    {
        Name = CloudflarePipelineSteps.GetConfigureRoutesStepName(tunnel);
        Description = $"Configure Cloudflare routes for tunnel '{tunnel.Name}'.";
        DependsOnSteps = [WellKnownPipelineSteps.Publish];
        RequiredBySteps = [WellKnownPipelineSteps.Deploy];
        Tags = [CloudflarePipelineSteps.Tag];
        Action = context => CloudflarePipelineSteps.ExecuteContextAsync(
            tunnel,
            UnresolvedDeployments,
            context);
    }

    // Deployed resources whose deployment step the configuration did not find.
    public IReadOnlyList<ResourceDeployment> UnresolvedDeployments { get; private set; } = [];

    public void WaitFor(IReadOnlyList<ResourceDeployment> deployments)
    {
        foreach (var step in deployments.SelectMany(deployment => deployment.Steps).Distinct())
        {
            DependsOn(step);
        }

        UnresolvedDeployments = [.. deployments.Where(deployment => deployment.IsUnresolved)];
    }
}

// The tunnel or a route target, the compute environment that deploys it, and the pipeline
// steps that deploy it. A resource without a deployment target has no compute environment.
// When a deployment target names no compute environment, the target stands in for it.
internal sealed record ResourceDeployment
{
    public required IResource Resource { get; init; }

    public IResource? ComputeEnvironment { get; init; }

    public required IReadOnlyList<PipelineStep> Steps { get; init; }

    public bool IsUnresolved => ComputeEnvironment is not null && Steps.Count == 0;
}

#pragma warning restore ASPIREPIPELINES001
