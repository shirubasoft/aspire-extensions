using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class DistributedApplicationModule(
    IDistributedApplicationBuilder definitionApplicationBuilder,
    string name,
    string version,
    string? packageId) : IDistributedApplicationModule
{
    private readonly List<IDistributedApplicationModuleResource> _resources = [];
    private readonly List<DistributedApplicationModuleProject> _projects = [];
    private readonly List<DistributedApplicationModuleContainer> _containers = [];
    private readonly List<DistributedApplicationModuleComposition> _compositions = [];
    private readonly Dictionary<string, IResource> _materializedResources =
        new(StringComparer.OrdinalIgnoreCase);
    private IDistributedApplicationBuilder? _materializedApplicationBuilder;

    public string Name { get; } = name;

    public string Version { get; } = version;

    public string? PackageId { get; } = packageId;

    public IReadOnlyList<IDistributedApplicationModuleResource> Resources => _resources;

    public IReadOnlyList<IDistributedApplicationModuleProject> Projects => _projects;

    public IReadOnlyList<IDistributedApplicationModuleContainer> Containers => _containers;

    internal IReadOnlyList<DistributedApplicationModuleProject> ProjectDefinitions => _projects;

    internal IReadOnlyList<DistributedApplicationModuleContainer> ContainerDefinitions => _containers;

    internal IReadOnlyList<IDistributedApplicationModuleResource> ResourceDefinitions => _resources;

    internal IReadOnlyList<DistributedApplicationModuleComposition> Compositions => _compositions;

    internal string? Repository { get; set; }

    internal string? RepositoryRevision { get; set; }

    internal string? CheckoutDirectoryName { get; set; }

    internal IDistributedApplicationBuilder DefinitionApplicationBuilder { get; } = definitionApplicationBuilder;

    internal bool RequiresRepositoryContent { get; set; }

    internal bool ExplicitlyRequiresRepositoryContent { get; set; }

    internal void AddProject(DistributedApplicationModuleProject project)
    {
        ThrowIfNameIsAlreadyUsed(project.Name);
        _projects.Add(project);
        _resources.Add(project);
    }

    internal void AddContainer(DistributedApplicationModuleContainer container)
    {
        ThrowIfNameIsAlreadyUsed(container.Name);
        _containers.Add(container);
        _resources.Add(container);
    }

    internal void AddResource<TResource>(DistributedApplicationModuleResource<TResource> resource)
        where TResource : IResource
    {
        ThrowIfNameIsAlreadyUsed(resource.Name);
        _resources.Add(resource);
    }

    internal void AddComposition(DistributedApplicationModuleComposition composition)
    {
        _compositions.Add(composition);
    }

    public IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = GetMaterializedApplicationBuilder();
        var resource = GetMaterializedResource(name);
        return builder.CreateResourceBuilder(RequireResourceType<TResource>(name, resource));
    }

    private IDistributedApplicationBuilder GetMaterializedApplicationBuilder() =>
        _materializedApplicationBuilder ?? throw new InvalidOperationException(
            $"Module '{Name}' has not been materialized. Call AddModule(module) or ImportModule('{Name}') first.");

    private IResource GetMaterializedResource(string name)
    {
        if (!_materializedResources.TryGetValue(name, out var resource))
        {
            throw new KeyNotFoundException(
                $"Module '{Name}' does not contain a materialized resource named '{name}'.");
        }

        return resource;
    }

    private static TResource RequireResourceType<TResource>(string name, IResource resource)
        where TResource : IResource =>
        resource is TResource typedResource
            ? typedResource
            : throw new InvalidOperationException(
                $"Module resource '{name}' is '{resource.GetType().Name}', not '{typeof(TResource).Name}'.");

    internal void TrackMaterializedResource(
        IDistributedApplicationBuilder builder,
        string declaredName,
        IResource resource)
    {
        _materializedApplicationBuilder = builder;
        _materializedResources[declaredName] = resource;
    }

    internal void Validate()
    {
        ValidateResourceDefinitions();

        var appHostDirectory = Path.GetFullPath(DefinitionApplicationBuilder.AppHostDirectory);
        foreach (var project in _projects)
        {
            project.SourceRepositoryRoot = ResolveProjectRepositoryRoot(project, appHostDirectory);
        }

        ValidateRepositoryRoots();
    }

    private string? ResolveProjectRepositoryRoot(
        DistributedApplicationModuleProject project,
        string appHostDirectory)
    {
        if (project.PathBase == ModuleProjectPathBase.Repository)
        {
            return GetDefinitionRepositoryRoot(Repository, appHostDirectory);
        }

        var detectedRoot = RepositoryIdentity.FindRepositoryRoot(project.ProjectPath);
        var configuredRoot = TryGetConfiguredLocalRepositoryRoot(
            Repository,
            appHostDirectory,
            project.ProjectPath);
        return configuredRoot ?? ResolveDetectedRepositoryRoot(
            detectedRoot,
            appHostDirectory,
            project.ProjectPath);
    }

    private static string ResolveDetectedRepositoryRoot(
        string detectedRoot,
        string appHostDirectory,
        string projectPath)
    {
        if (RepositoryIdentity.TryFindRepositoryRoot(detectedRoot) is not null)
        {
            return detectedRoot;
        }

        return PathSafety.IsContainedBy(appHostDirectory, projectPath)
            ? appHostDirectory
            : detectedRoot;
    }

    internal IResourceBuilder<TResource> GetResourceForCallback<TResource>(
        string name,
        string requestingResourceName)
        where TResource : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = GetCallbackResource(name, requestingResourceName);
        var typedResource = RequireResourceType<TResource>(name, resource);
        return _materializedApplicationBuilder!.CreateResourceBuilder(typedResource);
    }

    private IResource GetCallbackResource(string name, string requestingResourceName)
    {
        if (_materializedResources.TryGetValue(name, out var resource))
        {
            return resource;
        }

        ThrowCallbackResourceNotFound(name, requestingResourceName);
        throw new InvalidOperationException("Unreachable code.");
    }

    private void ThrowCallbackResourceNotFound(string name, string requestingResourceName)
    {
        if (_resources.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Module resource '{requestingResourceName}' cannot resolve '{name}' because module resources " +
                "are materialized in declaration order. Declare the dependency before the consuming resource.");
        }

        throw new KeyNotFoundException($"Module '{Name}' does not declare a resource named '{name}'.");
    }

    private void ValidateResourceDefinitions()
    {
        ValidateHasContent();
        ValidateAllProjectsAreExported();
    }

    private void ValidateHasContent()
    {
        if (_resources.Count == 0)
        {
            if (_compositions.Count == 0)
            {
                throw new InvalidOperationException($"Module '{Name}' does not contain any resources or modules.");
            }
        }
    }

    private void ValidateAllProjectsAreExported()
    {
        var notExported = _projects.FirstOrDefault(project => !project.IsExportedAsContainer);
        if (notExported is not null)
        {
            throw new InvalidOperationException(
                $"Project '{notExported.Name}' in module '{Name}' must call ExportAsContainer().");
        }
    }

    private string[] ValidateRepositoryRoots()
    {
        var repositoryRoots = _projects
            .Select(project => project.SourceRepositoryRoot)
            .Where(repositoryRoot => repositoryRoot is not null)
            .Select(repositoryRoot => repositoryRoot!)
            .Distinct(PathSafety.Comparer)
            .ToArray();

        if (repositoryRoots.Length > 1)
        {
            throw new InvalidOperationException(
                $"All projects in module '{Name}' must belong to the same Git repository or source tree.");
        }

        return repositoryRoots;
    }

    private static string? GetDefinitionRepositoryRoot(
        string? repository,
        string appHostDirectory)
    {
        if (!string.IsNullOrWhiteSpace(repository) &&
            !RepositoryIdentity.IsRemoteRepository(repository, appHostDirectory))
        {
            return Path.GetFullPath(repository, appHostDirectory);
        }

        return null;
    }

    private static string? TryGetConfiguredLocalRepositoryRoot(
        string? repository,
        string appHostDirectory,
        string projectPath)
    {
        if (!HasConfiguredLocalRepository(repository, appHostDirectory))
        {
            return null;
        }

        var candidate = Path.GetFullPath(repository!, appHostDirectory);

        return PathSafety.IsContainedBy(candidate, projectPath)
            ? candidate
            : null;
    }

    private static bool HasConfiguredLocalRepository(string? repository, string appHostDirectory)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        return !RepositoryIdentity.IsRemoteRepository(repository, appHostDirectory);
    }

    private void ThrowIfNameIsAlreadyUsed(string name)
    {
        if (_resources.Any(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Module '{Name}' already contains a resource named '{name}'.");
        }
    }
}

