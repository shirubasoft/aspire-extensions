using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Delegates a generated, strongly typed module API to its materialized module definition.
/// </summary>
public abstract class DistributedApplicationModuleReference : IDistributedApplicationModule
{
    private readonly IDistributedApplicationModule _module;

    /// <summary>Initializes a strongly typed reference to a materialized module.</summary>
    /// <param name="module">The module definition that provides resource lookups and contract metadata.</param>
    protected DistributedApplicationModuleReference(IDistributedApplicationModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _module = module;
    }

    internal IDistributedApplicationModule Module => _module;

    /// <inheritdoc />
    public string Name => _module.Name;

    /// <inheritdoc />
    public string Version => _module.Version;

    /// <inheritdoc />
    public string? PackageId => _module.PackageId;

    /// <inheritdoc />
    public IReadOnlyList<IDistributedApplicationModuleResource> Resources => _module.Resources;

    /// <inheritdoc />
    public IReadOnlyList<IDistributedApplicationModuleProject> Projects => _module.Projects;

    /// <inheritdoc />
    public IReadOnlyList<IDistributedApplicationModuleContainer> Containers => _module.Containers;

    /// <inheritdoc />
    public IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource => _module.GetResource<TResource>(name);
}
