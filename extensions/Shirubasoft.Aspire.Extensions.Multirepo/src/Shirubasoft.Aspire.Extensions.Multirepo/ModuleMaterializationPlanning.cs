namespace Aspire.Hosting;

internal sealed record ModuleRepositoryContext(
    string RepositoryPath,
    string? Repository,
    string? Revision,
    bool InitializerOwned,
    bool UsesModuleRepository,
    bool IsResolved = true);

internal static class ModuleMaterializationPlanning
{
    public static ModuleRepositoryContext ResolveDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool imported,
        bool shouldPlan,
        bool requiredOnRun)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(registry);

        var configurationKey = DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(module.Name);
        var settings = ResolveDefinitionRepositorySettings(
            builder,
            module,
            moduleOptions,
            configurationKey);
        if (!imported)
        {
            return ResolveLocalDefinitionRepository(builder, module, settings.Repository);
        }

        return ResolveImportedDefinitionRepository(
            builder,
            module,
            registry,
            moduleOptions,
            shouldPlan,
            requiredOnRun,
            configurationKey,
            settings);
    }

    private static DefinitionRepositorySettings ResolveDefinitionRepositorySettings(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? moduleOptions,
        string configurationKey) =>
        new(
            ResolveDefinitionRepositoryValue(builder, module, moduleOptions, configurationKey),
            ResolveDefinitionRevision(module, moduleOptions),
            ResolveDefinitionCheckoutDirectoryName(module, moduleOptions));

    private static string? ResolveDefinitionRepositoryValue(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? moduleOptions,
        string configurationKey)
    {
        var configuredRepository = GetConfiguredValue(builder.Configuration[configurationKey]);
        if (configuredRepository is not null)
        {
            return configuredRepository;
        }

        return ResolveDeclaredDefinitionRepository(module, moduleOptions);
    }

    private static string? ResolveDeclaredDefinitionRepository(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? moduleOptions) =>
        GetConfiguredValue(moduleOptions?.Repository) ?? GetConfiguredValue(module.Repository);

    private static string? ResolveDefinitionRevision(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? moduleOptions) =>
        GetConfiguredValue(moduleOptions?.RepositoryRevision) ?? GetConfiguredValue(module.RepositoryRevision);

    private static string? ResolveDefinitionCheckoutDirectoryName(
        DistributedApplicationModule module,
        DistributedApplicationModuleOptions? moduleOptions) =>
        moduleOptions?.CheckoutDirectoryName ?? module.CheckoutDirectoryName;

    private static ModuleRepositoryContext ResolveLocalDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string? repository)
    {
        var localPath = GetLocalDefinitionPath(builder, module, repository);
        return new ModuleRepositoryContext(
            localPath,
            repository ?? localPath,
            Revision: null,
            InitializerOwned: false,
            UsesModuleRepository: true);
    }

    private static ModuleRepositoryContext ResolveImportedDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool shouldPlan,
        bool requiredOnRun,
        string configurationKey,
        DefinitionRepositorySettings settings)
    {
        if (IsOptionalDefinitionRepository(shouldPlan, requiredOnRun, settings.Repository))
        {
            return ResolveOptionalDefinitionRepository(builder, settings);
        }

        return ResolveRequiredDefinitionRepository(
            builder,
            module,
            registry,
            moduleOptions,
            requiredOnRun,
            configurationKey,
            settings);
    }

    private static bool IsOptionalDefinitionRepository(
        bool shouldPlan,
        bool requiredOnRun,
        string? repository) =>
        !shouldPlan || repository is null && !requiredOnRun;

    private static ModuleRepositoryContext ResolveOptionalDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DefinitionRepositorySettings settings) =>
        new(
            GetOptionalDefinitionPath(builder, settings.Repository),
            settings.Repository,
            settings.Revision,
            InitializerOwned: false,
            UsesModuleRepository: true,
            IsResolved: false);

    private static string GetOptionalDefinitionPath(
        IDistributedApplicationBuilder builder,
        string? repository)
    {
        if (repository is null || RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory))
        {
            return Path.GetFullPath(builder.AppHostDirectory);
        }

        return Path.GetFullPath(repository, builder.AppHostDirectory);
    }

    private static ModuleRepositoryContext ResolveRequiredDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool requiredOnRun,
        string configurationKey,
        DefinitionRepositorySettings settings)
    {
        if (settings.Repository is null)
        {
            throw new InvalidOperationException(
                $"Imported module '{module.Name}' requires repository content. Configure '{configurationKey}' " +
                "or declare the repository with WithRepository().");
        }

        return ResolvePresentDefinitionRepository(
            builder,
            module,
            registry,
            moduleOptions,
            requiredOnRun,
            configurationKey,
            settings,
            settings.Repository);
    }

    private static ModuleRepositoryContext ResolvePresentDefinitionRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool requiredOnRun,
        string configurationKey,
        DefinitionRepositorySettings settings,
        string repository)
    {
        var isRemote = RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory);
        if (IsUnpinnedLocalRepository(isRemote, settings.Revision))
        {
            RejectLocalCheckoutDirectoryName(settings.CheckoutDirectoryName, configurationKey);
            var localPath = Path.GetFullPath(repository, builder.AppHostDirectory);
            registry.RequireDirectory(module.Name, "repository checkout", localPath);
            return new ModuleRepositoryContext(
                localPath,
                localPath,
                Revision: null,
                InitializerOwned: false,
                UsesModuleRepository: true);
        }

        var updateRepository = ResolveUpdateRepository(moduleOptions, registry);
        var requirement = registry.RegisterRepository(
            builder,
            module.Name,
            NormalizeDefinitionRepository(repository, builder, isRemote),
            settings.Revision,
            updateRepository,
            requiredOnRun,
            checkoutDirectoryName: settings.CheckoutDirectoryName,
            checkoutDirectoryNameConfigurationKey: GetCheckoutDirectoryNameConfigurationKey(module.Name));
        return new ModuleRepositoryContext(
            requirement.RepositoryPath,
            requirement.Repository,
            requirement.Revision,
            InitializerOwned: true,
            UsesModuleRepository: true);
    }

    private static bool IsUnpinnedLocalRepository(bool isRemote, string? revision) =>
        !isRemote && revision is null;

    private static bool ResolveUpdateRepository(
        DistributedApplicationModuleOptions? moduleOptions,
        ModuleApplicationRegistry registry) =>
        moduleOptions?.UpdateRepositoryOnInitialize ?? registry.Options.UpdateRepositoriesOnInitialize;

    private static string NormalizeDefinitionRepository(
        string repository,
        IDistributedApplicationBuilder builder,
        bool isRemote)
    {
        if (isRemote)
        {
            return repository;
        }

        return Path.GetFullPath(repository, builder.AppHostDirectory);
    }

    public static ModuleRepositoryContext ResolveBuildRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string resourceName,
        ModuleImageCommandOptions declared,
        DistributedApplicationModuleImageOptions? configured,
        ModuleRepositoryContext definitionRepository,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool allowMissingBuildRepository = false,
        ModuleResourceKind resourceKind = ModuleResourceKind.Container)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(definitionRepository);
        ArgumentNullException.ThrowIfNull(registry);

        var requestedRepository = ResolveRequestedBuildRepository(configured, declared);
        var requestedRevision = ResolveRequestedBuildRevision(configured, declared);
        var checkoutDirectoryName = declared.CheckoutDirectoryName;
        var checkoutDirectoryNameConfigurationKey = GetBuildRepositoryCheckoutDirectoryNameConfigurationKey(
            module.Name,
            resourceName,
            resourceKind);
        if (!HasBuildRepositoryOverride(requestedRepository, requestedRevision, checkoutDirectoryName))
        {
            return definitionRepository;
        }

        var repository = ResolveBuildRepositoryValue(requestedRepository, definitionRepository);
        var normalizedRepository = NormalizeBuildRepository(builder, repository);
        if (CanReuseDefinitionRepository(
            builder,
            normalizedRepository,
            requestedRevision,
            checkoutDirectoryName,
            definitionRepository))
        {
            return definitionRepository;
        }

        return ResolveDistinctBuildRepository(
            builder,
            module,
            resourceName,
            normalizedRepository,
            requestedRevision,
            checkoutDirectoryName,
            checkoutDirectoryNameConfigurationKey,
            registry,
            moduleOptions,
            allowMissingBuildRepository);
    }

    private static string? ResolveRequestedBuildRepository(
        DistributedApplicationModuleImageOptions? configured,
        ModuleImageCommandOptions declared) =>
        GetConfiguredValue(configured?.BuildRepository) ?? GetConfiguredValue(declared.BuildRepository);

    private static string? ResolveRequestedBuildRevision(
        DistributedApplicationModuleImageOptions? configured,
        ModuleImageCommandOptions declared) =>
        GetConfiguredValue(configured?.BuildRepositoryRevision) ??
        GetConfiguredValue(declared.BuildRepositoryRevision);

    private static bool HasBuildRepositoryOverride(
        string? requestedRepository,
        string? requestedRevision,
        string? checkoutDirectoryName) =>
        requestedRepository is not null || requestedRevision is not null || checkoutDirectoryName is not null;

    private static string ResolveBuildRepositoryValue(
        string? requestedRepository,
        ModuleRepositoryContext definitionRepository) =>
        requestedRepository ?? definitionRepository.Repository ?? definitionRepository.RepositoryPath;

    private static string NormalizeBuildRepository(
        IDistributedApplicationBuilder builder,
        string repository)
    {
        if (RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory))
        {
            return repository;
        }

        return Path.GetFullPath(repository, builder.AppHostDirectory);
    }

    private static bool CanReuseDefinitionRepository(
        IDistributedApplicationBuilder builder,
        string repository,
        string? revision,
        string? checkoutDirectoryName,
        ModuleRepositoryContext definitionRepository)
    {
        if (checkoutDirectoryName is not null || !definitionRepository.IsResolved)
        {
            return false;
        }

        return HasSameDefinitionRepository(builder, repository, revision, definitionRepository);
    }

    private static bool HasSameDefinitionRepository(
        IDistributedApplicationBuilder builder,
        string repository,
        string? revision,
        ModuleRepositoryContext definitionRepository) =>
        RepositoryIdentity.AreEquivalent(
            repository,
            definitionRepository.Repository ?? definitionRepository.RepositoryPath,
            builder.AppHostDirectory) &&
        string.Equals(revision, definitionRepository.Revision, StringComparison.Ordinal);

    private static ModuleRepositoryContext ResolveDistinctBuildRepository(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string resourceName,
        string normalizedRepository,
        string? requestedRevision,
        string? checkoutDirectoryName,
        string checkoutDirectoryNameConfigurationKey,
        ModuleApplicationRegistry registry,
        DistributedApplicationModuleOptions? moduleOptions,
        bool allowMissingBuildRepository)
    {
        var isRemote = RepositoryIdentity.IsRemoteRepository(
            normalizedRepository,
            builder.AppHostDirectory);
        if (IsUnpinnedLocalRepository(isRemote, requestedRevision))
        {
            RejectLocalCheckoutDirectoryName(
                checkoutDirectoryName,
                checkoutDirectoryNameConfigurationKey);
            registry.RequireDirectory(
                module.Name,
                $"build repository for resource '{resourceName}'",
                normalizedRepository,
                requiredOnRun: !allowMissingBuildRepository,
                resourceName: resourceName);
            return new ModuleRepositoryContext(
                normalizedRepository,
                normalizedRepository,
                Revision: null,
                InitializerOwned: false,
                UsesModuleRepository: false);
        }

        var updateRepository = ResolveUpdateRepository(moduleOptions, registry);
        var requirement = registry.RegisterRepository(
            builder,
            module.Name,
            normalizedRepository,
            requestedRevision,
            updateRepository,
            requiredOnRun: !allowMissingBuildRepository,
            checkoutDirectoryName: checkoutDirectoryName,
            checkoutDirectoryNameConfigurationKey: checkoutDirectoryNameConfigurationKey,
            resourceName: resourceName,
            requirementName: $"{module.Name}/{resourceName} image");
        return new ModuleRepositoryContext(
            requirement.RepositoryPath,
            requirement.Repository,
            requirement.Revision,
            InitializerOwned: true,
            UsesModuleRepository: false);
    }

    private static string GetLocalDefinitionPath(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module,
        string? repository)
    {
        if (repository is null)
        {
            return GetDefaultDefinitionPath(builder, module);
        }

        if (!RepositoryIdentity.IsRemoteRepository(repository, builder.AppHostDirectory))
        {
            return Path.GetFullPath(repository, builder.AppHostDirectory);
        }

        return GetDefaultDefinitionPath(builder, module);
    }

    private static string GetDefaultDefinitionPath(
        IDistributedApplicationBuilder builder,
        DistributedApplicationModule module) =>
        module.ProjectDefinitions
            .Select(project => project.SourceRepositoryRoot)
            .OfType<string>()
            .Distinct(PathSafety.Comparer)
            .SingleOrDefault() ?? Path.GetFullPath(builder.AppHostDirectory);


    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetCheckoutDirectoryNameConfigurationKey(string moduleName) =>
        $"{DistributedApplicationModuleExtensions.GetModuleConfigurationKey(moduleName)}:" +
        $"{nameof(DistributedApplicationModuleOptions.CheckoutDirectoryName)}";

    private static string GetBuildRepositoryCheckoutDirectoryNameConfigurationKey(
        string moduleName,
        string resourceName,
        ModuleResourceKind resourceKind)
    {
        var collection = resourceKind == ModuleResourceKind.Project
            ? nameof(DistributedApplicationModuleOptions.Projects)
            : nameof(DistributedApplicationModuleOptions.Containers);
        return $"{DistributedApplicationModuleExtensions.GetModuleConfigurationKey(moduleName)}:" +
            $"{collection}:{resourceName}:{nameof(DistributedApplicationModuleImageOptions.CheckoutDirectoryName)}";
    }

    private static void RejectLocalCheckoutDirectoryName(string? value, string repositoryConfigurationKey)
    {
        if (value is null)
        {
            return;
        }

        var configurationKey = GetLocalCheckoutDirectoryConfigurationKey(repositoryConfigurationKey);
        throw new InvalidOperationException(
            $"Checkout directory name '{value}' from configuration key '{configurationKey}' is invalid: " +
            "CheckoutDirectoryName applies only to unpinned remote repositories; local-path repository behavior is unchanged.");
    }

    private static string GetLocalCheckoutDirectoryConfigurationKey(string repositoryConfigurationKey) =>
        System.Text.RegularExpressions.Regex.Replace(
            repositoryConfigurationKey,
            $":{nameof(DistributedApplicationModuleOptions.Repository)}$",
            $":{nameof(DistributedApplicationModuleOptions.CheckoutDirectoryName)}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed class DefinitionRepositorySettings(
        string? repository,
        string? revision,
        string? checkoutDirectoryName)
    {
        public string? Repository { get; } = repository;

        public string? Revision { get; } = revision;

        public string? CheckoutDirectoryName { get; } = checkoutDirectoryName;
    }
}
