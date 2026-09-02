#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting;

internal static class ModuleImageBuildPipeline
{
    internal const string BuildContainerImageTag = "build-module-container-image";

    public static void AddBuildStep(IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);
        container.WithPipelineStepFactory(factoryContext =>
        {
            var resource = factoryContext.Resource;
            if (resource.IsExcludedFromPublish() ||
                resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault() is null)
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = GetStepName(resource),
                    Description = $"Builds the effective module container image for {resource.Name}.",
                    Action = context => BuildAsync(resource, context),
                    DependsOnSteps =
                    [
                        WellKnownPipelineSteps.BuildPrereq,
                        WellKnownPipelineSteps.CheckContainerRuntime
                    ],
                    RequiredBySteps = [WellKnownPipelineSteps.Build],
                    Tags = [BuildContainerImageTag, WellKnownPipelineTags.BuildCompute],
                    Resource = resource
                }
            ];
        });
    }

    internal static string GetStepName(IResource resource) => $"build-{resource.Name}";

    internal static async Task BuildAsync(
        IResource resource,
        PipelineStepContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a module image publisher.");
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);
        var task = await context.ReportingStep.CreateTaskAsync(
            $"Prepare image for {resource.Name}",
            context.CancellationToken).ConfigureAwait(false);
        await using var configuredTask = task.ConfigureAwait(false);
        try
        {
            var prepared = await publisher.PrepareAsync(
                context.Services,
                NullLogger.Instance,
                resourceLogger,
                context.CancellationToken).ConfigureAwait(false);
            await task.SucceedAsync(
                $"Prepared {prepared.CanonicalImageReference}",
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await task.FailAsync(
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
