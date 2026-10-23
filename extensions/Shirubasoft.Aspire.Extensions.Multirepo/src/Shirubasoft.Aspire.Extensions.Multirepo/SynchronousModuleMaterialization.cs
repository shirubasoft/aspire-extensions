#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

public static partial class DistributedApplicationModuleExtensions
{
    private static readonly Dictionary<bool, string> TrackedResourceDiagnostics =
        new Dictionary<bool, string>
        {
            [false] = string.Empty,
            [true] = " and is already tracked by the module registry"
        };

    private static readonly Action<ILogger, string, string, Exception?> LogImagePreparationFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1, nameof(LogImagePreparationFailed)),
            "Image preparation failed for module {ModuleName} resource {ResourceName}.");

    /// <summary>Adds a defined module using its local source checkout.</summary>
    public static IDistributedApplicationModule AddModule(
        this IDistributedApplicationBuilder builder,
        IDistributedApplicationModule module)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);
        var typedModule = GetTypedModule(module);
        ValidateDefinitionBuilder(builder, typedModule);
        var registry = GetOrCreateRegistry(builder);
        RegisterModuleDefinition(registry, typedModule);
        MaterializeModule(builder, typedModule, registry, imported: false);
        return module;
    }

    private static DistributedApplicationModule GetTypedModule(IDistributedApplicationModule module)
    {
        if (module is not DistributedApplicationModule typedModule)
        {
            throw new ArgumentException(
                "The module must have been created by DefineModule or ExportModule on this extension.",
                nameof(module));
        }

        return typedModule;
    }

    private static void ValidateDefinitionBuilder(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module)
    {
        if (!ReferenceEquals(module.DefinitionApplicationBuilder, builder))
        {
            throw new ArgumentException(
                "The module definition belongs to a different distributed application builder. " +
                "Define and materialize the module on the same AppHost builder.",
                nameof(module));
        }
    }

    private static void RegisterModuleDefinition(
        ModuleApplicationRegistry registry,
        DistributedApplicationModule module)
    {
        if (!registry.TryGetDefinition(module.Name, out _))
        {
            registry.AddModule(module);
        }
    }

    /// <summary>Imports a defined module by name.</summary>
    public static IDistributedApplicationModule ImportModule(
        this IDistributedApplicationBuilder builder,
        string name) =>
        ImportModule(builder, name, new ModuleImportOptions());

    /// <summary>Imports a defined module by name with resource aliases or a common prefix.</summary>
    public static IDistributedApplicationModule ImportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        ModuleImportOptions importOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(importOptions);

        var registry = GetOrCreateRegistry(builder);
        if (!registry.TryGetDefinition(name, out var module) || module is null)
        {
            throw new InvalidOperationException(
                $"Module '{name}' has not been defined. Call DefineModule or ExportModule before ImportModule.");
        }

        MaterializeModule(builder, module, registry, imported: true, importOptions);
        return module;
    }

    private static void MaterializeModule(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        bool imported,
        ModuleImportOptions? importOptions = null,
        string[]? materializationPath = null)
    {
        ValidateOptions(registry.Options);
        var materializationKey = GetMaterializationKey(imported, importOptions);
        if (!ShouldMaterialize(registry, module.Name, materializationKey))
        {
            return;
        }

        var currentPath = ExtendMaterializationPath(module, materializationPath);
        MaterializeCompositions(builder, module, registry, currentPath);

        var options = registry.Options;
        var moduleOptions = options.FindModule(module.Name);
        ValidateModuleConfiguration(module, moduleOptions);
        var resourceNames = new ModuleResourceNameMap(module, imported ? importOptions : null);
        RegisterProjectModes(module, registry, resourceNames);

        ValidateSynchronousResourceNames(
            builder,
            module,
            registry,
            options,
            moduleOptions,
            imported,
            resourceNames);

        var repositoryUsage = ResolveRepositoryUsageForSynchronousMaterialization(
            builder,
            module,
            registry,
            options,
            moduleOptions,
            imported,
            resourceNames);
        var definitionRepository = ModuleMaterializationPlanning.ResolveDefinitionRepository(
            builder,
            module,
            registry,
            moduleOptions,
            imported,
            repositoryUsage.ShouldPlan,
            repositoryUsage.RequiredOnRun);

        MaterializeDefinitions(
            builder,
            module,
            definitionRepository,
            imported,
            registry,
            options,
            moduleOptions,
            resourceNames);
        AddSkippedRepositoryStepWhenNeeded(
            builder,
            module,
            registry,
            imported,
            repositoryUsage,
            definitionRepository);

        registry.MarkMaterialized(module.Name, materializationKey);
    }

    private static bool ShouldMaterialize(
        ModuleApplicationRegistry registry,
        string moduleName,
        string materializationKey)
    {
        if (!registry.TryGetMaterialization(moduleName, out var existingMaterialization))
        {
            return true;
        }

        return ConfirmMatchingMaterialization(moduleName, existingMaterialization, materializationKey);
    }

    private static bool ConfirmMatchingMaterialization(
        string moduleName,
        string? existingMaterialization,
        string materializationKey)
    {
        if (string.Equals(existingMaterialization, materializationKey, StringComparison.Ordinal))
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Module '{moduleName}' is already materialized with different local/import or resource naming options.");
    }

    private static string[] ExtendMaterializationPath(
        DistributedApplicationModule module,
        string[]? materializationPath)
    {
        var path = materializationPath ?? [];
        var cycleStart = Array.FindIndex(
            path,
            name => string.Equals(name, module.Name, StringComparison.OrdinalIgnoreCase));
        ThrowIfMaterializationCycle(module, path, cycleStart);
        return path.Append(module.Name).ToArray();
    }

    private static void ThrowIfMaterializationCycle(
        DistributedApplicationModule module,
        string[] materializationPath,
        int cycleStart)
    {
        if (cycleStart < 0)
        {
            return;
        }

        var cycle = materializationPath.Skip(cycleStart).Append(module.Name);
        throw new InvalidOperationException(
            $"Module materialization cycle detected: {string.Join(" -> ", cycle.Select(name => $"'{name}'"))}.");
    }

    private static void MaterializeCompositions(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        string[] currentPath)
    {
        foreach (var composition in module.Compositions)
        {
            MaterializeModule(
                builder,
                composition.Module,
                registry,
                composition.Imported,
                composition.ImportOptions,
                currentPath);
        }
    }

    private static void RegisterProjectModes(
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        ModuleResourceNameMap resourceNames)
    {
        foreach (var project in module.ProjectDefinitions)
        {
            registry.ProjectModeSwitching?.RegisterProject(resourceNames[project.Name]);
        }
    }

    private static void MaterializeDefinitions(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        ModuleResourceNameMap resourceNames)
    {
        foreach (var definition in module.ResourceDefinitions)
        {
            MaterializeDefinition(
                builder,
                module,
                definition,
                resourceNames[definition.Name],
                definitionRepository,
                imported,
                registry,
                options,
                moduleOptions);
        }
    }

    private static void MaterializeDefinition(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleResource definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        if (definition is DistributedApplicationModuleProject project)
        {
            MaterializeProject(
                builder,
                module,
                project,
                resourceName,
                definitionRepository,
                imported,
                registry,
                options,
                moduleOptions);
            return;
        }

        MaterializeNonProjectDefinition(
            builder,
            module,
            definition,
            resourceName,
            definitionRepository,
            imported,
            registry,
            options,
            moduleOptions);
    }

    private static void MaterializeNonProjectDefinition(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleResource definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        if (definition is DistributedApplicationModuleContainer container)
        {
            MaterializeContainer(
                builder,
                module,
                container,
                resourceName,
                definitionRepository,
                imported,
                registry,
                options,
                moduleOptions);
            return;
        }

        MaterializeFactoryDefinition(
            builder,
            module,
            definition,
            resourceName,
            definitionRepository,
            imported,
            registry,
            options,
            moduleOptions);
    }

    private static void MaterializeFactoryDefinition(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleResource definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        if (definition is IDistributedApplicationModuleFactoryResource resource)
        {
            MaterializeFactoryResource(
                builder,
                module,
                resource,
                resourceName,
                definitionRepository,
                imported,
                registry,
                options,
                moduleOptions);
            return;
        }

        throw new InvalidOperationException(
            $"Module resource definition '{definition.Name}' has an unsupported implementation type.");
    }

    private static void AddSkippedRepositoryStepWhenNeeded(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        bool imported,
        ModuleRepositoryUsage repositoryUsage,
        ModuleRepositoryContext definitionRepository)
    {
        if (!ShouldAddSkippedRepositoryStep(builder, imported, repositoryUsage, definitionRepository))
        {
            return;
        }

        var omittedRepository = definitionRepository.Repository!;
        if (IsRepositoryPlanned(builder, registry, omittedRepository, definitionRepository.Revision))
        {
            return;
        }

        ModuleRepositoryInitializationPipeline.AddSkippedRepositoryStep(
            builder,
            module.Name,
            omittedRepository);
    }

    private static bool ShouldAddSkippedRepositoryStep(
        IDistributedApplicationBuilder builder,
        bool imported,
        ModuleRepositoryUsage repositoryUsage,
        ModuleRepositoryContext definitionRepository)
    {
        if (!imported || repositoryUsage.ShouldPlan)
        {
            return false;
        }

        return IsOmittedRemoteRepository(builder, definitionRepository.Repository);
    }

    private static bool IsOmittedRemoteRepository(
        IDistributedApplicationBuilder builder,
        string? repository) =>
        repository is not null && RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory);

    private static bool IsRepositoryPlanned(
        IDistributedApplicationBuilder builder,
        ModuleApplicationRegistry registry,
        string repository,
        string? revision) =>
        registry.RepositoryPlans?.Requirements.Any(requirement =>
            RepositoryIdentity.AreEquivalent(
                repository,
                requirement.Repository,
                builder.AppHostDirectory) &&
            string.Equals(revision, requirement.Revision, StringComparison.Ordinal)) ?? false;

    private static void MaterializeProject(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var projectOptions = moduleOptions?.FindProject(project.Name);
        ValidatePublisherDigest(project.Name, project.IsExportedAsContainer, projectOptions);
        var context = new ProjectMaterializationContext(
            builder,
            module,
            project,
            resourceName,
            definitionRepository,
            imported,
            registry,
            options,
            moduleOptions,
            projectOptions);
        if (TryMaterializeDeferredProject(context))
        {
            return;
        }

        var runAsContainer = ShouldRunAsContainer(context);
        MaterializeReadyProject(context, runAsContainer);
    }

    private static bool TryMaterializeDeferredProject(ProjectMaterializationContext context)
    {
        if (!ShouldConsiderDeferredProject(context))
        {
            return false;
        }

        var projectPath = PathSafety.GetContainedPath(
            context.DefinitionRepository.RepositoryPath,
            context.Project.GetRepositoryRelativeProjectPath(),
            nameof(context.Project.ProjectPath));
        return TryMaterializeMissingProject(context, projectPath);
    }

    private static bool ShouldConsiderDeferredProject(ProjectMaterializationContext context) =>
        IsLocallyBuiltProject(context.Project, context.ProjectOptions) &&
        CanDeferProject(context.Builder, context.DefinitionRepository);

    private static bool IsLocallyBuiltProject(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? projectOptions) =>
        project.Export.CommandOptions is null && !UsesExternalImage(projectOptions);

    private static bool CanDeferProject(
        IDistributedApplicationBuilder builder,
        ModuleRepositoryContext definitionRepository) =>
        !builder.ExecutionContext.IsRunMode && definitionRepository.InitializerOwned;

    private static bool TryMaterializeMissingProject(
        ProjectMaterializationContext context,
        string projectPath)
    {
        if (File.Exists(projectPath))
        {
            return false;
        }

        MaterializeDeferredProjectResource(
            context.Builder,
            context.Module,
            context.Project,
            context.ResourceName,
            context.DefinitionRepository.RepositoryPath,
            context.Imported,
            context.Registry);
        return true;
    }

    private static bool ShouldRunAsContainer(ProjectMaterializationContext context)
    {
        if (!context.Builder.ExecutionContext.IsRunMode)
        {
            return false;
        }

        return ResolveProjectMode(
            context.Options,
            context.ModuleOptions,
            context.ProjectOptions,
            context.Imported,
            context.ResourceName,
            context.Registry.ProjectModeSwitching) == ModuleProjectMode.Container;
    }

    private static void MaterializeReadyProject(
        ProjectMaterializationContext context,
        bool runAsContainer)
    {
        if (IsLocallyBuiltProject(context.Project, context.ProjectOptions) && !runAsContainer)
        {
            MaterializeNativeProjectResource(
                context.Builder,
                context.Module,
                context.Project,
                context.ResourceName,
                context.ProjectOptions,
                context.DefinitionRepository.RepositoryPath,
                context.Imported,
                context.Registry);
            return;
        }

        MaterializeContainerReadyProject(context, runAsContainer);
    }

    private static void MaterializeContainerReadyProject(
        ProjectMaterializationContext context,
        bool runAsContainer)
    {
        if (!runAsContainer && context.Builder.ExecutionContext.IsRunMode)
        {
            MaterializeProjectResourceSynchronously(
                context.Builder,
                context.Module,
                context.Project,
                context.ResourceName,
                context.ProjectOptions,
                context.DefinitionRepository.RepositoryPath,
                context.Imported,
                context.Registry);
            return;
        }

        MaterializePublishProject(context);
    }

    private static void MaterializePublishProject(ProjectMaterializationContext context)
    {
        if (IsLocallyBuiltProject(context.Project, context.ProjectOptions))
        {
            MaterializeNativeProjectContainerResource(
                context.Builder,
                context.Module,
                context.Project,
                context.ResourceName,
                context.ProjectOptions,
                context.DefinitionRepository.RepositoryPath,
                context.Imported,
                context.Registry);
            return;
        }

        MaterializeImageProject(context);
    }

    private static void MaterializeImageProject(ProjectMaterializationContext context)
    {
        var publisher = CreateProjectPublisher(context);
        var container = context.Builder.AddContainer(
            context.ResourceName,
            ResolveProjectImageName(context.Project, context.ProjectOptions, publisher),
            ResolveProjectImageTag(context.Project, context.ProjectOptions, publisher));
        ApplyImageRegistry(
            container,
            ResolveProjectImageRegistry(context.ProjectOptions, publisher));
        var image = CreateResourceImage(container, publisher, context.ProjectOptions);
        FinalizeContainerResource(
            context.Builder,
            container,
            new NormalizedContainerDescriptor(
                new ModuleContainerIdentity(
                    context.Module,
                    context.Project.Name,
                    context.ResourceName,
                    context.DefinitionRepository.RepositoryPath,
                    context.Imported),
                new ModuleContainerImagePlan(
                    publisher,
                    context.ProjectOptions,
                    image,
                    ResolveOwnedProjectImage(context.ProjectOptions, publisher, image)),
                context.Project.Export.ConfigureContainer,
                ModuleAnnotationAlreadyApplied: false),
            context.Registry);
    }

    private static ModuleImagePublisherAnnotation? CreateProjectPublisher(
        ProjectMaterializationContext context)
    {
        if (UsesExternalImage(context.ProjectOptions))
        {
            return null;
        }

        return CreateImagePublisher(
            context.Builder,
            context.Module,
            context.Project.Name,
            ModuleResourceKind.Project,
            context.Project.Export.CommandOptions!,
            context.ProjectOptions,
            context.DefinitionRepository,
            context.Registry,
            context.Options,
            context.ModuleOptions,
            GetProjectWorkingDirectory(context.Project),
            context.Project.GetRepositoryRelativeProjectPath());
    }

    private static string ResolveProjectImageName(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? projectOptions,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Recipe.Options.ImageName;
        }

        return ResolveUnpublishedProjectImageName(project, projectOptions);
    }

    private static string ResolveUnpublishedProjectImageName(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? projectOptions)
    {
        if (projectOptions is null)
        {
            return project.Export.ImageName;
        }

        return GetConfiguredValue(projectOptions.ImageName) ?? project.Export.ImageName;
    }

    private static string ResolveProjectImageTag(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? projectOptions,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return ModuleImageBuildRecipe.LocalRunTag;
        }

        return ResolveUnpublishedProjectImageTag(project, projectOptions);
    }

    private static string ResolveUnpublishedProjectImageTag(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? projectOptions)
    {
        var declaredTag = ResolveDeclaredProjectImageTag(project);
        var configuredTag = GetProjectImageTag(projectOptions);
        return configuredTag ?? declaredTag;
    }

    private static string? GetProjectImageTag(DistributedApplicationModuleProjectOptions? projectOptions)
    {
        if (projectOptions is null)
        {
            return null;
        }

        return GetConfiguredValue(projectOptions.ImageTag);
    }

    private static string ResolveDeclaredProjectImageTag(DistributedApplicationModuleProject project)
    {
        if (project.Export.CommandOptions is null)
        {
            return "latest";
        }

        return GetConfiguredValue(project.Export.CommandOptions.ImageTag) ?? "latest";
    }

    private static string? ResolveProjectImageRegistry(
        DistributedApplicationModuleProjectOptions? projectOptions,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Recipe.Options.ImageRegistry;
        }

        return GetConfiguredValue(projectOptions?.ImageRegistry);
    }

    private static ModuleResourceImage? ResolveOwnedProjectImage(
        DistributedApplicationModuleProjectOptions? projectOptions,
        ModuleImagePublisherAnnotation? publisher,
        ModuleResourceImage image)
    {
        if (publisher is not null)
        {
            return image;
        }

        return UsesExternalImage(projectOptions) ? image : null;
    }

    private static void MaterializeContainer(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleContainer definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var configured = moduleOptions?.FindContainer(definition.Name);
        ValidatePublishOverrides(definition, configured);
        var publisher = CreateContainerPublisher(
            builder,
            module,
            definition,
            configured,
            definitionRepository,
            registry,
            options,
            moduleOptions);
        var container = builder.AddContainer(
            resourceName,
            ResolveContainerImageName(definition, configured, publisher),
            ResolveContainerImageTag(definition, configured, publisher));
        ApplyImageRegistry(container, ResolveContainerImageRegistry(configured, publisher));
        var image = CreateResourceImage(container, publisher, configured);
        FinalizeContainerResource(
            builder,
            container,
            new NormalizedContainerDescriptor(
                new ModuleContainerIdentity(
                    module,
                    definition.Name,
                    resourceName,
                    definitionRepository.RepositoryPath,
                    imported),
                new ModuleContainerImagePlan(
                    publisher,
                    configured,
                    image,
                    ResolveOwnedImage(configured, publisher, image)),
                definition.ConfigureContainer,
                ModuleAnnotationAlreadyApplied: false),
            registry);
    }

    private static ModuleImagePublisherAnnotation? CreateContainerPublisher(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        if (definition.ImagePublishOptions is null || UsesExternalImage(configured))
        {
            return null;
        }

        return CreateImagePublisher(
            builder,
            module,
            definition.Name,
            ModuleResourceKind.Container,
            definition.ImagePublishOptions,
            configured,
            definitionRepository,
            registry,
            options,
            moduleOptions,
            defaultWorkingDirectory: ".");
    }

    private static string ResolveContainerImageName(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Recipe.Options.ImageName;
        }

        return ResolveUnpublishedContainerImageName(definition, configured);
    }

    private static string ResolveUnpublishedContainerImageName(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured)
    {
        if (configured is null)
        {
            return definition.Image;
        }

        return GetConfiguredValue(configured.ImageName) ?? definition.Image;
    }

    private static string ResolveContainerImageTag(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return ModuleImageBuildRecipe.LocalRunTag;
        }

        return ResolveUnpublishedContainerImageTag(definition, configured);
    }

    private static string ResolveUnpublishedContainerImageTag(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured)
    {
        if (configured is null)
        {
            return definition.Tag;
        }

        return GetConfiguredValue(configured.ImageTag) ?? definition.Tag;
    }

    private static string? ResolveContainerImageRegistry(
        DistributedApplicationModuleContainerOptions? configured,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Recipe.Options.ImageRegistry;
        }

        return GetConfiguredValue(configured?.ImageRegistry);
    }

    private static ModuleResourceImage? ResolveOwnedImage(
        DistributedApplicationModuleImageOptions? configured,
        ModuleImagePublisherAnnotation? publisher,
        ModuleResourceImage image)
    {
        if (publisher is not null)
        {
            return image;
        }

        return UsesExternalImage(configured) ? image : null;
    }

    private static void MaterializeFactoryResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        var configured = moduleOptions?.FindContainer(definition.Name);
        ValidatePublishOverrides(
            definition.Name,
            definition.ImagePublishOptions is not null,
            configured,
            nameof(IDistributedApplicationModuleBuilder.AddResource));
        var publisher = CreateFactoryPublisher(
            builder,
            module,
            definition,
            configured,
            definitionRepository,
            registry,
            options,
            moduleOptions);
        var resourceImage = CreateFactoryResourceImage(publisher, configured);
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            definitionRepository.RepositoryPath,
            imported,
            resourceImage);
        var moduleAnnotation = new DistributedApplicationModuleResourceAnnotation(
            module.Name,
            definition.Name,
            definitionRepository.RepositoryPath,
            imported,
            module.PackageId);
        var resource = MaterializeFactoryResource(
            builder,
            definition,
            context,
            moduleAnnotation,
            definitionRepository,
            resourceName);
        FinalizeFactoryResource(
            builder,
            module,
            definition,
            resourceName,
            definitionRepository,
            imported,
            registry,
            configured,
            publisher,
            resourceImage,
            resource);
    }

    private static ModuleImagePublisherAnnotation? CreateFactoryPublisher(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        DistributedApplicationModuleContainerOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        if (definition.ImagePublishOptions is null || UsesExternalImage(configured))
        {
            return null;
        }

        return CreateImagePublisher(
            builder,
            module,
            definition.Name,
            ModuleResourceKind.Container,
            definition.ImagePublishOptions,
            configured,
            definitionRepository,
            registry,
            options,
            moduleOptions,
            defaultWorkingDirectory: ".");
    }

    private static IResource MaterializeFactoryResource(
        IDistributedApplicationBuilder builder,
        IDistributedApplicationModuleFactoryResource definition,
        DistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation moduleAnnotation,
        ModuleRepositoryContext definitionRepository,
        string resourceName)
    {
        try
        {
            return definition.Materialize(context, moduleAnnotation);
        }
        catch (DistributedApplicationException exception) when (CanDeferFactoryProject(
            definition,
            definitionRepository))
        {
            return MaterializeDeferredFactoryProject(
                builder,
                definition,
                moduleAnnotation,
                definitionRepository,
                resourceName,
                exception);
        }
    }

    private static bool CanDeferFactoryProject(
        IDistributedApplicationModuleFactoryResource definition,
        ModuleRepositoryContext definitionRepository)
    {
        if (!typeof(ProjectResource).IsAssignableFrom(definition.ResourceType))
        {
            return false;
        }

        return IsMissingInitializerRepository(definitionRepository);
    }

    private static bool IsMissingInitializerRepository(ModuleRepositoryContext definitionRepository) =>
        definitionRepository.InitializerOwned && !Directory.Exists(definitionRepository.RepositoryPath);

    private static ProjectResource MaterializeDeferredFactoryProject(
        IDistributedApplicationBuilder builder,
        IDistributedApplicationModuleFactoryResource definition,
        DistributedApplicationModuleResourceAnnotation moduleAnnotation,
        ModuleRepositoryContext definitionRepository,
        string resourceName,
        DistributedApplicationException exception)
    {
        if (!builder.ExecutionContext.IsRunMode)
        {
            var deferredProject = FindOrAddDeferredProject(builder, resourceName);
            builder.CreateResourceBuilder(deferredProject).WithAnnotation(moduleAnnotation);
            return deferredProject;
        }

        var initializeCommand = ModuleRepositoryPreflight.CreateInitializeCommand(builder.AppHostDirectory);
        throw new InvalidOperationException(
            $"Module resource '{definition.Name}' requires repository " +
            $"'{definitionRepository.Repository}' at '{definitionRepository.RepositoryPath}', but the " +
            $"initializer-owned checkout is missing. Run '{initializeCommand}'.",
            exception);
    }

    private static ProjectResource FindOrAddDeferredProject(
        IDistributedApplicationBuilder builder,
        string resourceName) =>
        builder.Resources
            .OfType<ProjectResource>()
            .LastOrDefault(candidate => string.Equals(
                candidate.Name,
                resourceName,
                StringComparison.OrdinalIgnoreCase)) ??
        builder.AddResource(new ProjectResource(resourceName)).Resource;

    private static void FinalizeFactoryResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        IDistributedApplicationModuleFactoryResource definition,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleContainerOptions? configured,
        ModuleImagePublisherAnnotation? publisher,
        ModuleResourceImage? resourceImage,
        IResource resource)
    {
        if (resource is ContainerResource containerResource)
        {
            var container = builder.CreateResourceBuilder(containerResource);
            FinalizeContainerResource(
                builder,
                container,
                new NormalizedContainerDescriptor(
                    new ModuleContainerIdentity(
                        module,
                        definition.Name,
                        resourceName,
                        definitionRepository.RepositoryPath,
                        imported),
                    new ModuleContainerImagePlan(
                        publisher,
                        configured,
                        resourceImage,
                        resourceImage),
                    ConfigureContainer: null,
                    ModuleAnnotationAlreadyApplied: true),
                registry);
            return;
        }

        ValidateNonContainerFactoryPublisher(definition, publisher);
        ConfigureRepositoryLifecycle(
            builder,
            builder.CreateResourceBuilder(resource),
            registry);
        registry.TrackResource(resource);
        module.TrackMaterializedResource(builder, definition.Name, resource);
    }

    private static void ValidateNonContainerFactoryPublisher(
        IDistributedApplicationModuleFactoryResource definition,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            throw new InvalidOperationException(
                $"Image-published module resource '{definition.Name}' did not create a container resource.");
        }
    }

    private static void FinalizeContainerResource(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        NormalizedContainerDescriptor descriptor,
        ModuleApplicationRegistry registry)
    {
        var identity = descriptor.Identity;
        if (!descriptor.ModuleAnnotationAlreadyApplied)
        {
            container.WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                identity.Module.Name,
                identity.DeclaredResourceName,
                identity.RepositoryPath,
                identity.Imported,
                identity.Module.PackageId));
        }

        var context = new DistributedApplicationModuleResourceContext(
            builder,
            identity.Module,
            identity.EffectiveResourceName,
            identity.RepositoryPath,
            identity.Imported,
            descriptor.Image.ContextImage);
        descriptor.ConfigureContainer?.Invoke(context, container);
        ConfigureNativeDockerfilePublisher(builder, container, descriptor, registry.Options);
        ConfigureRepositoryLifecycle(builder, container, registry);
        ConfigureContainerImage(
            builder,
            container,
            descriptor.Image.Publisher,
            descriptor.Image.Configured,
            descriptor.Image.OwnedImage);
        registry.TrackResource(container.Resource);
        identity.Module.TrackMaterializedResource(
            builder,
            identity.DeclaredResourceName,
            container.Resource);
    }

    private static void ConfigureNativeDockerfilePublisher(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        NormalizedContainerDescriptor descriptor,
        ModularAppHostsOptions options)
    {
        if (HasExistingImagePublisher(container, descriptor))
        {
            return;
        }

        var dockerfile = container.Resource.Annotations
            .OfType<DockerfileBuildAnnotation>()
            .LastOrDefault();
        var image = container.Resource.Annotations
            .OfType<ContainerImageAnnotation>()
            .LastOrDefault();
        if (!HasDockerfileImage(dockerfile, image))
        {
            return;
        }

        var workflowTag = ModuleImageWorkflowConfiguration
            .Read(builder.Configuration)
            .ResolveTag(
                descriptor.Identity.Module.Name,
                descriptor.Identity.DeclaredResourceName);
        var defaultImageName = ResolveNativeDockerfileName(dockerfile!, image!);
        var defaultImageTag = ResolveNativeDockerfileTag(workflowTag, dockerfile!, image!);
        container.WithAnnotation(new ModuleNativeImagePublisherAnnotation(ModuleResourceKind.Container));
        ModuleNativeImageValidationPipeline.AddValidationStep(
            container,
            descriptor.Identity.RepositoryPath,
            options);
        container.WithImagePushOptions(context =>
            FillDefaultImagePushOptions(context, defaultImageName, defaultImageTag));
    }

    private static bool HasDockerfileImage(
        DockerfileBuildAnnotation? dockerfile,
        ContainerImageAnnotation? image) =>
        dockerfile is not null && image is not null;

    private static string ResolveNativeDockerfileName(
        DockerfileBuildAnnotation dockerfile,
        ContainerImageAnnotation image) =>
        GetConfiguredValue(dockerfile.ImageName) ?? image.Image;

    private static bool HasExistingImagePublisher(
        IResourceBuilder<ContainerResource> container,
        NormalizedContainerDescriptor descriptor)
    {
        if (descriptor.Image.Publisher is not null)
        {
            return true;
        }

        return UsesExternalImage(descriptor.Image.Configured) ||
            container.Resource.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().Any();
    }

    private static string ResolveNativeDockerfileTag(
        string? workflowTag,
        DockerfileBuildAnnotation dockerfile,
        ContainerImageAnnotation image)
    {
        var annotatedTag = ResolveAnnotatedImageTag(dockerfile, image);
        return workflowTag ?? annotatedTag;
    }

    private static string ResolveAnnotatedImageTag(
        DockerfileBuildAnnotation dockerfile,
        ContainerImageAnnotation image) =>
        GetConfiguredValue(dockerfile.ImageTag) ?? GetConfiguredValue(image.Tag) ?? "latest";

    private static ModuleImagePublisherAnnotation CreateImagePublisher(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string declaredResourceName,
        ModuleResourceKind resourceKind,
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        string defaultWorkingDirectory,
        string? requiredProjectRelativePath = null)
    {
        var workflow = ModuleImageWorkflowConfiguration.Read(builder.Configuration);
        var effectiveOptions = ApplyImageRecipeOptions(
            declared,
            configured,
            workflow.ResolveTag(module.Name, declaredResourceName));
        var canPrepareWithoutBuildRepository = CanPrepareWithoutBuildRepository(effectiveOptions);
        var buildRepository = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            declaredResourceName,
            effectiveOptions,
            configured,
            definitionRepository,
            registry,
            moduleOptions,
            allowMissingBuildRepository: canPrepareWithoutBuildRepository,
            resourceKind: resourceKind);
        var allowsUnavailableSource = AllowsUnavailableSource(
            canPrepareWithoutBuildRepository,
            buildRepository);
        var workingDirectoryRelativePath = ResolveImageWorkingDirectory(
            effectiveOptions,
            buildRepository,
            defaultWorkingDirectory);
        var workingDirectory = PathSafety.GetContainedPath(
            buildRepository.RepositoryPath,
            workingDirectoryRelativePath,
            nameof(ModuleImageCommandOptions.WorkingDirectory));
        registry.RequireDirectory(
            module.Name,
            $"image build directory for resource '{declaredResourceName}'",
            workingDirectory,
            requiredOnRun: !allowsUnavailableSource,
            resourceName: declaredResourceName);
        RequirePublisherProjectFile(
            registry,
            module,
            declaredResourceName,
            definitionRepository,
            requiredProjectRelativePath);

        var refresh = ResolveBuildRepositoryRefresh(configured, options);
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity(module.Name, declaredResourceName),
            new ModuleImageRepositorySettings(
                buildRepository.RepositoryPath,
                workingDirectory,
                buildRepository.Repository,
                buildRepository.Revision,
                refresh,
                ResolveExecutable(options.GitExecutablePath, "git"),
                ResolveExecutable(options.GitHubCliPath, "gh"),
                options.RepositoryCommandTimeout,
                GetDetachedAppHostBranchAlias(builder, buildRepository),
                builder.AppHostDirectory,
                buildRepository.InitializerOwned,
                allowsUnavailableSource),
            new ModuleImageCommandSettings(
                effectiveOptions,
                options.ImageBuildTimeout,
                options.ImageTransferTimeout));
        return new ModuleImagePublisherAnnotation(resourceKind, recipe);
    }

    private static bool CanPrepareWithoutBuildRepository(ModuleImageCommandOptions options) =>
        options.PullBeforeBuild && !string.IsNullOrWhiteSpace(options.ImageTag);

    private static bool AllowsUnavailableSource(
        bool canPrepareWithoutBuildRepository,
        ModuleRepositoryContext buildRepository) =>
        canPrepareWithoutBuildRepository && !buildRepository.UsesModuleRepository;

    private static string ResolveImageWorkingDirectory(
        ModuleImageCommandOptions options,
        ModuleRepositoryContext buildRepository,
        string defaultWorkingDirectory)
    {
        if (options.WorkingDirectory is not null)
        {
            return options.WorkingDirectory;
        }

        return buildRepository.UsesModuleRepository ? defaultWorkingDirectory : ".";
    }

    private static void RequirePublisherProjectFile(
        ModuleApplicationRegistry registry,
        DistributedApplicationModule module,
        string declaredResourceName,
        ModuleRepositoryContext definitionRepository,
        string? requiredProjectRelativePath)
    {
        if (requiredProjectRelativePath is null)
        {
            return;
        }

        registry.RequireFile(
            module.Name,
            $"project '{declaredResourceName}'",
            PathSafety.GetContainedPath(
                definitionRepository.RepositoryPath,
                requiredProjectRelativePath,
                nameof(IDistributedApplicationModuleProject.ProjectPath)),
            resourceName: declaredResourceName);
    }

    private static bool ResolveBuildRepositoryRefresh(
        DistributedApplicationModuleImageOptions? configured,
        ModularAppHostsOptions options) =>
        configured?.RefreshBuildRepositoryOnRun ?? options.RefreshBuildRepositoriesOnRun;

    private static string ResolveExecutable(string? configured, string fallback) =>
        GetConfiguredValue(configured) ?? fallback;

    private static string GetProjectWorkingDirectory(DistributedApplicationModuleProject project)
    {
        var directory = Path.GetDirectoryName(project.GetRepositoryRelativeProjectPath());
        return string.IsNullOrWhiteSpace(directory) ? "." : directory;
    }

    private static string? GetDetachedAppHostBranchAlias(
        IDistributedApplicationBuilder builder,
        ModuleRepositoryContext buildRepository)
    {
        if (buildRepository.Revision is not null)
        {
            return null;
        }

        var appHostRepositoryRoot = RepositoryIdentity.TryFindRepositoryRoot(builder.AppHostDirectory);
        var buildRepositoryRoot = RepositoryIdentity.TryFindRepositoryRoot(buildRepository.RepositoryPath);
        if (!AreSameRepositoryRoots(appHostRepositoryRoot, buildRepositoryRoot))
        {
            return null;
        }

        return ResolveGitHubHeadReference(builder);
    }

    private static bool AreSameRepositoryRoots(string? first, string? second)
    {
        if (first is null || second is null)
        {
            return false;
        }

        return PathSafety.AreEqual(first, second);
    }

    private static string? ResolveGitHubHeadReference(IDistributedApplicationBuilder builder) =>
        GetConfiguredValue(builder.Configuration["GITHUB_HEAD_REF"]) ??
        GetConfiguredValue(builder.Configuration["GITHUB_REF_NAME"]);

    private static void ConfigureContainerImage(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured,
        ModuleResourceImage? ownedImage)
    {
        ApplyOptionalOwnedImage(container, ownedImage);
        ApplyImageSHA256(container, configured?.ImageSHA256);
        ApplyImagePullPolicy(container, configured?.ImagePullPolicy);
        ModuleImagePullPipeline.AddPullStep(container);
        ConfigurePublishedContainerImage(builder, container, publisher);
    }

    private static void ApplyOptionalOwnedImage(
        IResourceBuilder<ContainerResource> container,
        ModuleResourceImage? ownedImage)
    {
        if (ownedImage is not null)
        {
            ApplyOwnedImage(container, ownedImage);
        }
    }

    private static void ConfigurePublishedContainerImage(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is null)
        {
            return;
        }

        container.WithAnnotation(publisher);
        container.WithImagePushOptions(context => ApplyPublisherPushDefaults(context, publisher));
        ModuleImageBuildPipeline.AddBuildStep(container);
        ModuleImagePushPipeline.AddPushStep(container);
        ConfigureRunImagePreparation(builder, container, publisher);
    }

    private static void ApplyPublisherPushDefaults(
        ContainerImagePushOptionsCallbackContext context,
        ModuleImagePublisherAnnotation publisher)
    {
        if (string.IsNullOrWhiteSpace(context.Options.RemoteImageName))
        {
            context.Options.RemoteImageName = publisher.Options.ImageName;
        }

        if (string.IsNullOrWhiteSpace(context.Options.RemoteImageTag))
        {
            context.Options.RemoteImageTag = ResolvePublisherPushTag(publisher);
        }
    }

    private static string ResolvePublisherPushTag(ModuleImagePublisherAnnotation publisher)
    {
        if (publisher.TryGetPreparedImage(out var preparedImage))
        {
            return ModuleImageReference.GetTag(preparedImage.CanonicalImageReference);
        }

        return publisher.Options.ImageTag ?? "latest";
    }

    private static void ConfigureRunImagePreparation(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation publisher)
    {
        if (!builder.ExecutionContext.IsRunMode)
        {
            return;
        }

        container.OnBeforeResourceStarted((resource, @event, cancellationToken) =>
            PreparePublisherImageAsync(resource, @event, publisher, cancellationToken));
    }

    private static async Task PreparePublisherImageAsync(
        ContainerResource resource,
        BeforeResourceStartedEvent @event,
        ModuleImagePublisherAnnotation publisher,
        CancellationToken cancellationToken)
    {
        var resourceLogger = @event.Services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(resource);
        try
        {
            await publisher.PrepareAsync(
                @event.Services,
                resourceLogger,
                resourceLogger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldLogImagePreparationFailure(exception, cancellationToken))
        {
            LogImagePreparationFailed(
                resourceLogger,
                publisher.ModuleName,
                publisher.ResourceName,
                exception);
            throw;
        }
    }

    private static bool ShouldLogImagePreparationFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is not OperationCanceledException | !cancellationToken.IsCancellationRequested;

    private static void ConfigureRepositoryLifecycle<TResource>(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<TResource> resource,
        ModuleApplicationRegistry registry)
        where TResource : IResource
    {
        var module = resource.Resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .Last();
        var scope = registry.GetRepositoryPreflightScope(module.ModuleName, module.ResourceName);
        if (!HasPreflightRequirements(scope))
        {
            return;
        }

        ConfigureRequiredRepositoryCommands(resource, registry, scope);

        if (!builder.ExecutionContext.IsRunMode)
        {
            return;
        }

        resource.OnBeforeResourceStarted(async (startedResource, @event, cancellationToken) =>
        {
            var logger = @event.Services
                .GetRequiredService<ResourceLoggerService>()
                .GetLogger(startedResource);
            await ModuleApplicationRegistry.ValidateRepositoryPreflightAsync(
                scope,
                @event.Services.GetRequiredService<IModuleRepositoryStateStore>(),
                new ModuleRepositoryInitializationSettings(
                    registry.Options.GitExecutablePath,
                    registry.Options.GitHubCliPath,
                    registry.Options.RepositoryCommandTimeout),
                builder.AppHostDirectory,
                logger,
                cancellationToken).ConfigureAwait(false);
        });
    }

    private static bool HasPreflightRequirements(ModuleRepositoryPreflightScope scope) =>
        scope.Repositories.Count > 0 || scope.RequiredPaths.Count > 0;

    private static void ConfigureRequiredRepositoryCommands<TResource>(
        IResourceBuilder<TResource> resource,
        ModuleApplicationRegistry registry,
        ModuleRepositoryPreflightScope scope)
        where TResource : IResource
    {
        if (scope.Repositories.Count == 0)
        {
            return;
        }

        resource.WithRequiredCommand(registry.Options.GitExecutablePath);
        AddRequiredGitHubCli(resource, registry, scope);
    }

    private static void AddRequiredGitHubCli<TResource>(
        IResourceBuilder<TResource> resource,
        ModuleApplicationRegistry registry,
        ModuleRepositoryPreflightScope scope)
        where TResource : IResource
    {
        if (scope.Repositories.Any(requirement =>
                GitHubGitAuthentication.UsesCredentialProvider(requirement.Repository)))
        {
            resource.WithRequiredCommand(registry.Options.GitHubCliPath);
        }
    }

    private static void MaterializeProjectResourceSynchronously(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var projectPath = PathSafety.GetContainedPath(
            repositoryPath,
            project.GetRepositoryRelativeProjectPath(),
            nameof(project.ProjectPath));
        registry.RequireFile(
            module.Name,
            $"project '{project.Name}'",
            projectPath,
            resourceName: project.Name);
        var resource = builder
            .AddProject(resourceName, projectPath, projectOptions =>
                ConfigureProjectOptions(projectOptions, options))
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported,
                module.PackageId));
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            repositoryPath,
            imported);
        ConfigureModuleProject(project, context, resource);
        ConfigureRepositoryLifecycle(builder, resource, registry);
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static void MaterializeNativeProjectResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var projectPath = PathSafety.GetContainedPath(
            repositoryPath,
            project.GetRepositoryRelativeProjectPath(),
            nameof(project.ProjectPath));
        registry.RequireFile(
            module.Name,
            $"project '{project.Name}'",
            projectPath,
            resourceName: project.Name);
        var workflow = ModuleImageWorkflowConfiguration.Read(builder.Configuration);
        var imageName = ResolveNativeProjectImageName(project, options);
        var imageTag = ResolveNativeProjectImageTag(workflow, module, project, options);
        var imageRegistry = GetProjectImageRegistry(options);
        var resource = builder
            .AddProject(resourceName, projectPath, projectOptions =>
                ConfigureProjectOptions(projectOptions, options))
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported,
                module.PackageId))
            .WithAnnotation(new ModuleNativeImagePublisherAnnotation(ModuleResourceKind.Project));
        var contextImage = new ModuleResourceImage(imageRegistry, imageName, imageTag);
        var context = new DistributedApplicationModuleResourceContext(
            builder,
            module,
            resourceName,
            repositoryPath,
            imported,
            contextImage);
        ConfigureModuleProject(project, context, resource);
        resource.PublishAsDockerFile(container => ConfigureNativeProjectContainer(
            container,
            project,
            context,
            options,
            imageRegistry,
            imageName,
            imageTag));
        ModuleNativeImageValidationPipeline.AddValidationStep(resource, repositoryPath, registry.Options);
        ConfigureRepositoryLifecycle(builder, resource, registry);
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static string? GetProjectImageRegistry(DistributedApplicationModuleProjectOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return GetConfiguredValue(options.ImageRegistry);
    }

    private static void ConfigureModuleProject(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleResourceContext context,
        IResourceBuilder<ProjectResource> resource)
    {
        if (project.ConfigureProject is not null)
        {
            project.ConfigureProject(context, resource);
        }
    }

    private static void ConfigureNativeProjectContainer(
        IResourceBuilder<ContainerResource> container,
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleProjectOptions? options,
        string? imageRegistry,
        string imageName,
        string imageTag)
    {
        container.WithImage(imageName, imageTag);
        ApplyImageRegistry(container, imageRegistry);
        ApplyImagePullPolicy(container, GetProjectImagePullPolicy(options));
        ConfigureExportedProjectContainer(project, context, container);
        container.WithImagePushOptions(pushContext =>
            FillDefaultImagePushOptions(pushContext, imageName, imageTag));
    }

    private static ImagePullPolicy? GetProjectImagePullPolicy(
        DistributedApplicationModuleProjectOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return options.ImagePullPolicy;
    }

    private static void ConfigureExportedProjectContainer(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleResourceContext context,
        IResourceBuilder<ContainerResource> container)
    {
        if (project.Export.ConfigureContainer is not null)
        {
            project.Export.ConfigureContainer(context, container);
        }
    }

    private static void ConfigureProjectOptions(
        ProjectResourceOptions projectOptions,
        DistributedApplicationModuleProjectOptions? configured)
    {
        ConfigureLaunchProfileName(projectOptions, configured);
        ConfigureExcludeLaunchProfile(projectOptions, configured);
        ConfigureExcludeKestrelEndpoints(projectOptions, configured);
    }

    private static void ConfigureLaunchProfileName(
        ProjectResourceOptions projectOptions,
        DistributedApplicationModuleProjectOptions? configured)
    {
        if (configured?.LaunchProfileName is not null)
        {
            projectOptions.LaunchProfileName = configured.LaunchProfileName;
        }
    }

    private static void ConfigureExcludeLaunchProfile(
        ProjectResourceOptions projectOptions,
        DistributedApplicationModuleProjectOptions? configured)
    {
        if (configured?.ExcludeLaunchProfile is { } excludeLaunchProfile)
        {
            projectOptions.ExcludeLaunchProfile = excludeLaunchProfile;
        }
    }

    private static void ConfigureExcludeKestrelEndpoints(
        ProjectResourceOptions projectOptions,
        DistributedApplicationModuleProjectOptions? configured)
    {
        if (configured?.ExcludeKestrelEndpoints is { } excludeKestrelEndpoints)
        {
            projectOptions.ExcludeKestrelEndpoints = excludeKestrelEndpoints;
        }
    }

    private static string ResolveNativeProjectImageName(
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? options) =>
        GetConfiguredValue(options?.ImageName) ?? project.Export.ImageName;

    private static string ResolveNativeProjectImageTag(
        ModuleImageWorkflowOptions workflow,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? options)
    {
        var workflowTag = workflow.ResolveTag(module.Name, project.Name);
        if (workflowTag is not null)
        {
            return workflowTag;
        }

        return GetProjectImageTag(options) ?? "latest";
    }

    private static void MaterializeDeferredProjectResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var resource = builder
            .AddResource(new ProjectResource(resourceName))
            .WithAnnotation(new DistributedApplicationModuleResourceAnnotation(
                module.Name,
                project.Name,
                repositoryPath,
                imported,
                module.PackageId));
        ConfigureRepositoryLifecycle(builder, resource, registry);
        registry.TrackResource(resource.Resource);
        module.TrackMaterializedResource(builder, project.Name, resource.Resource);
    }

    private static void MaterializeNativeProjectContainerResource(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        DistributedApplicationModuleProjectOptions? options,
        string repositoryPath,
        bool imported,
        ModuleApplicationRegistry registry)
    {
        var workflow = ModuleImageWorkflowConfiguration.Read(builder.Configuration);
        var imageName = ResolveNativeProjectImageName(project, options);
        var imageTag = ResolveNativeProjectImageTag(workflow, module, project, options);
        var imageRegistry = GetConfiguredValue(options?.ImageRegistry);
        var container = builder.AddContainer(resourceName, imageName, imageTag);
        ApplyImageRegistry(container, imageRegistry);
        var image = new ModuleResourceImage(imageRegistry, imageName, imageTag);
        FinalizeContainerResource(
            builder,
            container,
            new NormalizedContainerDescriptor(
                new ModuleContainerIdentity(
                    module,
                    project.Name,
                    resourceName,
                    repositoryPath,
                    imported),
                new ModuleContainerImagePlan(
                    Publisher: null,
                    Configured: options,
                    ContextImage: image,
                    OwnedImage: image),
                project.Export.ConfigureContainer,
                ModuleAnnotationAlreadyApplied: false),
            registry);
    }

    private static ModuleImageCommandOptions ApplyImageRecipeOptions(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        string? workflowTag)
    {
        var imageName = ResolveImageName(declared, configured);
        var imageRegistry = ResolveImageRegistry(declared, configured);
        var publishCommand = ResolvePublishCommand(declared, configured);
        var publishArguments = ResolvePublishArguments(declared, configured);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishCommand);
        return new ModuleImageCommandOptions(imageName, publishCommand, publishArguments)
        {
            ImageRegistry = imageRegistry,
            ProducedImageReference = ResolveProducedImageReference(declared, configured),
            PullBeforeBuild = ResolvePullBeforeBuild(declared, configured),
            ImageTag = ResolveImageTag(declared, configured, workflowTag),
            WorkingDirectory = ResolveWorkingDirectory(declared, configured),
            BuildRepository = ResolveBuildRepository(declared, configured),
            BuildRepositoryRevision = ResolveBuildRepositoryRevision(declared, configured),
            CheckoutDirectoryName = ResolveCheckoutDirectoryName(declared, configured)
        };
    }

    private static string ResolveImageName(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        GetConfiguredValue(configured?.ImageName) ?? declared.ImageName;

    private static string? ResolveImageRegistry(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured)
    {
        if (configured?.ImageRegistry is not null)
        {
            return GetConfiguredValue(configured.ImageRegistry);
        }

        return GetConfiguredValue(declared.ImageRegistry);
    }

    private static string ResolvePublishCommand(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        GetConfiguredValue(configured?.PublishCommand) ?? declared.PublishCommand;

    private static string[] ResolvePublishArguments(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured)
    {
        if (configured?.PublishArguments is { } publishArguments)
        {
            return publishArguments.ToArray();
        }

        return declared.PublishArguments.ToArray();
    }

    private static string? ResolveProducedImageReference(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        configured?.ProducedImageReference ?? declared.ProducedImageReference;

    private static bool ResolvePullBeforeBuild(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        configured?.PullBeforeBuild ?? declared.PullBeforeBuild;

    private static string? ResolveImageTag(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        string? workflowTag)
    {
        if (workflowTag is not null)
        {
            return workflowTag;
        }

        return ResolveConfiguredImageTag(declared, configured);
    }

    private static string? ResolveConfiguredImageTag(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        GetConfiguredValue(configured?.ImageTag) ?? GetConfiguredValue(declared.ImageTag);

    private static string? ResolveWorkingDirectory(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        configured?.PublishWorkingDirectory ?? declared.WorkingDirectory;

    private static string? ResolveBuildRepository(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        GetConfiguredValue(configured?.BuildRepository) ?? GetConfiguredValue(declared.BuildRepository);

    private static string? ResolveBuildRepositoryRevision(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        GetConfiguredValue(configured?.BuildRepositoryRevision) ??
        GetConfiguredValue(declared.BuildRepositoryRevision);

    private static string? ResolveCheckoutDirectoryName(
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured) =>
        configured?.CheckoutDirectoryName ?? declared.CheckoutDirectoryName;

    private static ModuleResourceImage CreateResourceImage(
        IResourceBuilder<ContainerResource> container,
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured)
    {
        var image = container.Resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        return new ModuleResourceImage(
            ResolveResourceImageRegistry(image, publisher),
            ResolveResourceImageName(image, publisher),
            ResolveResourceImageTag(image, publisher),
            GetConfiguredValue(configured?.ImageSHA256));
    }

    private static string? ResolveResourceImageRegistry(
        ContainerImageAnnotation image,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Recipe.Options.ImageRegistry;
        }

        return image.Registry;
    }

    private static string ResolveResourceImageName(
        ContainerImageAnnotation image,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return publisher.Recipe.Options.ImageName;
        }

        return image.Image;
    }

    private static string ResolveResourceImageTag(
        ContainerImageAnnotation image,
        ModuleImagePublisherAnnotation? publisher)
    {
        if (publisher is not null)
        {
            return ModuleImageBuildRecipe.LocalRunTag;
        }

        return image.Tag ?? "latest";
    }

    private static ModuleResourceImage? CreateFactoryResourceImage(
        ModuleImagePublisherAnnotation? publisher,
        DistributedApplicationModuleImageOptions? configured)
    {
        if (publisher is not null)
        {
            return new ModuleResourceImage(
                publisher.Recipe.Options.ImageRegistry,
                publisher.Recipe.Options.ImageName,
                ModuleImageBuildRecipe.LocalRunTag,
                GetConfiguredValue(configured?.ImageSHA256));
        }

        return CreateUnpublishedFactoryResourceImage(configured);
    }

    private static ModuleResourceImage? CreateUnpublishedFactoryResourceImage(
        DistributedApplicationModuleImageOptions? configured)
    {
        if (!UsesExternalImage(configured))
        {
            return null;
        }

        return CreateExternalFactoryResourceImage(configured!);
    }

    private static ModuleResourceImage CreateExternalFactoryResourceImage(
        DistributedApplicationModuleImageOptions configured) =>
        new(
            GetConfiguredValue(configured!.ImageRegistry),
            GetConfiguredValue(configured.ImageName)!,
            GetConfiguredValue(configured.ImageTag) ?? "latest",
            GetConfiguredValue(configured.ImageSHA256));

    private static void ApplyOwnedImage(
        IResourceBuilder<ContainerResource> container,
        ModuleResourceImage image)
    {
        var annotation = container.Resource.Annotations
            .OfType<ContainerImageAnnotation>()
            .LastOrDefault() ?? throw new InvalidOperationException(
                $"Image-published module resource '{container.Resource.Name}' created a container without an image. " +
                "Create it with AddContainer and use context.Image for the managed image identity.");
        annotation.Registry = image.Registry;
        annotation.Image = image.Name;
        container.WithImageTag(image.Tag);
    }

    private static void FillDefaultImagePushOptions(
        ContainerImagePushOptionsCallbackContext context,
        string imageName,
        string imageTag)
    {
#pragma warning disable CA1308
        var aspireDefaultName = context.Resource.Name.ToLowerInvariant();
#pragma warning restore CA1308
        if (ShouldReplaceImageName(context.Options.RemoteImageName, aspireDefaultName))
        {
            context.Options.RemoteImageName = imageName;
        }

        if (ShouldReplaceImageTag(context.Options.RemoteImageTag))
        {
            context.Options.RemoteImageTag = imageTag;
        }
    }

    private static bool ShouldReplaceImageName(string? imageName, string aspireDefaultName) =>
        string.IsNullOrWhiteSpace(imageName) ||
        string.Equals(imageName, aspireDefaultName, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldReplaceImageTag(string? imageTag) =>
        string.IsNullOrWhiteSpace(imageTag) ||
        string.Equals(imageTag, "latest", StringComparison.OrdinalIgnoreCase);

    private sealed class ProjectMaterializationContext(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleProject project,
        string resourceName,
        ModuleRepositoryContext definitionRepository,
        bool imported,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        DistributedApplicationModuleProjectOptions? projectOptions)
    {
        public IDistributedApplicationBuilder Builder { get; } = builder;

        public DistributedApplicationModule Module { get; } = module;

        public DistributedApplicationModuleProject Project { get; } = project;

        public string ResourceName { get; } = resourceName;

        public ModuleRepositoryContext DefinitionRepository { get; } = definitionRepository;

        public bool Imported { get; } = imported;

        public ModuleApplicationRegistry Registry { get; } = registry;

        public ModularAppHostsOptions Options { get; } = options;

        public DistributedApplicationModuleOptions? ModuleOptions { get; } = moduleOptions;

        public DistributedApplicationModuleProjectOptions? ProjectOptions { get; } = projectOptions;
    }

    private sealed record ModuleContainerIdentity(
        DistributedApplicationModule Module,
        string DeclaredResourceName,
        string EffectiveResourceName,
        string RepositoryPath,
        bool Imported);

    private sealed record ModuleContainerImagePlan(
        ModuleImagePublisherAnnotation? Publisher,
        DistributedApplicationModuleImageOptions? Configured,
        ModuleResourceImage? ContextImage,
        ModuleResourceImage? OwnedImage);

    private sealed record NormalizedContainerDescriptor(
        ModuleContainerIdentity Identity,
        ModuleContainerImagePlan Image,
        Action<
            IDistributedApplicationModuleResourceContext,
            IResourceBuilder<ContainerResource>>? ConfigureContainer,
        bool ModuleAnnotationAlreadyApplied);

    private readonly record struct ModuleRepositoryUsage(bool ShouldPlan, bool RequiredOnRun);

    private static ModuleRepositoryUsage ResolveRepositoryUsageForSynchronousMaterialization(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames)
    {
        if (module.ExplicitlyRequiresRepositoryContent)
        {
            return new ModuleRepositoryUsage(ShouldPlan: true, RequiredOnRun: true);
        }

        var usage = new ModuleRepositoryUsage(ShouldPlan: false, RequiredOnRun: false);
        foreach (var project in module.ProjectDefinitions)
        {
            usage = IncludeProjectRepositoryUsage(
                builder,
                project,
                registry,
                options,
                moduleOptions,
                imported,
                resourceNames,
                usage);
        }

        return IncludeContainerRepositoryUsage(module, moduleOptions, usage);
    }

    private static ModuleRepositoryUsage IncludeProjectRepositoryUsage(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModuleProject project,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames,
        ModuleRepositoryUsage usage)
    {
        var configured = moduleOptions?.FindProject(project.Name);
        var runAsProject = IsRunAsProject(
            builder,
            project,
            configured,
            registry,
            options,
            moduleOptions,
            imported,
            resourceNames);
        var publishesFromModuleRepository = PublishesFromModuleRepository(
            configured,
            project.Export.CommandOptions);
        return new ModuleRepositoryUsage(
            ShouldPlan: true,
            RequiredOnRun: usage.RequiredOnRun || runAsProject || publishesFromModuleRepository);
    }

    private static bool IsRunAsProject(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModuleProject project,
        DistributedApplicationModuleProjectOptions? configured,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames)
    {
        if (!builder.ExecutionContext.IsRunMode)
        {
            return false;
        }

        return ResolveProjectMode(
            options,
            moduleOptions,
            configured,
            imported,
            resourceNames[project.Name],
            registry.ProjectModeSwitching) == ModuleProjectMode.Project;
    }

    private static bool PublishesFromModuleRepository(
        DistributedApplicationModuleImageOptions? configured,
        ModuleImageCommandOptions? declared)
    {
        if (UsesExternalImage(configured))
        {
            return false;
        }

        return HasNoBuildRepository(configured, declared);
    }

    private static bool HasNoBuildRepository(
        DistributedApplicationModuleImageOptions? configured,
        ModuleImageCommandOptions? declared) =>
        GetConfiguredBuildRepository(configured) is null && GetDeclaredBuildRepository(declared) is null;

    private static string? GetConfiguredBuildRepository(DistributedApplicationModuleImageOptions? configured)
    {
        if (configured is null)
        {
            return null;
        }

        return GetConfiguredValue(configured.BuildRepository);
    }

    private static string? GetDeclaredBuildRepository(ModuleImageCommandOptions? declared)
    {
        if (declared is null)
        {
            return null;
        }

        return GetConfiguredValue(declared.BuildRepository);
    }

    private static ModuleRepositoryUsage IncludeContainerRepositoryUsage(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? moduleOptions,
        ModuleRepositoryUsage usage)
    {
        foreach (var publisher in GetContainerPublishers(module))
        {
            usage = IncludeContainerPublisherRepositoryUsage(moduleOptions, publisher, usage);
        }

        return usage;
    }

    private static ModuleRepositoryUsage IncludeContainerPublisherRepositoryUsage(
        DistributedApplicationModuleOptions? moduleOptions,
        (string ResourceName, ModuleImageCommandOptions Options) publisher,
        ModuleRepositoryUsage usage)
    {
        var configured = moduleOptions?.FindContainer(publisher.ResourceName);
        if (PublishesFromModuleRepository(configured, publisher.Options))
        {
            return new ModuleRepositoryUsage(ShouldPlan: true, RequiredOnRun: true);
        }

        return usage;
    }

    private static void ValidateSynchronousResourceNames(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        ModuleResourceNameMap resourceNames)
    {
        var planned = module.ResourceDefinitions
            .Select(definition => resourceNames[definition.Name])
            .ToArray();
        ValidateNoDuplicateResourceNames(module, planned);
        ValidateNoExistingResourceNames(builder, module, registry, planned);
    }

    private static void ValidateNoDuplicateResourceNames(
        DistributedApplicationModule module,
        string[] planned)
    {
        var duplicate = planned
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Cannot materialize module '{module.Name}' because its aliases and prefix " +
                $"produce duplicate resource '{duplicate}'.");
        }
    }

    private static void ValidateNoExistingResourceNames(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        IEnumerable<string> planned)
    {
        foreach (var resourceName in planned)
        {
            ValidateResourceNameIsAvailable(builder, module, registry, resourceName);
        }
    }

    private static void ValidateResourceNameIsAvailable(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        string resourceName)
    {
        if (!builder.Resources.Any(resource => string.Equals(
                resource.Name,
                resourceName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var tracked = GetTrackedResourceDiagnostic(registry, resourceName);
        throw new InvalidOperationException(
            $"Cannot materialize module '{module.Name}' because resource '{resourceName}' already exists{tracked}.");
    }

    private static string GetTrackedResourceDiagnostic(
        ModuleApplicationRegistry registry,
        string resourceName) =>
        TrackedResourceDiagnostics[registry.TryGetResource(resourceName, out _)];

}
