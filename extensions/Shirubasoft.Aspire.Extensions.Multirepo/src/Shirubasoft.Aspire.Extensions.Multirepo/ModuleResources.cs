using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Associates a materialized resource with its module definition.</summary>
public sealed class DistributedApplicationModuleResourceAnnotation(
    string moduleName,
    string resourceName,
    string repositoryPath,
    bool imported,
    string? packageId = null) : IResourceAnnotation
{
    /// <summary>Gets the module name.</summary>
    public string ModuleName { get; } = moduleName;

    /// <summary>Gets the exported resource name.</summary>
    public string ResourceName { get; } = resourceName;

    /// <summary>Gets the module-definition worktree associated with the resource.</summary>
    public string RepositoryPath { get; } = repositoryPath;

    /// <summary>Gets whether the resource came from <c>ImportModule</c>.</summary>
    public bool Imported { get; } = imported;

    /// <summary>Gets the NuGet package ID that publishes the owning module contract, when declared.</summary>
    public string? PackageId { get; } = packageId;
}
