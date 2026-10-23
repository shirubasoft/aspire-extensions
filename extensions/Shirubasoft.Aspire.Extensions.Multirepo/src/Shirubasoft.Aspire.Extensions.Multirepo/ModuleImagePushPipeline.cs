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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal static class ModuleImagePushPipeline
{
    private static readonly Action<ILogger, string, string, Exception?> LogContainerRuntimeOutput =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogContainerRuntimeOutput)),
            "{ContainerRuntime}: {Output}");

    private static readonly Action<ILogger, string, string, Exception?> LogBranchAlias =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(3, nameof(LogBranchAlias)),
            "Publishing branch image alias {Alias} for resource {Resource}.");

    private static readonly Action<ILogger, string, string, Exception?> LogPushTarget =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(4, nameof(LogPushTarget)),
            "Publishing image push target {Target} for resource {Resource}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = "validate-image-push-targets",
            Description = "Rejects image push targets that a module image publisher cannot push.",
            Action = context =>
            {
                ValidatePushTargets(context.Model.Resources);
                return Task.CompletedTask;
            },
            RequiredBySteps = [WellKnownPipelineSteps.PushPrereq, ModuleImageWorkflowPipeline.StepName]
        });
    }

    public static void AddPushStep(IResourceBuilder<ContainerResource> container)
    {
        ArgumentNullException.ThrowIfNull(container);
        container.WithPipelineStepFactory(CreatePushSteps);
    }

    private static IReadOnlyList<PipelineStep> CreatePushSteps(PipelineStepFactoryContext factoryContext)
    {
        var resource = factoryContext.Resource;
        if (ShouldSkipPushStep(resource))
        {
            return [];
        }

        return
        [
            new PipelineStep
            {
                Name = $"push-{resource.Name}",
                Description = $"Pushes the existing container image and branch alias for the {resource.Name} resource.",
                Action = context => PushAsync(resource, context),
                DependsOnSteps =
                [
                    ModuleImageBuildPipeline.GetStepName(resource),
                    WellKnownPipelineSteps.PushPrereq,
                    WellKnownPipelineSteps.CheckContainerRuntime
                ],
                RequiredBySteps = [WellKnownPipelineSteps.Push],
                Tags = [WellKnownPipelineTags.PushContainerImage],
                Resource = resource
            }
        ];
    }

    private static bool ShouldSkipPushStep(IResource resource)
    {
        if (resource.IsExcludedFromPublish())
        {
            return true;
        }

        return RequiresBuildOrHasNothingToPush(resource);
    }

    private static bool RequiresBuildOrHasNothingToPush(IResource resource) =>
        resource.RequiresImageBuildAndPush() ||
        (!ModuleEffectiveImageResolver.HasPushTarget(resource) &&
            !resource.Annotations.OfType<ModuleImagePushTargetAnnotation>().Any());

    private static bool RequiresBuildOrHasNoPushTarget(IResource resource) =>
        resource.RequiresImageBuildAndPush() || !ModuleEffectiveImageResolver.HasPushTarget(resource);

    internal static void ValidatePushTargets(IEnumerable<IResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var resource = resources.Where(PublisherDeclaresPushTargets).FirstOrDefault(IgnoresPushTargets);
        if (resource is not null)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' declares WithImagePushTarget, but push targets add to a module image's " +
                "registry target. Give the resource an image publish command (WithImagePublishCommand, " +
                "ExportAsContainerWithCommand, or the command-oriented AddResource overload) and a registry " +
                "(WithContainerRegistry, ImageRegistry, or a deployment target registry), which can be one of the " +
                "push targets.");
        }
    }

    private static bool PublisherDeclaresPushTargets(IResource resource) =>
        resource.Annotations.OfType<ModuleImagePushTargetAnnotation>().Any() &&
        (resource.Annotations.OfType<ModuleImagePublisherAnnotation>().Any() ||
            resource.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().Any());

    private static bool IgnoresPushTargets(IResource resource) =>
        !resource.IsExcludedFromPublish() && RequiresBuildOrHasNoPushTarget(resource);

    private static async Task PushAsync(IResource resource, PipelineStepContext context)
    {
        var resourceLogger = context.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a module image publisher.");
        var task = await context.ReportingStep.CreateTaskAsync(
            $"Push image for {resource.Name}",
            context.CancellationToken).ConfigureAwait(false);
        await using var configuredTask = task.ConfigureAwait(false);
        try
        {
            await PushCoreAsync(resource, context, resourceLogger, publisher).ConfigureAwait(false);
            await task.SucceedAsync(
                $"Pushed image for {resource.Name}",
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

    private static async Task PushCoreAsync(
        IResource resource,
        PipelineStepContext context,
        ILogger resourceLogger,
        ModuleImagePublisherAnnotation publisher)
    {
        await PreparePublisherAsync(resource, context, resourceLogger, publisher).ConfigureAwait(false);
        var resolved = await ResolvePushTargetAsync(resource, context.CancellationToken)
            .ConfigureAwait(false);
        var imageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();
        var transferTimeout = context.Services
            .GetRequiredService<IOptions<ModularAppHostsOptions>>()
            .Value.ImageTransferTimeout;
        var runtime = await ResolveRuntimeIfNeededAsync(resolved, context).ConfigureAwait(false);
        await PushImageAsync(
            resolved.PushTargetKind,
            resource,
            resolved.PushReference!,
            (reference, token) => PushWithRuntimeAsync(runtime, reference, resourceLogger, token),
            imageManager.PushImageAsync,
            transferTimeout,
            context.CancellationToken).ConfigureAwait(false);
        await PushBranchAliasIfAvailableAsync(
            resource,
            resolved,
            runtime,
            context,
            resourceLogger,
            transferTimeout).ConfigureAwait(false);
        await PushTargetsAsync(resource, resolved, context, resourceLogger, transferTimeout).ConfigureAwait(false);
    }

    private static async Task PushTargetsAsync(
        IResource resource,
        ModuleEffectiveImage resolved,
        PipelineStepContext context,
        ILogger resourceLogger,
        TimeSpan transferTimeout)
    {
        var targets = GetPushTargets(resource, resolved.Reference);
        if (targets.Count == 0)
        {
            return;
        }

        var runtime = await ResolveRuntimeAsync(context).ConfigureAwait(false);
        await PushTargetsAsync(
            resource,
            targets,
            (target, token) => TagAndPushAsync(
                runtime,
                resolved.Reference,
                target,
                "Image",
                resource,
                resourceLogger,
                transferTimeout,
                token),
            context.Logger,
            context.CancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> GetPushTargets(IResource resource, string localImage)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var localTag = ModuleImageReference.GetTag(localImage);
        return resource.Annotations
            .OfType<ModuleImagePushTargetAnnotation>()
            .Select(target => ModuleImageReference.WithDefaultTag(target.RemoteImageReference, localTag))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static async Task PushTargetsAsync(
        IResource resource,
        IReadOnlyList<string> targets,
        Func<string, CancellationToken, Task> pushTargetAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(pushTargetAsync);
        ArgumentNullException.ThrowIfNull(logger);
        var failures = new List<(string Target, Exception Exception)>();
        foreach (var target in targets)
        {
            LogPushTarget(logger, target, resource.Name, null);
            try
            {
                await pushTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add((target, exception));
            }
        }

        ThrowIfAnyPushTargetFailed(resource, targets.Count, failures);
    }

    private static void ThrowIfAnyPushTargetFailed(
        IResource resource,
        int targetCount,
        List<(string Target, Exception Exception)> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var details = failures.Select(failure => $"- {failure.Target}: {failure.Exception.Message}");
        throw new InvalidOperationException(
            $"Resource '{resource.Name}' failed to push {failures.Count} of {targetCount} image push targets:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, details)}{Environment.NewLine}" +
            "Log in to each target registry with the selected container runtime and check each reference, " +
            "then rerun the push.",
            new AggregateException(failures.Select(failure => failure.Exception)));
    }

    private static async Task PreparePublisherAsync(
        IResource resource,
        PipelineStepContext context,
        ILogger resourceLogger,
        ModuleImagePublisherAnnotation publisher)
    {
        var preparedImage = await publisher.PrepareAsync(
            context.Services,
            NullLogger.Instance,
            resourceLogger,
            context.CancellationToken).ConfigureAwait(false);
        if (preparedImage.SourceState.IsDirty)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot push an image built from a dirty repository. " +
                "Commit or stash the source changes before publishing the image.");
        }
    }

    private static async Task<ModuleEffectiveImage> ResolvePushTargetAsync(
        IResource resource,
        CancellationToken cancellationToken)
    {
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            cancellationToken,
            usePreparedPublisherImage: true).ConfigureAwait(false);
        if (resolved.PushTargetKind == ModuleImagePushTargetKind.None)
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote image push target.");
        }

        return resolved;
    }

    private static async Task<IContainerRuntime?> ResolveRuntimeIfNeededAsync(
        ModuleEffectiveImage resolved,
        PipelineStepContext context)
    {
        if (resolved.PushTargetKind != ModuleImagePushTargetKind.ContainerRuntime)
        {
            return null;
        }

        return await ResolveRuntimeAsync(context).ConfigureAwait(false);
    }

    private static Task<IContainerRuntime> ResolveRuntimeAsync(PipelineStepContext context) =>
        context.Services
            .GetRequiredService<IContainerRuntimeResolver>()
            .ResolveAsync(context.CancellationToken);

    private static Task PushWithRuntimeAsync(
        IContainerRuntime? runtime,
        string reference,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return RunContainerRuntimeAsync(
            ModuleImageRecipeOperations.GetContainerRuntimeExecutableName(runtime.Name),
            CreatePushArguments(runtime.Name, reference),
            resourceLogger,
            cancellationToken);
    }

    private static async Task PushBranchAliasIfAvailableAsync(
        IResource resource,
        ModuleEffectiveImage resolved,
        IContainerRuntime? runtime,
        PipelineStepContext context,
        ILogger resourceLogger,
        TimeSpan transferTimeout)
    {

        var branchAlias = GetBranchAliasReference(resource, resolved);
        if (branchAlias is null)
        {
            return;
        }

        runtime = await ResolveBranchAliasRuntimeAsync(runtime, context).ConfigureAwait(false);
        LogBranchAlias(context.Logger, branchAlias, resource.Name, null);
        await TagAndPushAsync(
            runtime,
            resolved.Reference,
            branchAlias,
            "Branch image",
            resource,
            resourceLogger,
            transferTimeout,
            context.CancellationToken).ConfigureAwait(false);
    }

    private static Task<IContainerRuntime> ResolveBranchAliasRuntimeAsync(
        IContainerRuntime? runtime,
        PipelineStepContext context)
    {
        Func<Task<IContainerRuntime>>[] resolvers =
        [
            () => ResolveRuntimeAsync(context),
            () => Task.FromResult(runtime!)
        ];
        return resolvers[Convert.ToInt32(runtime is not null)]();
    }

    private static async Task TagAndPushAsync(
        IContainerRuntime runtime,
        string source,
        string target,
        string imageKind,
        IResource resource,
        ILogger resourceLogger,
        TimeSpan transferTimeout,
        CancellationToken cancellationToken)
    {
        await ModuleOperationTimeout.RunAsync(
            token => runtime.TagImageAsync(source, target, token),
            transferTimeout,
            $"{imageKind} tag for resource '{resource.Name}'",
            cancellationToken).ConfigureAwait(false);
        await ModuleOperationTimeout.RunAsync(
            token => PushWithRuntimeAsync(runtime, target, resourceLogger, token),
            transferTimeout,
            $"{imageKind} push for resource '{resource.Name}'",
            cancellationToken).ConfigureAwait(false);
    }

    internal static Task PushImageAsync(
        ModuleImagePushTargetKind targetKind,
        IResource resource,
        string pushReference,
        Func<string, CancellationToken, Task> pushWithRuntimeAsync,
        Func<IResource, CancellationToken, Task> pushWithImageManagerAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(pushReference);
        ArgumentNullException.ThrowIfNull(pushWithRuntimeAsync);
        ArgumentNullException.ThrowIfNull(pushWithImageManagerAsync);
        Func<CancellationToken, Task> pushAsync = targetKind switch
        {
            ModuleImagePushTargetKind.ContainerRuntime =>
                token => pushWithRuntimeAsync(pushReference, token),
            ModuleImagePushTargetKind.AspireRegistry =>
                token => pushWithImageManagerAsync(resource, token),
            _ => throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote image push target.")
        };
        return ModuleOperationTimeout.RunAsync(
            pushAsync,
            timeout,
            $"Image push for resource '{resource.Name}'",
            cancellationToken);
    }

    internal static string? GetBranchAliasReference(IResource resource, ModuleEffectiveImage resolved)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(resolved);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        if (publisher is null)
        {
            return null;
        }

        return GetPreparedBranchAlias(publisher, resolved);
    }

    private static string? GetPreparedBranchAlias(
        ModuleImagePublisherAnnotation publisher,
        ModuleEffectiveImage resolved)
    {
        if (!publisher.TryGetPreparedImage(out var preparedImage))
        {
            return null;
        }

        return GetAvailableBranchAlias(publisher, preparedImage, resolved);
    }

    private static string? GetAvailableBranchAlias(
        ModuleImagePublisherAnnotation publisher,
        ModulePreparedImage preparedImage,
        ModuleEffectiveImage resolved)
    {
        if (!CanCreateBranchAlias(preparedImage.SourceState, resolved.PushImage))
        {
            return null;
        }

        var branch = GetBranchName(preparedImage.SourceState, publisher.Recipe.DetachedBranchAlias);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var branchImageTag = ModuleImageTag.FromBranch(branch);
        var pushImage = resolved.PushImage!;
        var alias = $"{pushImage.Registry}/{pushImage.Repository}:{branchImageTag}";
        return RemoveDuplicateAlias(alias, resolved.PushReference);
    }

    private static bool CanCreateBranchAlias(
        ModuleImageSourceState sourceState,
        ModuleRemoteImage? pushImage)
    {
        if (!sourceState.IsAvailable || sourceState.IsDirty)
        {
            return false;
        }

        return pushImage is not null;
    }

    private static string? GetBranchName(
        ModuleImageSourceState sourceState,
        string? detachedBranchAlias) =>
        sourceState.Branch ?? detachedBranchAlias;

    private static string? RemoveDuplicateAlias(string alias, string? pushReference) =>
        string.Equals(alias, pushReference, StringComparison.OrdinalIgnoreCase) ? null : alias;

    internal static IReadOnlyList<string> CreatePushArguments(
        string runtimeName,
        string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        var (registry, _) = ModuleImageReference.ParseRepository(imageReference);
        if (RequiresInsecureLoopbackPush(runtimeName, registry))
        {
            return ["push", "--tls-verify=false", imageReference];
        }

        return ["push", imageReference];
    }

    private static bool RequiresInsecureLoopbackPush(string runtimeName, string? registry)
    {
        if (!string.Equals(runtimeName, "Podman", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsPresentLoopbackRegistry(registry);
    }

    private static bool IsPresentLoopbackRegistry(string? registry)
    {
        if (registry is null)
        {
            return false;
        }

        return IsLoopbackRegistry(registry);
    }

    private static bool IsLoopbackRegistry(string registry)
    {
        if (!Uri.TryCreate($"http://{registry}", UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsLoopbackHost(uri.Host);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsLoopbackAddress(host);
    }

    private static bool IsLoopbackAddress(string host) =>
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static async Task RunContainerRuntimeAsync(
        string runtime,
        IReadOnlyList<string> arguments,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var lastError = string.Empty;
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
            {
                LogContainerRuntimeOutput(resourceLogger, runtime, line, null);
                lastError = string.IsNullOrWhiteSpace(line) ? lastError : line.Trim();
            }))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container runtime command '{runtime} {string.Join(' ', arguments)}' failed with exit code {result.ExitCode}. {lastError}"
                    .TrimEnd());
        }
    }
}
