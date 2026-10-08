using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

#pragma warning disable ASPIREPIPELINES001

internal static class CloudflarePipelineSteps
{
    internal const string Tag = "cloudflare";

    // Docker Compose tags its compose-up step with this value. Other compute
    // environments tag their deployment steps with WellKnownPipelineTags.DeployCompute.
    private static readonly string[] DeploymentTags =
    [
        "docker-compose-up",
        WellKnownPipelineTags.DeployCompute,
    ];

    public static PipelineStep CreateConfigureRoutesStep(CloudflareTunnelResource tunnel) =>
        new()
        {
            Name = GetConfigureRoutesStepName(tunnel),
            Description = $"Configure Cloudflare routes for tunnel '{tunnel.Name}'.",
            DependsOnSteps = [WellKnownPipelineSteps.Publish],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy],
            Tags = [Tag],
            Action = context => ExecuteContextAsync(tunnel, context),
        };

    public static string GetConfigureRoutesStepName(CloudflareTunnelResource tunnel) =>
        $"configure-{tunnel.Name}-cloudflare-routes";

    // Routes switch public traffic to the deployment, so the route step waits until
    // the tunnel connector and every target have deployed. A failed deployment step
    // stops the route step, and DNS and ingress keep serving the previous deployment.
    public static void ConfigureDependencies(
        PipelineConfigurationContext context,
        CloudflareTunnelResource tunnel) =>
        context
            .GetSteps(tunnel, Tag)
            .DependsOn(GetDeploymentSteps(
                context,
                [tunnel, .. GetRoutes(context.Model, tunnel).Select(route => route.TargetResource)]));

    internal static IEnumerable<PipelineStep> GetDeploymentSteps(
        PipelineConfigurationContext context,
        IEnumerable<IResource> resources) =>
        resources
            .Select(resource => resource.GetDeploymentTargetAnnotation())
            .OfType<DeploymentTargetAnnotation>()
            .SelectMany(target => new IResource?[] { target.ComputeEnvironment, target.DeploymentTarget })
            .OfType<IResource>()
            .SelectMany(owner => DeploymentTags.SelectMany(tag => context.GetSteps(owner, tag)))
            .Distinct();

    internal static PublishedRouteResource[] GetRoutes(
        DistributedApplicationModel model,
        CloudflareTunnelResource tunnel) =>
        model.Resources
            .OfType<PublishedRouteResource>()
            .Where(route => ReferenceEquals(route.Tunnel, tunnel))
            .ToArray();

    private static Task ExecuteContextAsync(
        CloudflareTunnelResource tunnel,
        PipelineStepContext context)
    {
        var routes = GetRoutes(context.Model, tunnel);

        return ExecuteAsync(
            routes,
            token => context.Services
                .GetRequiredService<CloudflareRouteProvisioner>()
                .ConfigureRoutesForPipelineAsync(
                    tunnel,
                    routes,
                    context.ExecutionContext,
                    (_, warning, warningToken) => ReportWarningAsync(
                        context.ReportingStep,
                        warning,
                        warningToken),
                    context.Logger,
                    token),
            summary => context.Summary.Add(
                $"Cloudflare tunnel '{tunnel.Name}'",
                summary),
            context.CancellationToken);
    }

    // The declared routes are the tunnel's complete set, so a tunnel without routes
    // still runs to remove the routes of earlier deployments.
    internal static async Task ExecuteAsync(
        IReadOnlyList<PublishedRouteResource> routes,
        Func<CancellationToken, Task> configure,
        Action<string> addSummary,
        CancellationToken cancellationToken)
    {
        await configure(cancellationToken).ConfigureAwait(false);

        addSummary(routes.Count == 0
            ? "No routes"
            : string.Join(", ", routes.Select(route => route.Hostname)));
    }

    // A warning task marks the step, and the pipeline, as completed with warnings.
    internal static async Task ReportWarningAsync(
        IReportingStep step,
        RouteWarning warning,
        CancellationToken cancellationToken)
    {
        var task = await step.CreateTaskAsync(
            warning.Activity,
            cancellationToken).ConfigureAwait(false);
        await using var configuredTask = task.ConfigureAwait(false);
        await task.WarnAsync(warning.Message, cancellationToken).ConfigureAwait(false);
    }
}

#pragma warning restore ASPIREPIPELINES001