internal sealed record DistributedApplicationModuleComposition(
    DistributedApplicationModule Module,
    bool Imported,
    ModuleImportOptions? ImportOptions);

internal sealed class DistributedApplicationModuleProject(
    string name,
    string projectPath,
    ModuleProjectPathBase pathBase,
    string? sourceRepositoryRoot) : IDistributedApplicationModuleProject
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(IResourceWithEndpoints);

    public string ProjectPath { get; } = projectPath;

    internal ModuleProjectPathBase PathBase { get; } = pathBase;

    public bool IsExportedAsContainer => Export is not null;

    internal string? SourceRepositoryRoot { get; set; } = sourceRepositoryRoot;

    internal Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ProjectResource>>? ConfigureProject { get; set; }

    internal string GetRepositoryRelativeProjectPath()
    {
        return PathBase == ModuleProjectPathBase.Repository
            ? ProjectPath
            : Path.GetRelativePath(SourceRepositoryRoot!, ProjectPath);
    }

    internal ModuleContainerExport Export => _export
        ?? throw new InvalidOperationException($"Project '{Name}' has not been exported as a container.");

    private ModuleContainerExport? _export;

    internal void SetExport(ModuleContainerExport export)
    {
        _export = export;
    }
}

internal sealed record ModuleContainerExport(
    string ImageName,
    ModuleImageCommandOptions? CommandOptions,
    Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? ConfigureContainer);

