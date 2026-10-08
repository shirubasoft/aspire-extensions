using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

#pragma warning disable ASPIREPIPELINES001

internal static class CloudflarePipelineSteps
{
    internal const string Tag = "cloudflare";

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

    private static Task ExecuteContextAsync(
        CloudflareTunnelResource tunnel,
        PipelineStepContext context)
    {
        var routes = context.Model.Resources
            .OfType<PublishedRouteResource>()
            .Where(route => ReferenceEquals(route.Tunnel, tunnel))
            .ToArray();

        return ExecuteAsync(
            tunnel,
            routes,
            token => context.Services
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
                    token),
            context.Logger,
            summary => context.Summary.Add(
                $"Cloudflare tunnel '{tunnel.Name}'",
                summary),
            context.CancellationToken);
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

#pragma warning restore ASPIREPIPELINES001
