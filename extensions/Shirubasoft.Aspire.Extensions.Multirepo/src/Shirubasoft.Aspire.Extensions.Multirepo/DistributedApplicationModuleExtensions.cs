#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

/// <summary>Extensions for composing reusable modules in an Aspire AppHost.</summary>
public static partial class DistributedApplicationModuleExtensions
{
    private static readonly Func<DistributedApplicationModuleContainerOptions, bool>[]
        ImagePublishingOverrideRules =
        [
            options => !string.IsNullOrWhiteSpace(options.PublishCommand),
            options => options.PublishArguments is not null,
            options => options.PublishWorkingDirectory is not null,
            options => options.ProducedImageReference is not null,
            options => options.PullBeforeBuild is not null,
            options => options.BuildRepository is not null,
            options => options.BuildRepositoryRevision is not null,
            options => options.CheckoutDirectoryName is not null,
            options => options.RefreshBuildRepositoryOnRun is not null,
            options => options.PublishImage is true
        ];

    /// <summary>Exports a named module definition without adding its services to the application model.</summary>
    public static IDistributedApplicationModule ExportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DefineModule(builder, name, "1", packageId: null, moduleBuilder);
    }

    /// <summary>Exports a named module definition with its NuGet contract package identity.</summary>
    public static IDistributedApplicationModule ExportModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DefineModule(builder, name, "1", packageId, moduleBuilder);
    }

    /// <summary>Defines a versioned module contract without adding its resources to the application model.</summary>
    public static IDistributedApplicationModule DefineModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DefineModule(builder, name, version, packageId: null, moduleBuilder);
    }

    /// <summary>Defines a versioned module contract with its NuGet package identity.</summary>
    public static IDistributedApplicationModule DefineModule(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        string? packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ValidatePackageId(packageId);
        ArgumentNullException.ThrowIfNull(moduleBuilder);

        var registry = GetOrCreateRegistry(builder);
        ValidateOptions(registry.Options);
        if (registry.TryGetDefinition(name, out var existingModule) && existingModule is not null)
        {
            ValidateExistingDefinition(existingModule, version, packageId);
            return existingModule;
        }

        registry.BeginDefinition(name);
        try
        {
            var module = new DistributedApplicationModule(builder, name, version, packageId);
            moduleBuilder(new DistributedApplicationModuleBuilder(builder, module, registry));
            module.Validate();
            ValidateModuleConfiguration(module, registry.Options.FindModule(module.Name));
            registry.AddModule(module);
            return module;
        }
        finally
        {
            registry.EndDefinition(name);
        }
    }

    /// <summary>Configures module materialization options after applying AppHost configuration.</summary>
    public static IDistributedApplicationBuilder ConfigureModularAppHosts(
        this IDistributedApplicationBuilder builder,
        Action<ModularAppHostsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var optionsModel = GetOrCreateOptionsModel(builder);
        _ = optionsModel.ConfigureAndCreatePreview(configure, builder.Configuration, ValidateOptions);
        return builder;
    }

    /// <summary>Runs every exported project directly in Aspire run mode for local debugging.</summary>
    public static IDistributedApplicationBuilder UseLocalModuleProjects(
        this IDistributedApplicationBuilder builder)
    {
        return builder.ConfigureModularAppHosts(options => options.ProjectMode = ModuleProjectMode.Project);
    }

    /// <summary>Runs every exported project through its portable container representation.</summary>
    public static IDistributedApplicationBuilder UseModuleContainers(
        this IDistributedApplicationBuilder builder)
    {
        return builder.ConfigureModularAppHosts(options => options.ProjectMode = ModuleProjectMode.Container);
    }

    /// <summary>Gets the configuration key used to resolve an imported module's Git repository.</summary>
    public static string GetRepositoryConfigurationKey(string moduleName)
    {
        return $"{GetModuleConfigurationKey(moduleName)}:Repository";
    }

    /// <summary>Gets the conventional configuration section key for a module.</summary>
    public static string GetModuleConfigurationKey(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:{moduleName}";
    }

    private static void ValidatePublishOverrides(
        DistributedApplicationModuleContainer definition,
        DistributedApplicationModuleContainerOptions? configured)
    {
        ValidatePublishOverrides(
            definition.Name,
            definition.ImagePublishOptions is not null,
            configured,
            nameof(IDistributedApplicationModuleContainerBuilder.WithImagePublishCommand));
    }

    private static void ValidatePublishOverrides(
        string resourceName,
        bool hasDeclaredPublisher,
        DistributedApplicationModuleContainerOptions? configured,
        string declarationMethod)
    {
        if (configured is null)
        {
            return;
        }

        ValidatePublisherDigest(resourceName, hasDeclaredPublisher, configured);
        ValidateUndeclaredPublisherOverrides(
            resourceName,
            hasDeclaredPublisher,
            configured,
            declarationMethod);
    }

    private static void ValidateUndeclaredPublisherOverrides(
        string resourceName,
        bool hasDeclaredPublisher,
        DistributedApplicationModuleContainerOptions configured,
        string declarationMethod)
    {
        if (hasDeclaredPublisher)
        {
            return;
        }

        if (HasImagePublishingOverrides(configured))
        {
            throw new InvalidOperationException(
                $"Container resource '{resourceName}' configures image publishing, but its module definition does not " +
                $"call {declarationMethod}() with image publish options.");
        }
    }

    private static bool HasImagePublishingOverrides(DistributedApplicationModuleContainerOptions configured) =>
        ImagePublishingOverrideRules.Any(rule => rule(configured));

    private static void ValidatePublisherDigest(
        string resourceName,
        bool hasDeclaredPublisher,
        DistributedApplicationModuleImageOptions? configured)
    {
        if (HasInvalidPublisherDigest(hasDeclaredPublisher, configured))
        {
            throw new InvalidOperationException(
                $"Image-published resource '{resourceName}' cannot configure {nameof(configured.ImageSHA256)} " +
                $"unless {nameof(configured.PublishImage)} is false. A digest identifies an external immutable image, " +
                "not the image produced by the declared publish command.");
        }
    }

    private static bool HasInvalidPublisherDigest(
        bool hasDeclaredPublisher,
        DistributedApplicationModuleImageOptions? configured)
    {
        if (!hasDeclaredPublisher)
        {
            return false;
        }

        return HasConfiguredPublisherDigest(configured);
    }

    private static bool HasConfiguredPublisherDigest(DistributedApplicationModuleImageOptions? configured) =>
        configured is not null && HasEnabledPublisherDigest(configured);

    private static bool HasEnabledPublisherDigest(DistributedApplicationModuleImageOptions configured)
    {
        if (configured.PublishImage is false)
        {
            return false;
        }

        return GetConfiguredValue(configured.ImageSHA256) is not null;
    }

    private static void ApplyImagePullPolicy(
        IResourceBuilder<ContainerResource> container,
        ImagePullPolicy? policy)
    {
        if (policy is { } configuredPolicy)
        {
            container.WithImagePullPolicy(configuredPolicy);
        }
    }

    private static void ApplyImageRegistry(
        IResourceBuilder<ContainerResource> container,
        string? registry)
    {
        if (registry is not null)
        {
            container.WithImageRegistry(registry);
        }
    }

    private static void ApplyImageSHA256(
        IResourceBuilder<ContainerResource> container,
        string? sha256)
    {
        var configuredSha256 = GetConfiguredValue(sha256);
        if (configuredSha256 is not null)
        {
            container.WithImageSHA256(configuredSha256["sha256:".Length..]);
        }
    }

    private static ModuleApplicationRegistry GetOrCreateRegistry(IDistributedApplicationBuilder builder)
    {
        var existing = builder.Services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog))?
            .ImplementationInstance as ModuleApplicationRegistry;

        if (existing is not null)
        {
            return existing;
        }

        var optionsModel = GetOrCreateOptionsModel(builder);
        var options = optionsModel.CreateSnapshot(builder.Configuration);
        ValidateOptions(options);
        optionsModel.Freeze();
        var registry = new ModuleApplicationRegistry(
            options,
            projectModeSwitching: new ModuleProjectModeSwitchingPipeline(builder));
        builder.Services.AddSingleton<ModuleRepositoryRefreshCoordinator>();
        builder.Services.AddSingleton<IModuleRepositoryStateStore>(_ =>
            new FileModuleRepositoryStateStore(
                FileModuleRepositoryStateStore.ResolveStateFilePath(
                    builder.Configuration["AppHost:PathSha256"],
                    builder.AppHostDirectory)));
        ModuleImagePullPipeline.Configure(builder);
        ModuleImageDescriptionPipeline.Configure(builder);
        ModuleImageWorkflowPipeline.Configure(builder);
        builder.Services.AddSingleton<IDistributedApplicationModuleCatalog>(registry);
        var validationStep = new PipelineStep
        {
            Name = "validate-module-repositories",
            Description = "Validates initialized module repository checkouts.",
            Action = async context =>
            {
                ValidateOptions(registry.Options);
                registry.ValidateConfiguredModules();
                var settings = new ModuleRepositoryInitializationSettings(
                    GetConfiguredValue(registry.Options.GitExecutablePath) ?? "git",
                    GetConfiguredValue(registry.Options.GitHubCliPath) ?? "gh",
                    registry.Options.RepositoryCommandTimeout);
                await registry.ValidateRepositoryPreflightAsync(
                    context.Services.GetRequiredService<IModuleRepositoryStateStore>(),
                    settings,
                    builder.AppHostDirectory,
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);
            },
            RequiredBySteps =
            [
                WellKnownPipelineSteps.BuildPrereq,
                WellKnownPipelineSteps.PublishPrereq
            ]
        };
        builder.Pipeline.AddStep(validationStep);
        return registry;
    }

    private static ModularAppHostsOptionsModel GetOrCreateOptionsModel(
        IDistributedApplicationBuilder builder)
    {
        var existing = builder.Services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(ModularAppHostsOptionsModel))?
            .ImplementationInstance as ModularAppHostsOptionsModel;
        if (existing is not null)
        {
            return existing;
        }

        var model = new ModularAppHostsOptionsModel();
        builder.Services.AddSingleton(model);
        builder.Services
            .AddOptions<ModularAppHostsOptions>()
            .BindConfiguration(ModularAppHostsOptions.ConfigurationSectionName)
            .PostConfigure(model.Apply)
            .PostConfigure(ModularAppHostsOptionsModel.PostConfigure)
            .Validate(AreOptionsValid, "Modular AppHost configuration is invalid.");
        return model;
    }

    private static bool AreOptionsValid(ModularAppHostsOptions options)
    {
        try
        {
            ValidateOptions(options);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ValidateModuleConfiguration(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? configured)
    {
        if (configured is null)
        {
            return;
        }

        var projectNames = module.ProjectDefinitions
            .Select(project => project.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containerNames = module.ContainerDefinitions
            .Select(container => container.Name)
            .Concat(module.ResourceDefinitions
                .OfType<IDistributedApplicationModuleFactoryResource>()
                .Where(resource => resource.ImagePublishOptions is not null)
                .Select(resource => resource.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateConfiguredProjects(module, configured, projectNames);
        ValidateConfiguredContainers(module, configured, containerNames);
    }

    private static void ValidateConfiguredProjects(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions configured,
        HashSet<string> projectNames)
    {
        var missingProject = configured.Projects.Keys.FirstOrDefault(name => !projectNames.Contains(name));
        if (missingProject is not null)
        {
            throw new InvalidOperationException(
                $"Configuration for module '{module.Name}' references project service '{missingProject}', but no " +
                $"exported project with that name was found. Available projects: {FormatNames(projectNames)}.");
        }
    }

    private static void ValidateConfiguredContainers(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions configured,
        HashSet<string> containerNames)
    {
        var missingContainer = configured.Containers.Keys.FirstOrDefault(name => !containerNames.Contains(name));
        if (missingContainer is not null)
        {
            throw new InvalidOperationException(
                $"Configuration for module '{module.Name}' references container service '{missingContainer}', but no " +
                $"exported container with that name was found. Available containers: {FormatNames(containerNames)}.");
        }
    }

    private static string FormatNames(IEnumerable<string> names)
    {
        var values = names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? "(none)" : string.Join(", ", values.Select(name => $"'{name}'"));
    }

    private static IEnumerable<(string ResourceName, ModuleImageCommandOptions Options)> GetContainerPublishers(
        DistributedApplicationModule module) =>
        GetDeclaredContainerPublishers(module).Concat(GetFactoryResourcePublishers(module));

    private static IEnumerable<(string ResourceName, ModuleImageCommandOptions Options)>
        GetDeclaredContainerPublishers(DistributedApplicationModule module) =>
        module.ContainerDefinitions
            .Where(container => container.ImagePublishOptions is not null)
            .Select(container => (container.Name, container.ImagePublishOptions!));

    private static IEnumerable<(string ResourceName, ModuleImageCommandOptions Options)>
        GetFactoryResourcePublishers(DistributedApplicationModule module) =>
        module.ResourceDefinitions
            .OfType<IDistributedApplicationModuleFactoryResource>()
            .Where(resource => resource.ImagePublishOptions is not null)
            .Select(resource => (resource.Name, resource.ImagePublishOptions!));

    private static ModuleProjectMode ResolveProjectMode(
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        DistributedApplicationModuleProjectOptions? projectOptions,
        bool imported,
        string effectiveResourceName,
        ModuleProjectModeSwitchingPipeline? switching)
    {
        var mode = ResolveConfiguredProjectMode(options, moduleOptions, projectOptions);
        var defaultMode = ResolveDefaultProjectMode(mode, imported);
        if (switching is not null)
        {
            return switching.Resolve(effectiveResourceName, mode, imported);
        }

        return defaultMode;
    }

    private static ModuleProjectMode ResolveConfiguredProjectMode(
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions,
        DistributedApplicationModuleProjectOptions? projectOptions)
    {
        if (projectOptions?.ProjectMode is { } projectMode)
        {
            return projectMode;
        }

        return ResolveConfiguredModuleProjectMode(options, moduleOptions);
    }

    private static ModuleProjectMode ResolveConfiguredModuleProjectMode(
        ModularAppHostsOptions options,
        DistributedApplicationModuleOptions? moduleOptions)
    {
        if (moduleOptions?.ProjectMode is { } moduleMode)
        {
            return moduleMode;
        }

        return options.ProjectMode;
    }

    private static ModuleProjectMode ResolveDefaultProjectMode(ModuleProjectMode mode, bool imported)
    {
        if (mode != ModuleProjectMode.Auto)
        {
            return mode;
        }

        return imported ? ModuleProjectMode.Container : ModuleProjectMode.Project;
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool UsesExternalImage(DistributedApplicationModuleImageOptions? options)
    {
        return options?.PublishImage == false;
    }

    private static void ValidatePackageId(string? packageId)
    {
        if (packageId is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ValidatePackageIdLength(packageId);
        ValidatePackageIdCharacters(packageId);
    }

    private static void ValidatePackageIdLength(string packageId)
    {
        if (packageId.Length > 100)
        {
            throw new ArgumentException(
                $"'{packageId}' is not a valid NuGet package ID.",
                nameof(packageId));
        }
    }

    private static void ValidatePackageIdCharacters(string packageId)
    {
        if (packageId.Any(character => !IsPackageIdCharacter(character)))
        {
            throw new ArgumentException(
                $"'{packageId}' is not a valid NuGet package ID.",
                nameof(packageId));
        }
    }

    private static bool IsPackageIdCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || ".-_".Contains(character, StringComparison.Ordinal);

    private static void ValidateExistingDefinition(
        DistributedApplicationModule existingModule,
        string version,
        string? packageId)
    {
        ValidateExistingVersion(existingModule, version);
        ValidateExistingPackage(existingModule, packageId);
    }

    private static void ValidateExistingVersion(
        DistributedApplicationModule existingModule,
        string version)
    {
        if (!string.Equals(existingModule.Version, version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{existingModule.Name}' is already defined with contract version '{existingModule.Version}', " +
                $"not requested version '{version}'.");
        }
    }

    private static void ValidateExistingPackage(
        DistributedApplicationModule existingModule,
        string? packageId)
    {
        if (!string.Equals(existingModule.PackageId, packageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module '{existingModule.Name}' is already defined with contract package ID " +
                $"'{FormatPackageId(existingModule.PackageId)}', not requested package ID '{FormatPackageId(packageId)}'.");
        }
    }

    private static string FormatPackageId(string? packageId) => packageId ?? "none";

    private static string GetMaterializationKey(bool imported, ModuleImportOptions? options)
    {
        var aliases = GetResourceAliases(options)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}");
        return $"{imported}|{GetResourcePrefix(options)}|{string.Join("|", aliases)}";
    }

    private static IEnumerable<KeyValuePair<string, string>> GetResourceAliases(ModuleImportOptions? options)
    {
        if (options is null)
        {
            return [];
        }

        return options.ResourceAliases;
    }

    private static string? GetResourcePrefix(ModuleImportOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return options.ResourcePrefix;
    }

    private static void ValidateOptions(ModularAppHostsOptions options)
    {
        ValidateRepositoryCommandTimeout(options);
        ValidateImageBuildTimeout(options);
        ValidateImageTransferTimeout(options);
        ValidateEnum(
            options.ProjectMode,
            $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.ProjectMode)}");
        ValidateModules(options);
    }

    private static void ValidateRepositoryCommandTimeout(ModularAppHostsOptions options)
    {
        if (options.RepositoryCommandTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.RepositoryCommandTimeout)} must be positive.");
        }
    }

    private static void ValidateImageBuildTimeout(ModularAppHostsOptions options)
    {
        if (options.ImageBuildTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.ImageBuildTimeout)} must be positive.");
        }
    }

    private static void ValidateImageTransferTimeout(ModularAppHostsOptions options)
    {
        if (options.ImageTransferTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.ImageTransferTimeout)} must be positive.");
        }
    }

    private static void ValidateModules(ModularAppHostsOptions options)
    {
        foreach (var (moduleName, module) in options.Modules)
        {
            ValidateModuleOptions(options, moduleName, module);
        }
    }

    private static void ValidateModuleOptions(
        ModularAppHostsOptions options,
        string moduleName,
        DistributedApplicationModuleOptions module)
    {
        var moduleKey = $"{ModularAppHostsOptions.ConfigurationSectionName}:{nameof(options.Modules)}:{moduleName}";
        ValidateCheckoutDirectoryName(
            module.CheckoutDirectoryName,
            module.RepositoryRevision,
            $"{moduleKey}:{nameof(module.CheckoutDirectoryName)}");
        ValidateOptionalModuleProjectMode(module, moduleKey);
        ValidateProjectOptions(module, moduleKey);
        ValidateContainerOptions(module, moduleKey);
    }

    private static void ValidateOptionalModuleProjectMode(
        DistributedApplicationModuleOptions module,
        string moduleKey)
    {
        if (module.ProjectMode is { } moduleMode)
        {
            ValidateEnum(moduleMode, $"{moduleKey}:{nameof(module.ProjectMode)}");
        }
    }

    private static void ValidateProjectOptions(
        DistributedApplicationModuleOptions module,
        string moduleKey)
    {
        foreach (var (projectName, project) in module.Projects)
        {
            ValidateProjectOptions(module, moduleKey, projectName, project);
        }
    }

    private static void ValidateProjectOptions(
        DistributedApplicationModuleOptions module,
        string moduleKey,
        string projectName,
        DistributedApplicationModuleProjectOptions project)
    {
        var projectKey = $"{moduleKey}:{nameof(module.Projects)}:{projectName}";
        ValidateCheckoutDirectoryName(
            project.CheckoutDirectoryName,
            project.BuildRepositoryRevision,
            $"{projectKey}:{nameof(project.CheckoutDirectoryName)}");
        ValidateImageSHA256(project.ImageSHA256, $"{projectKey}:{nameof(project.ImageSHA256)}");
        ValidateExternalImage(project, projectKey);
        ValidateOptionalProjectMode(project, projectKey);
        ValidateOptionalProjectPullPolicy(project, projectKey);
    }

    private static void ValidateOptionalProjectMode(
        DistributedApplicationModuleProjectOptions project,
        string projectKey)
    {
        if (project.ProjectMode is { } projectMode)
        {
            ValidateEnum(projectMode, $"{projectKey}:{nameof(project.ProjectMode)}");
        }
    }

    private static void ValidateOptionalProjectPullPolicy(
        DistributedApplicationModuleProjectOptions project,
        string projectKey)
    {
        if (project.ImagePullPolicy is { } projectPullPolicy)
        {
            ValidateEnum(projectPullPolicy, $"{projectKey}:{nameof(project.ImagePullPolicy)}");
        }
    }

    private static void ValidateContainerOptions(
        DistributedApplicationModuleOptions module,
        string moduleKey)
    {
        foreach (var (containerName, container) in module.Containers)
        {
            ValidateContainerOptions(module, moduleKey, containerName, container);
        }
    }

    private static void ValidateContainerOptions(
        DistributedApplicationModuleOptions module,
        string moduleKey,
        string containerName,
        DistributedApplicationModuleContainerOptions container)
    {
        var containerKey = $"{moduleKey}:{nameof(module.Containers)}:{containerName}";
        ValidateCheckoutDirectoryName(
            container.CheckoutDirectoryName,
            container.BuildRepositoryRevision,
            $"{containerKey}:{nameof(container.CheckoutDirectoryName)}");
        ValidateImageSHA256(container.ImageSHA256, $"{containerKey}:{nameof(container.ImageSHA256)}");
        ValidateExternalImage(container, containerKey);
        ValidateOptionalContainerPullPolicy(container, containerKey);
    }

    private static void ValidateOptionalContainerPullPolicy(
        DistributedApplicationModuleContainerOptions container,
        string containerKey)
    {
        if (container.ImagePullPolicy is { } containerPullPolicy)
        {
            ValidateEnum(containerPullPolicy, $"{containerKey}:{nameof(container.ImagePullPolicy)}");
        }
    }

    private static void ValidateExternalImage(
        DistributedApplicationModuleImageOptions options,
        string configurationKey)
    {
        if (options.PublishImage != false)
        {
            return;
        }

        ValidateExternalImageIdentity(options, configurationKey);
    }

    private static void ValidateExternalImageIdentity(
        DistributedApplicationModuleImageOptions options,
        string configurationKey)
    {
        ValidateExternalImageRegistry(options, configurationKey);
        ValidateExternalImageName(options, configurationKey);
        ValidateExternalImageTagOrDigest(options, configurationKey);
    }

    private static void ValidateExternalImageRegistry(
        DistributedApplicationModuleImageOptions options,
        string configurationKey)
    {
        if (GetConfiguredValue(options.ImageRegistry) is null)
        {
            throw new InvalidOperationException(
                $"{configurationKey}:{nameof(options.ImageRegistry)} is required when " +
                $"{configurationKey}:{nameof(options.PublishImage)} is false.");
        }
    }

    private static void ValidateExternalImageName(
        DistributedApplicationModuleImageOptions options,
        string configurationKey)
    {
        if (GetConfiguredValue(options.ImageName) is null)
        {
            throw new InvalidOperationException(
                $"{configurationKey}:{nameof(options.ImageName)} is required when " +
                $"{configurationKey}:{nameof(options.PublishImage)} is false.");
        }
    }

    private static void ValidateExternalImageTagOrDigest(
        DistributedApplicationModuleImageOptions options,
        string configurationKey)
    {
        var hasTag = GetConfiguredValue(options.ImageTag) is not null;
        var hasDigest = GetConfiguredValue(options.ImageSHA256) is not null;
        if (hasTag == hasDigest)
        {
            throw new InvalidOperationException(
                $"Configure exactly one of {configurationKey}:{nameof(options.ImageTag)} or " +
                $"{configurationKey}:{nameof(options.ImageSHA256)} when " +
                $"{configurationKey}:{nameof(options.PublishImage)} is false.");
        }
    }

    private static void ValidateCheckoutDirectoryName(
        string? checkoutDirectoryName,
        string? revision,
        string configurationKey)
    {
        if (checkoutDirectoryName is null)
        {
            return;
        }

        _ = RepositoryIdentity.ValidateCheckoutDirectoryName(
            checkoutDirectoryName,
            configurationKey,
            revision,
            Directory.GetCurrentDirectory());
    }

    private static void ValidateImageSHA256(string? sha256, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }

        if (!IsValidImageSHA256(sha256))
        {
            throw new InvalidOperationException(
                $"{configurationKey} must use the form 'sha256:<64 lowercase hexadecimal characters>'.");
        }
    }

    private static bool IsValidImageSHA256(string sha256)
    {
        const string prefix = "sha256:";
        if (!sha256.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return HasValidSHA256Suffix(sha256, prefix.Length);
    }

    private static bool HasValidSHA256Suffix(string sha256, int prefixLength)
    {
        if (sha256.Length != prefixLength + 64)
        {
            return false;
        }

        return sha256[prefixLength..].All(IsLowerHexadecimalCharacter);
    }

    private static bool IsLowerHexadecimalCharacter(char character) =>
        "0123456789abcdef".Contains(character, StringComparison.Ordinal);

    private static void ValidateEnum<TEnum>(TEnum value, string configurationKey)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new InvalidOperationException(
                $"{configurationKey} has unsupported value '{value}'. Expected one of: " +
                $"{string.Join(", ", Enum.GetNames<TEnum>())}.");
        }
    }

}
