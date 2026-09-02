#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class ModuleImageDescriptionPipeline
{
    internal const string StepName = "describe-images";
    internal const string FileName = "module-images.json";

    private static readonly Action<ILogger, string, string, string, Exception?> LogImage =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogImage)),
            "Module image {Resource}: {Reference} (push: {PushReference}).");

    private static readonly Action<ILogger, string, Exception?> LogOutput =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(LogOutput)),
            "Wrote module image descriptions to {Path}.");

    private sealed class ImageCandidate(
        IResource resource,
        DistributedApplicationModuleResourceAnnotation module,
        ModuleImagePublisherAnnotation? publisher,
        ModuleNativeImagePublisherAnnotation? nativePublisher)
    {
        public IResource Resource { get; } = resource;

        public DistributedApplicationModuleResourceAnnotation Module { get; } = module;

        public ModuleImagePublisherAnnotation? Publisher { get; } = publisher;

        public ModuleNativeImagePublisherAnnotation? NativePublisher { get; } = nativePublisher;
    }

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = StepName,
            Description = "Writes effective module image identities and build origins.",
            Action = context => DescribeAsync(context, ModuleImageSelection.All)
        });
    }

    internal static async Task<ModuleImageDescriptionDocument> CreateDocumentAsync(
        IEnumerable<IResource> resources,
        ModuleImageSelection selection,
        CancellationToken cancellationToken,
        IEnumerable<IDistributedApplicationModule>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(selection);
        var materializedResources = resources.ToArray();
        var images = GetImageCandidates(materializedResources);
        var selectedResources = selection.ResolveResources(
            images.Select(item => item.Resource),
            "described module images");
        var document = new ModuleImageDescriptionDocument();
        AddModuleDescriptions(document, materializedResources, modules);
        await AddImageDescriptionsAsync(
            document,
            images,
            selectedResources,
            cancellationToken).ConfigureAwait(false);
        return document;
    }

    private static void AddModuleDescriptions(
        ModuleImageDescriptionDocument document,
        IReadOnlyList<IResource> resources,
        IEnumerable<IDistributedApplicationModule>? modules)
    {
        var moduleAnnotations = resources
            .Select(resource => resource.Annotations
                .OfType<DistributedApplicationModuleResourceAnnotation>()
                .LastOrDefault())
            .OfType<DistributedApplicationModuleResourceAnnotation>();
        var moduleIdentities = (modules ?? [])
            .Select(module => (module.Name, module.PackageId))
            .Concat(moduleAnnotations.Select(module => (module.ModuleName, module.PackageId)))
            .GroupBy(module => module.Item1, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var moduleGroup in moduleIdentities)
        {
            AddModuleDescription(document, moduleGroup);
        }
    }

    private static void AddModuleDescription(
        ModuleImageDescriptionDocument document,
        IGrouping<string, (string, string?)> moduleGroup)
    {
        var packageIds = moduleGroup
            .Select(module => module.Item2)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packageIds.Length > 1)
        {
            throw new InvalidDataException(
                $"Module '{moduleGroup.Key}' has conflicting contract package identities in the AppHost model.");
        }

        document.Modules.Add(new ModuleImageModuleDescription
        {
            Name = moduleGroup.Key,
            ContractPackageId = packageIds.SingleOrDefault()
        });
    }

    private static ImageCandidate[] GetImageCandidates(IReadOnlyList<IResource> resources) =>
        resources
            .Select(resource => (
                Resource: resource,
                Module: resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>().LastOrDefault(),
                Publisher: resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault(),
                NativePublisher: resource.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().LastOrDefault()))
            .Where(item =>
                item.Module is not null &&
                item.Resource.Annotations.OfType<ContainerImageAnnotation>().Any())
            .Select(item => new ImageCandidate(
                item.Resource,
                item.Module!,
                item.Publisher,
                item.NativePublisher))
            .OrderBy(item => item.Resource.Name, StringComparer.Ordinal)
            .ToArray();

    private static async Task AddImageDescriptionsAsync(
        ModuleImageDescriptionDocument document,
        IEnumerable<ImageCandidate> images,
        IReadOnlySet<IResource> selectedResources,
        CancellationToken cancellationToken)
    {
        foreach (var item in images.Where(item => selectedResources.Contains(item.Resource)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = await CreateImageDescriptionAsync(item, cancellationToken).ConfigureAwait(false);
            document.Images.Add(description);
        }
    }

    private static async Task<ModuleImageDescription> CreateImageDescriptionAsync(
        ImageCandidate item,
        CancellationToken cancellationToken)
    {
        var executionPlan = await CreateExecutionPlanAsync(item.Publisher, cancellationToken).ConfigureAwait(false);
        var effective = await ModuleEffectiveImageResolver.ResolveAsync(
            item.Resource,
            cancellationToken,
            allowUnqualifiedPullReference: true,
            imagePlan: executionPlan).ConfigureAwait(false);
        var description = new ModuleImageDescription
        {
            Module = item.Module.ModuleName,
            Resource = item.Module.ResourceName,
            EffectiveResource = item.Resource.Name,
            ResourceKind = GetResourceKind(item.Publisher, item.NativePublisher),
            Registry = effective.Registry,
            Repository = effective.Repository,
            Tag = effective.Tag,
            Digest = effective.Digest,
            Reference = effective.Reference,
            PullReference = effective.PullReference,
            Push = CreatePushDescription(item.Publisher, item.NativePublisher, effective.PushImage),
            Build = CreateBuildDescription(item.Resource, item.Publisher)
        };
        AddPublishArguments(description, executionPlan);
        return description;
    }

    private static async Task<ModuleImageExecutionPlan?> CreateExecutionPlanAsync(
        ModuleImagePublisherAnnotation? publisher,
        CancellationToken cancellationToken)
    {
        if (publisher is null)
        {
            return null;
        }

        var sourceState = await GetSourceStateAsync(publisher, cancellationToken).ConfigureAwait(false);
        return ModuleImageExecutionPlan.Create(publisher.Recipe, RequireSourceState(publisher, sourceState));
    }

    private static async Task<ModuleImageSourceState?> GetSourceStateAsync(
        ModuleImagePublisherAnnotation publisher,
        CancellationToken cancellationToken)
    {
        if (publisher.TryGetPreparedImage(out var preparedImage))
        {
            return preparedImage.SourceState;
        }

        return await publisher.InspectSourceAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ModuleImageSourceState RequireSourceState(
        ModuleImagePublisherAnnotation publisher,
        ModuleImageSourceState? sourceState)
    {
        if (sourceState is not null)
        {
            return sourceState;
        }

        return GetUnavailableSourceState(publisher);
    }

    private static ModuleImageSourceState GetUnavailableSourceState(
        ModuleImagePublisherAnnotation publisher)
    {
        if (!publisher.Recipe.AllowsUnavailableSource)
        {
            throw ModuleImageRecipeEvaluator.CreateUnavailableSourceException(
                publisher.Recipe,
                "the build repository is missing");
        }

        return ModuleImageSourceState.Unavailable;
    }

    private static ModuleResourceKind GetResourceKind(
        ModuleImagePublisherAnnotation? publisher,
        ModuleNativeImagePublisherAnnotation? nativePublisher) =>
        publisher?.ResourceKind ?? nativePublisher?.ResourceKind ?? ModuleResourceKind.Container;

    private static ModuleImagePushDescription? CreatePushDescription(
        ModuleImagePublisherAnnotation? publisher,
        ModuleNativeImagePublisherAnnotation? nativePublisher,
        ModuleRemoteImage? pushImage)
    {
        if (pushImage is null)
        {
            return null;
        }

        return HasImagePublisher(publisher, nativePublisher)
            ? new ModuleImagePushDescription
            {
                Registry = pushImage.Registry,
                Repository = pushImage.Repository,
                Tag = pushImage.Tag
            }
            : null;
    }

    private static bool HasImagePublisher(
        ModuleImagePublisherAnnotation? publisher,
        ModuleNativeImagePublisherAnnotation? nativePublisher) =>
        publisher is not null || nativePublisher is not null;

    private static ModuleImageBuildDescription? CreateBuildDescription(
        IResource resource,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is null)
        {
            return null;
        }

        return new ModuleImageBuildDescription
        {
            Command = publisher.Options.PublishCommand,
            WorkingDirectory = publisher.WorkingDirectory,
            Repository = publisher.Repository,
            Revision = publisher.Revision,
            Step = $"build-{resource.Name}"
        };
    }

    private static void AddPublishArguments(
        ModuleImageDescription description,
        ModuleImageExecutionPlan? executionPlan)
    {
        if (executionPlan is null)
        {
            return;
        }

        AddPublishArguments(description, executionPlan.PublishArguments);
    }

    private static void AddPublishArguments(
        ModuleImageDescription description,
        IEnumerable<string> publishArguments)
    {
        foreach (var argument in publishArguments)
        {
            description.Build!.Arguments.Add(argument);
        }
    }

    private static async Task DescribeAsync(
        PipelineStepContext context,
        ModuleImageSelection selection)
    {
        var modules = GetMaterializedModules(context.Services);
        var document = await CreateDocumentAsync(
            context.Model.Resources,
            selection,
            context.CancellationToken,
            modules).ConfigureAwait(false);
        var output = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var path = Path.Combine(output, FileName);
        await document.SaveAsync(path, context.CancellationToken).ConfigureAwait(false);
        LogImages(context.Logger, document.Images);
        LogOutput(context.Logger, path, null);
    }

    private static IReadOnlyCollection<IDistributedApplicationModule> GetMaterializedModules(
        IServiceProvider services)
    {
        var catalog = services.GetService<IDistributedApplicationModuleCatalog>();
        return catalog is ModuleApplicationRegistry registry
            ? registry.GetMaterializedModules()
            : [];
    }

    private static void LogImages(ILogger logger, IEnumerable<ModuleImageDescription> images)
    {
        foreach (var image in images)
        {
            LogImage(
                logger,
                image.EffectiveResource,
                image.Reference,
                GetPushReference(image),
                null);
        }
    }

    private static string GetPushReference(ModuleImageDescription image) =>
        image.Push?.Reference ?? "none";

}