internal sealed class DistributedApplicationModuleContainer(
    string name,
    string image,
    string tag) : IDistributedApplicationModuleContainer
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(ContainerResource);

    public string Image { get; } = image;

    public string Tag { get; } = tag;

    internal Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? ConfigureContainer { get; set; }

    internal ModuleImageCommandOptions? ImagePublishOptions { get; private set; }

    internal void SetImagePublishOptions(ModuleImageCommandOptions options)
    {
        if (ImagePublishOptions is not null)
        {
            throw new InvalidOperationException(
                $"Container '{Name}' already has an image publish command.");
        }

        ImagePublishOptions = options;
    }
}

internal interface IDistributedApplicationModuleFactoryResource : IDistributedApplicationModuleResource
{
    ModuleImageCommandOptions? ImagePublishOptions { get; }

    IResource Materialize(
        IDistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation annotation);
}

internal sealed class DistributedApplicationModuleResource<TResource>(
    string name,
    Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory,
    ModuleImageCommandOptions? imagePublishOptions)
    : IDistributedApplicationModuleFactoryResource
    where TResource : IResource
{
    public string Name { get; } = name;

    public Type ResourceType => typeof(TResource);

    public ModuleImageCommandOptions? ImagePublishOptions { get; } = imagePublishOptions;

    public IResource Materialize(
        IDistributedApplicationModuleResourceContext context,
        DistributedApplicationModuleResourceAnnotation annotation)
    {
        var resource = resourceFactory(context)
            ?? throw new InvalidOperationException($"The factory for module resource '{Name}' returned null.");

        if (!string.Equals(resource.Resource.Name, context.ResourceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The factory for module resource '{Name}' returned a resource named '{resource.Resource.Name}'. " +
                "Use context.ResourceName when creating the resource.");
        }

        resource.WithAnnotation(annotation);
        return resource.Resource;
    }
}

internal sealed class DistributedApplicationModuleResourceContext(
    IDistributedApplicationBuilder applicationBuilder,
    DistributedApplicationModule module,
    string resourceName,
    string repositoryPath,
    bool imported,
    ModuleResourceImage? image = null) : IDistributedApplicationModuleResourceContext
{
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

    public string ResourceName { get; } = resourceName;

    public string RepositoryPath { get; } = repositoryPath;

    public bool Imported { get; } = imported;

    public ModuleResourceImage? Image { get; } = image;

    public IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource => module.GetResourceForCallback<TResource>(name, ResourceName);
}
