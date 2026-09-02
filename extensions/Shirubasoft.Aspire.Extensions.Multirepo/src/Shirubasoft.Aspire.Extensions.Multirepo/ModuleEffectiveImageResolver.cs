#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed record ModuleNativeImagePublisherAnnotation(
    ModuleResourceKind ResourceKind) : IResourceAnnotation;

internal sealed class ModuleImagePublisherAnnotation(
    ModuleResourceKind resourceKind,
    ModuleImageBuildRecipe recipe,
    Func<
        ModuleImageBuildRecipe,
        ILogger,
        ILogger,
        CancellationToken,
        Task<ModulePreparedImage>>? prepareAsync = null,
    Func<ModuleImageBuildRecipe, CancellationToken, Task<ModuleImageSourceState?>>? inspectAsync = null) : IResourceAnnotation
{
    private readonly object _preparationLock = new();
    private readonly Func<
        ModuleImageBuildRecipe,
        ILogger,
        ILogger,
        CancellationToken,
        Task<ModulePreparedImage>>? _prepareAsync = prepareAsync;
    private readonly Func<ModuleImageBuildRecipe, CancellationToken, Task<ModuleImageSourceState?>> _inspectAsync =
        inspectAsync ?? ModuleImageRecipeOperations.Instance.TryCaptureSourceStateAsync;
    private Task<ModulePreparedImage>? _preparationTask;
    private ModulePreparedImage? _preparedImage;

    public string ModuleName => Recipe.ModuleName;

    public string ResourceName => Recipe.ResourceName;

    public ModuleResourceKind ResourceKind { get; } = resourceKind;

    public ModuleImageBuildRecipe Recipe { get; } = recipe;

    public ModuleImageCommandOptions Options => Recipe.Options;

    public string WorkingDirectory => Recipe.WorkingDirectory;

    public string? Repository => Recipe.Repository;

    public string? Revision => Recipe.Revision;

    public Task<ModulePreparedImage> PrepareAsync(
        IServiceProvider services,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(lifecycleLogger);
        ArgumentNullException.ThrowIfNull(resourceLogger);

        cancellationToken.ThrowIfCancellationRequested();
        var operationToken = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ??
            CancellationToken.None;
        return PrepareAndWaitAsync(
            services,
            lifecycleLogger,
            resourceLogger,
            operationToken,
            cancellationToken);
    }

    private Task<ModulePreparedImage> PrepareAndWaitAsync(
        IServiceProvider services,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        CancellationToken operationToken,
        CancellationToken callerToken)
    {
        Task<ModulePreparedImage> sharedTask;
        lock (_preparationLock)
        {
            if (_preparationTask is null)
            {
                var preparationTask = PrepareCoreAsync(
                    services,
                    lifecycleLogger,
                    resourceLogger,
                    operationToken);
                _preparationTask = preparationTask;
                sharedTask = preparationTask;
                _ = preparationTask.ContinueWith(
                    completed => ClearFailedPreparation(completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                sharedTask = _preparationTask;
            }
        }

        return sharedTask.WaitAsync(callerToken);
    }

    internal Task<ModulePreparedImage> PrepareAsync(
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleLogger);
        ArgumentNullException.ThrowIfNull(resourceLogger);
        if (_prepareAsync is null)
        {
            throw new InvalidOperationException(
                "Aspire application services are required to prepare a module image with the default runtime operations.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return PrepareAndWaitAsync(
            EmptyServiceProvider.Instance,
            lifecycleLogger,
            resourceLogger,
            CancellationToken.None,
            cancellationToken);
    }

    public bool TryGetPreparedImage(out ModulePreparedImage preparedImage)
    {
        lock (_preparationLock)
        {
            if (_preparedImage is not null)
            {
                preparedImage = _preparedImage;
                return true;
            }

            preparedImage = null!;
            return false;
        }
    }

    public Task<ModuleImageSourceState?> InspectSourceAsync(CancellationToken cancellationToken) =>
        _inspectAsync(Recipe, cancellationToken);

    private async Task<ModulePreparedImage> PrepareCoreAsync(
        IServiceProvider services,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        var preparedImage = _prepareAsync is not null
            ? await _prepareAsync(
                Recipe,
                lifecycleLogger,
                resourceLogger,
                cancellationToken).ConfigureAwait(false)
            : await ModuleImageRecipeEvaluator.PrepareAsync(
                Recipe,
                lifecycleLogger,
                resourceLogger,
                new ModuleImageRecipeOperations(
                    services.GetRequiredService<IContainerRuntimeResolver>(),
                    services.GetRequiredService<ModuleRepositoryRefreshCoordinator>()),
                cancellationToken).ConfigureAwait(false);
        lock (_preparationLock)
        {
            _preparedImage = preparedImage;
        }

        return preparedImage;
    }

    private void ClearFailedPreparation(Task<ModulePreparedImage> completed)
    {
        if (completed.IsCompletedSuccessfully)
        {
            return;
        }

        lock (_preparationLock)
        {
            if (ReferenceEquals(_preparationTask, completed))
            {
                _preparationTask = null;
            }
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}

internal sealed record ModuleEffectiveImage(
    string Reference,
    string PullReference,
    string? PushReference,
    ModuleImagePushTargetKind PushTargetKind,
    string? Registry,
    string Repository,
    string? Tag,
    string? Digest,
    ModuleRemoteImage? PushImage);

internal sealed record ModuleRemoteImage(
    string Registry,
    string Repository,
    string Tag,
    string Reference);

internal enum ModuleImagePushTargetKind
{
    None,
    ContainerRuntime,
    AspireRegistry
}

internal static class ModuleEffectiveImageResolver
{
    public static bool HasPullSource(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (image is null)
        {
            return false;
        }

        return HasPullSource(resource, image);
    }

    private static bool HasPullSource(IResource resource, ContainerImageAnnotation image)
    {
        if (GetPullMapping(resource) is not null)
        {
            EnsureMappingIsTaggable(resource, image);
            return true;
        }

        return HasUnmappedPullSource(resource, image);
    }

    private static bool HasUnmappedPullSource(IResource resource, ContainerImageAnnotation image)
    {
        var explicitRegistry = GetResourceRegistry(resource);
        var mayHaveExplicitRegistry = MayHaveExplicitRegistry(explicitRegistry);
        if (image.SHA256 is { Length: > 0 })
        {
            return HasPinnedPullSource(image, mayHaveExplicitRegistry);
        }

        return HasTaggedPullSource(resource, image, mayHaveExplicitRegistry);
    }

    private static bool MayHaveExplicitRegistry(IContainerRegistry? registry) =>
        registry is not null && MayHaveRemoteEndpoint(registry);

    private static bool HasPinnedPullSource(
        ContainerImageAnnotation image,
        bool mayHaveExplicitRegistry) =>
        image.Registry is { Length: > 0 } && !mayHaveExplicitRegistry;

    private static bool HasTaggedPullSource(
        IResource resource,
        ContainerImageAnnotation image,
        bool mayHaveExplicitRegistry)
    {
        if (image.Registry is { Length: > 0 })
        {
            return true;
        }

        return HasImplicitPullSource(resource, mayHaveExplicitRegistry);
    }

    private static bool HasImplicitPullSource(IResource resource, bool mayHaveExplicitRegistry) =>
        mayHaveExplicitRegistry || HasFallbackRegistry(resource);

    public static bool HasPushTarget(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (image is null)
        {
            return false;
        }

        return HasPushTarget(resource, image);
    }

    private static bool HasPushTarget(IResource resource, ContainerImageAnnotation image)
    {
        if (image.SHA256 is { Length: > 0 })
        {
            return false;
        }

        return HasPushRegistry(resource, image);
    }

    private static bool HasPushRegistry(IResource resource, ContainerImageAnnotation image)
    {
        if (image.Registry is { Length: > 0 })
        {
            return true;
        }

        var registry = GetResourceRegistry(resource);
        return HasConfiguredPushRegistry(resource, registry);
    }

    private static bool HasConfiguredPushRegistry(IResource resource, IContainerRegistry? registry)
    {
        if (registry is null)
        {
            return HasFallbackRegistry(resource);
        }

        return MayHaveRemoteEndpoint(registry) || HasFallbackRegistry(resource);
    }

    public static async Task<ModuleEffectiveImage> ResolveAsync(
        IResource resource,
        CancellationToken cancellationToken,
        bool allowUnqualifiedPullReference = false,
        bool usePreparedPublisherImage = false,
        ModuleImageExecutionPlan? imagePlan = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var localImage = GetRequiredContainerImageName(resource);
        var image = GetRequiredImageAnnotation(resource);
        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        var preparedImage = GetPreparedImage(resource, publisher, usePreparedPublisherImage);
        localImage = SelectLocalImage(localImage, preparedImage, imagePlan);
        var mapping = GetPullMapping(resource);
        ValidatePullMapping(resource, image, mapping);

        var explicitRegistry = await GetRemoteRegistryAsync(
            GetResourceRegistry(resource),
            cancellationToken).ConfigureAwait(false);
        var moduleDeclaresRegistry = image.Registry is { Length: > 0 };
        var registry = await ResolveRegistryAsync(
            resource,
            explicitRegistry,
            moduleDeclaresRegistry,
            cancellationToken).ConfigureAwait(false);
        var push = await ResolvePushAsync(
            resource,
            image,
            registry,
            moduleDeclaresRegistry,
            publisher,
            preparedImage,
            imagePlan,
            localImage,
            cancellationToken).ConfigureAwait(false);
        var pullImage = ResolvePullImage(
            resource,
            image,
            mapping,
            push.Image,
            explicitRegistry,
            allowUnqualifiedPullReference,
            localImage);

        var parsed = ParseReference(localImage, image);
        return new ModuleEffectiveImage(
            localImage,
            pullImage,
            push.Image?.Reference,
            push.TargetKind,
            parsed.Registry,
            parsed.Repository,
            parsed.Tag,
            parsed.Digest,
            push.Image);
    }

    private static string GetRequiredContainerImageName(IResource resource)
    {
        if (resource.TryGetContainerImageName(out var localImage))
        {
            return localImage;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have a container image reference.");
    }

    private static ContainerImageAnnotation GetRequiredImageAnnotation(IResource resource) =>
        resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault()
        ?? throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have a container image annotation.");

    private static ModulePreparedImage? GetPreparedImage(
        IResource resource,
        ModuleImagePublisherAnnotation? publisher,
        bool usePreparedPublisherImage)
    {
        if (!usePreparedPublisherImage || publisher is null)
        {
            return null;
        }

        return GetRequiredPreparedImage(resource, publisher);
    }

    private static ModulePreparedImage GetRequiredPreparedImage(
        IResource resource,
        ModuleImagePublisherAnnotation publisher)
    {
        if (publisher.TryGetPreparedImage(out var preparedImage))
        {
            return preparedImage;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' has not prepared its module image. Run its image preparation step first.");
    }

    private static string SelectLocalImage(
        string localImage,
        ModulePreparedImage? preparedImage,
        ModuleImageExecutionPlan? imagePlan)
    {
        if (preparedImage is not null)
        {
            return preparedImage.CanonicalImageReference;
        }

        return SelectPlannedLocalImage(localImage, imagePlan);
    }

    private static string SelectPlannedLocalImage(
        string localImage,
        ModuleImageExecutionPlan? imagePlan)
    {
        if (imagePlan is not null)
        {
            return imagePlan.CanonicalImageReference;
        }

        return localImage;
    }

    private static void ValidatePullMapping(
        IResource resource,
        ContainerImageAnnotation image,
        ModuleImagePullMappingAnnotation? mapping)
    {
        if (mapping is not null)
        {
            EnsureMappingIsTaggable(resource, image);
        }
    }

    private static async Task<IContainerRegistry?> ResolveRegistryAsync(
        IResource resource,
        IContainerRegistry? explicitRegistry,
        bool moduleDeclaresRegistry,
        CancellationToken cancellationToken)
    {
        if (explicitRegistry is not null || moduleDeclaresRegistry)
        {
            return explicitRegistry;
        }

        return await GetFallbackRegistryAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResolvedPush> ResolvePushAsync(
        IResource resource,
        ContainerImageAnnotation image,
        IContainerRegistry? registry,
        bool moduleDeclaresRegistry,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        ModuleImageExecutionPlan? imagePlan,
        string localImage,
        CancellationToken cancellationToken) =>
        await TryResolveAspireRegistryPushAsync(
            resource,
            image,
            registry,
            publisher,
            preparedImage,
            imagePlan,
            localImage,
            cancellationToken).ConfigureAwait(false)
        ?? ResolveContainerRuntimePush(
            image,
            moduleDeclaresRegistry,
            publisher,
            preparedImage,
            localImage);

    private static async Task<ResolvedPush?> TryResolveAspireRegistryPushAsync(
        IResource resource,
        ContainerImageAnnotation image,
        IContainerRegistry? registry,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        ModuleImageExecutionPlan? imagePlan,
        string localImage,
        CancellationToken cancellationToken)
    {
        if (!CanPushToAspireRegistry(registry, image))
        {
            return null;
        }

        var pushedImage = await ResolveRegistryImageAsync(
            resource,
            image,
            registry!,
            publisher,
            preparedImage,
            imagePlan,
            localImage,
            cancellationToken).ConfigureAwait(false);
        return new ResolvedPush(pushedImage, ModuleImagePushTargetKind.AspireRegistry);
    }

    private static bool CanPushToAspireRegistry(
        IContainerRegistry? registry,
        ContainerImageAnnotation image) =>
        registry is not null && image.SHA256 is not { Length: > 0 };

    private static ResolvedPush ResolveContainerRuntimePush(
        ContainerImageAnnotation image,
        bool moduleDeclaresRegistry,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        string localImage)
    {
        if (!CanPushToContainerRuntime(moduleDeclaresRegistry, image))
        {
            return ResolvedPush.None;
        }

        var remoteImage = ResolveContainerRuntimeReference(localImage, publisher, preparedImage);
        var remoteIdentity = ParseReference(remoteImage, image);
        return new ResolvedPush(
            new ModuleRemoteImage(
                remoteIdentity.Registry!,
                remoteIdentity.Repository,
                remoteIdentity.Tag!,
                remoteImage),
            ModuleImagePushTargetKind.ContainerRuntime);
    }

    private static bool CanPushToContainerRuntime(
        bool moduleDeclaresRegistry,
        ContainerImageAnnotation image) =>
        moduleDeclaresRegistry && image.SHA256 is not { Length: > 0 };

    private static string ResolveContainerRuntimeReference(
        string localImage,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage)
    {
        if (publisher is null)
        {
            return localImage;
        }

        return ResolvePublisherReference(publisher, preparedImage);
    }

    private static string ResolvePublisherReference(
        ModuleImagePublisherAnnotation publisher,
        ModulePreparedImage? preparedImage)
    {
        if (preparedImage is not null)
        {
            return preparedImage.CanonicalImageReference;
        }

        return $"{ModuleImageReference.GetRepository(publisher.Options)}:{GetImageTag(publisher.Options.ImageTag)}";
    }

    private static string GetImageTag(string? tag) => tag ?? "latest";

    private static string ResolvePullImage(
        IResource resource,
        ContainerImageAnnotation image,
        ModuleImagePullMappingAnnotation? mapping,
        ModuleRemoteImage? pushedImage,
        IContainerRegistry? explicitRegistry,
        bool allowUnqualifiedPullReference,
        string localImage)
    {
        var publishedReference = TryGetPublishedPullReference(mapping, pushedImage);
        return publishedReference ?? ResolveUnpublishedPullImage(
            resource,
            image,
            explicitRegistry,
            allowUnqualifiedPullReference,
            localImage);
    }

    private static string? TryGetPublishedPullReference(
        ModuleImagePullMappingAnnotation? mapping,
        ModuleRemoteImage? pushedImage)
    {
        if (mapping is not null)
        {
            return mapping.RemoteImageReference;
        }

        return pushedImage?.Reference;
    }

    private static string ResolveUnpublishedPullImage(
        IResource resource,
        ContainerImageAnnotation image,
        IContainerRegistry? explicitRegistry,
        bool allowUnqualifiedPullReference,
        string localImage)
    {
        var declaredReference = TryGetDeclaredPullReference(image, explicitRegistry, localImage);
        return declaredReference ?? ResolveUnqualifiedPullImage(
            resource,
            allowUnqualifiedPullReference,
            localImage);
    }

    private static string? TryGetDeclaredPullReference(
        ContainerImageAnnotation image,
        IContainerRegistry? explicitRegistry,
        string localImage)
    {
        if (CanUseDeclaredPullReference(image, explicitRegistry))
        {
            return localImage;
        }

        return null;
    }

    private static bool CanUseDeclaredPullReference(
        ContainerImageAnnotation image,
        IContainerRegistry? explicitRegistry) =>
        explicitRegistry is null && image.Registry is { Length: > 0 };

    private static string ResolveUnqualifiedPullImage(
        IResource resource,
        bool allowUnqualifiedPullReference,
        string localImage)
    {
        if (allowUnqualifiedPullReference)
        {
            return localImage;
        }

        throw new InvalidOperationException(
            $"Resource '{resource.Name}' does not have a container registry to pull from.");
    }

    private static async Task<ModuleRemoteImage> ResolveRegistryImageAsync(
        IResource resource,
        ContainerImageAnnotation image,
        IContainerRegistry registry,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        ModuleImageExecutionPlan? imagePlan,
        string localImageReference,
        CancellationToken cancellationToken)
    {
        var effectiveIdentity = ParseReference(
            preparedImage?.CanonicalImageReference ?? localImageReference,
            image);
        var options = CreatePushOptions(resource, publisher, preparedImage, imagePlan, effectiveIdentity);
        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = resource,
            Options = options,
            CancellationToken = cancellationToken
        };
        await ApplyPushOptionsCallbacksAsync(resource, context).ConfigureAwait(false);

        var reference = await options.GetFullRemoteImageNameAsync(registry, cancellationToken).ConfigureAwait(false);
        var remoteName = options.RemoteImageName!;
        var parsedName = ModuleImageReference.ParseRepository(remoteName);
        var registryHost = await ResolveRegistryHostAsync(
            resource,
            registry,
            parsedName.Registry,
            cancellationToken).ConfigureAwait(false);
        var registryRepository = await ResolveRegistryRepositoryAsync(
            registry,
            parsedName.Registry,
            cancellationToken).ConfigureAwait(false);
        var repository = CombineRepository(registryRepository, parsedName.Name);
        return new ModuleRemoteImage(
            registryHost,
            repository,
            GetImageTag(options.RemoteImageTag),
            reference);
    }

    private static ContainerImagePushOptions CreatePushOptions(
        IResource resource,
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        ModuleImageExecutionPlan? imagePlan,
        (string? Registry, string Repository, string? Tag, string? Digest) effectiveIdentity) =>
        new()
        {
            RemoteImageName = GetRemoteImageName(resource, publisher),
            RemoteImageTag = ResolveRemoteImageTag(publisher, preparedImage, imagePlan, effectiveIdentity.Tag)
        };

    private static string GetRemoteImageName(
        IResource resource,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Options.ImageName;
        }

#pragma warning disable CA1308
        return resource.Name.ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static string ResolveRemoteImageTag(
        ModuleImagePublisherAnnotation? publisher,
        ModulePreparedImage? preparedImage,
        ModuleImageExecutionPlan? imagePlan,
        string? effectiveTag)
    {
        if (preparedImage is not null || imagePlan is not null)
        {
            return GetImageTag(effectiveTag);
        }

        return GetPublisherImageTag(publisher);
    }

    private static string GetPublisherImageTag(ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is null)
        {
            return "latest";
        }

        return GetImageTag(publisher.Options.ImageTag);
    }

    private static async Task ApplyPushOptionsCallbacksAsync(
        IResource resource,
        ContainerImagePushOptionsCallbackContext context)
    {
        foreach (var annotation in resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>())
        {
            await annotation.Callback(context).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveRegistryHostAsync(
        IResource resource,
        IContainerRegistry registry,
        string? declaredRegistry,
        CancellationToken cancellationToken)
    {
        var registryHost = declaredRegistry ??
            await registry.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(registryHost))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not have a remote container registry endpoint.");
        }

        return registryHost;
    }

    private static async Task<string?> ResolveRegistryRepositoryAsync(
        IContainerRegistry registry,
        string? declaredRegistry,
        CancellationToken cancellationToken)
    {
        if (declaredRegistry is not null || registry.Repository is null)
        {
            return null;
        }

        return await registry.Repository.GetValueAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CombineRepository(string? registryRepository, string imageName)
    {
        if (string.IsNullOrWhiteSpace(registryRepository))
        {
            return imageName;
        }

        return $"{registryRepository}/{imageName}";
    }

    private static (string? Registry, string Repository, string? Tag, string? Digest) ParseReference(
        string reference,
        ContainerImageAnnotation image)
    {
        var digest = GetDigest(image);
        var withoutDigest = reference.Split('@', 2)[0];
        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');
        var repositoryWithRegistry = GetRepositoryWithRegistry(withoutDigest, lastSlash, lastColon);
        var tag = GetTag(image, digest, withoutDigest, lastSlash, lastColon);
        var parsed = ModuleImageReference.ParseRepository(repositoryWithRegistry);
        return (parsed.Registry, parsed.Name, tag, digest);
    }

    private static string? GetDigest(ContainerImageAnnotation image)
    {
        if (image.SHA256 is { Length: > 0 })
        {
            return $"sha256:{image.SHA256}";
        }

        return null;
    }

    private static string GetRepositoryWithRegistry(string reference, int lastSlash, int lastColon)
    {
        if (lastColon > lastSlash)
        {
            return reference[..lastColon];
        }

        return reference;
    }

    private static string? GetTag(
        ContainerImageAnnotation image,
        string? digest,
        string reference,
        int lastSlash,
        int lastColon)
    {
        if (digest is null && lastColon > lastSlash)
        {
            return reference[(lastColon + 1)..];
        }

        return image.Tag;
    }

    private static void EnsureMappingIsTaggable(IResource resource, ContainerImageAnnotation image)
    {
        if (image.SHA256 is { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot map a pulled image to its digest-pinned local reference. " +
                "Configure a tagged local image when using WithImagePullMapping.");
        }
    }

    private static ModuleImagePullMappingAnnotation? GetPullMapping(IResource resource) =>
        resource.Annotations.OfType<ModuleImagePullMappingAnnotation>().LastOrDefault();

    private static IContainerRegistry? GetResourceRegistry(IResource resource) =>
        resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().LastOrDefault()?.Registry;

    private static IEnumerable<IContainerRegistry> GetFallbackRegistries(IResource resource) =>
        GetDeploymentRegistries(resource).Concat(GetRegistryTargets(resource));

    private static IEnumerable<IContainerRegistry> GetDeploymentRegistries(IResource resource) =>
        resource.Annotations
            .OfType<DeploymentTargetAnnotation>()
            .Reverse()
            .Where(annotation => annotation.ContainerRegistry is not null)
            .Select(annotation => annotation.ContainerRegistry!);

    private static IEnumerable<IContainerRegistry> GetRegistryTargets(IResource resource) =>
        resource.Annotations
            .OfType<RegistryTargetAnnotation>()
            .Select(annotation => annotation.Registry);

    private static async Task<IContainerRegistry?> GetFallbackRegistryAsync(
        IResource resource,
        CancellationToken cancellationToken)
    {
        var registries = new List<IContainerRegistry>();
        await CollectRemoteRegistriesAsync(resource, registries, cancellationToken).ConfigureAwait(false);
        return SelectFallbackRegistry(resource, registries);
    }

    private static async Task CollectRemoteRegistriesAsync(
        IResource resource,
        List<IContainerRegistry> registries,
        CancellationToken cancellationToken)
    {
        foreach (var registry in GetFallbackRegistries(resource))
        {
            await TryAddRemoteRegistryAsync(registries, registry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryAddRemoteRegistryAsync(
        List<IContainerRegistry> registries,
        IContainerRegistry registry,
        CancellationToken cancellationToken)
    {
        if (await GetRemoteRegistryAsync(registry, cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        if (!registries.Contains(registry))
        {
            registries.Add(registry);
        }
    }

    private static IContainerRegistry? SelectFallbackRegistry(
        IResource resource,
        List<IContainerRegistry> registries) =>
        registries.Count switch
        {
            0 => null,
            1 => registries[0],
            _ => throw new InvalidOperationException(
                $"Resource '{resource.Name}' has multiple container registries available. " +
                "Specify one with WithContainerRegistry.")
        };

    private static bool HasFallbackRegistry(IResource resource)
    {
        var registries = GetFallbackRegistries(resource)
            .Where(MayHaveRemoteEndpoint)
            .Distinct()
            .ToArray();
        return registries.Length switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException(
                $"Resource '{resource.Name}' has multiple container registries available. " +
                "Specify one with WithContainerRegistry.")
        };
    }

    private static async Task<IContainerRegistry?> GetRemoteRegistryAsync(
        IContainerRegistry? registry,
        CancellationToken cancellationToken)
    {
        if (registry is null)
        {
            return null;
        }

        var endpoint = await registry.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(endpoint) ? null : registry;
    }

    private static bool MayHaveRemoteEndpoint(IContainerRegistry registry) =>
        registry.Endpoint.IsConditional ||
        registry.Endpoint.ValueProviders.Count > 0 ||
        !string.IsNullOrWhiteSpace(registry.Endpoint.Format);

    private sealed class ResolvedPush(ModuleRemoteImage? image, ModuleImagePushTargetKind targetKind)
    {
        public static ResolvedPush None { get; } = new(null, ModuleImagePushTargetKind.None);

        public ModuleRemoteImage? Image { get; } = image;

        public ModuleImagePushTargetKind TargetKind { get; } = targetKind;
    }
}
