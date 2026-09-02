#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal static class ModuleImagePullPipeline
{
    internal const string PullStepName = "pull";
    internal const string PullPrerequisiteStepName = "pull-prereq";
    internal const string PullContainerImageTag = "pull-container-image";

    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(3, nameof(LogContainerRuntimeOutput)),
            "{ContainerRuntime}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogImagePullStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(5, nameof(LogImagePullStarted)),
            "Pulling remote image {RemoteImage} for resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogImageRetagStarted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(6, nameof(LogImageRetagStarted)),
            "Re-tagging remote image {RemoteImage} as local image {LocalImage} for resource {ResourceName}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogImageRetagCompleted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(7, nameof(LogImageRetagCompleted)),
            "Re-tagged remote image {RemoteImage} as local image {LocalImage} for resource {ResourceName}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = PullStepName,
            Description = "Aggregation step for all modular container image pull operations.",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [PullPrerequisiteStepName]
        });
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = PullPrerequisiteStepName,
            Description = "Prerequisite step that runs before modular container image pulls.",
            Action = _ => Task.CompletedTask
        });

    }

    public static void AddPullStep(IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.WithPipelineStepFactory(factoryContext =>
        {
            var resource = factoryContext.Resource;
            if (resource.IsExcludedFromPublish() || !ModuleEffectiveImageResolver.HasPullSource(resource))
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = $"pull-{resource.Name}",
                    Description = $"Pulls the container image for the {resource.Name} resource.",
                    Action = context => PullAsync(resource, context),
                    DependsOnSteps =
                    [
                        PullPrerequisiteStepName,
                        WellKnownPipelineSteps.CheckContainerRuntime
                    ],
                    RequiredBySteps = [PullStepName],
                    Tags = [PullContainerImageTag],
                    Resource = resource
                }
            ];
        });
    }

    internal static async Task<(string RemoteImage, string LocalImage)> ResolveImageReferencesAsync(
        IResource resource,
        CancellationToken cancellationToken)
    {
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(resource, cancellationToken)
            .ConfigureAwait(false);
        return (resolved.PullReference, resolved.Reference);
    }

    private static async Task PullAsync(IResource resource, PipelineStepContext context)
    {
        var timeout = context.Services
            .GetRequiredService<IOptions<ModularAppHostsOptions>>()
            .Value.ImageTransferTimeout;
        var runtime = await context.Services
            .GetRequiredService<IContainerRuntimeResolver>()
            .ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        await ModuleOperationTimeout.RunAsync(
            token => PullWithRuntimeAsync(resource, context, runtime, token),
            timeout,
            $"Image pull for resource '{resource.Name}'",
            context.CancellationToken).ConfigureAwait(false);
    }

    private static Task PullWithRuntimeAsync(
        IResource resource,
        PipelineStepContext context,
        IContainerRuntime runtime,
        CancellationToken cancellationToken) =>
        PullAsync(
            resource,
            context,
            _ => Task.FromResult(
                ModuleImageRecipeOperations.GetContainerRuntimeExecutableName(runtime.Name)),
            ExecuteRuntimeAsync,
            runtime.TagImageAsync,
            cancellationToken);

    internal static async Task PullAsync(
        IResource resource,
        PipelineStepContext context,
        Func<CancellationToken, Task<string>> resolveRuntimeAsync,
        Func<string, IReadOnlyList<string>, PipelineStepContext, ILogger, CancellationToken, Task> executeRuntimeAsync,
        Func<string, string, CancellationToken, Task> tagImageAsync,
        CancellationToken operationCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolveRuntimeAsync);
        ArgumentNullException.ThrowIfNull(executeRuntimeAsync);
        ArgumentNullException.ThrowIfNull(tagImageAsync);

        var cancellationToken = operationCancellationToken.CanBeCanceled
            ? operationCancellationToken
            : context.CancellationToken;
        var (remoteImage, localImage) = await ResolveImageReferencesAsync(
            resource,
            cancellationToken).ConfigureAwait(false);
        var runtime = await resolveRuntimeAsync(cancellationToken)
            .ConfigureAwait(false);
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);

        LogImagePullStarted(context.Logger, remoteImage, resource.Name, null);

        await executeRuntimeAsync(
            runtime,
            ["pull", remoteImage],
            context,
            resourceLogger,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(remoteImage, localImage, StringComparison.Ordinal))
        {
            LogImageRetagStarted(context.Logger, remoteImage, localImage, resource.Name, null);
            await tagImageAsync(
                remoteImage,
                localImage,
                cancellationToken).ConfigureAwait(false);
            LogImageRetagCompleted(context.Logger, remoteImage, localImage, resource.Name, null);
        }
    }

    internal static async Task ExecuteRuntimeAsync(
        string runtime,
        IReadOnlyList<string> arguments,
        PipelineStepContext context,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resourceLogger);

        var result = await CliCommand.Wrap(runtime)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(
                    resourceLogger,
                    runtime,
                    line,
                    null)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                LogContainerRuntimeOutput(
                    resourceLogger,
                    runtime,
                    line,
                    null)))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container runtime '{runtime}' failed with exit code {result.ExitCode}.");
        }
    }
}
