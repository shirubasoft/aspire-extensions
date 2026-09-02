using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Maps a remote container image reference to the effective local image reference of a resource during
/// <c>aspire do pull</c>.
/// </summary>
public sealed class ModuleImagePullMappingAnnotation : IResourceAnnotation
{
    /// <summary>Creates a pull mapping from <paramref name="remoteImageReference"/> to the resource image.</summary>
    public ModuleImagePullMappingAnnotation(string remoteImageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteImageReference);
        RemoteImageReference = remoteImageReference.Trim();
    }

    /// <summary>Gets the complete remote image reference that the container runtime pulls.</summary>
    public string RemoteImageReference { get; }
}

public static partial class DistributedApplicationModuleExtensions
{
    /// <summary>
    /// Pulls <paramref name="remoteImageReference"/> and tags it as the declared container's effective image when
    /// <c>aspire do pull</c> runs. This mapping does not affect image push behavior.
    /// </summary>
    public static IDistributedApplicationModuleContainerBuilder WithImagePullMapping(
        this IDistributedApplicationModuleContainerBuilder resource,
        string remoteImageReference)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteImageReference);
        var normalizedRemoteImageReference = remoteImageReference.Trim();
        return resource.Configure((_, container) =>
            container.WithImagePullMapping(normalizedRemoteImageReference));
    }

    /// <summary>
    /// Pulls <paramref name="remoteImageReference"/> and tags it as the resource's effective image when
    /// <c>aspire do pull</c> runs. This mapping does not affect image push behavior.
    /// </summary>
    public static IResourceBuilder<TResource> WithImagePullMapping<TResource>(
        this IResourceBuilder<TResource> resource,
        string remoteImageReference)
        where TResource : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.WithAnnotation(
            new ModuleImagePullMappingAnnotation(remoteImageReference),
            ResourceAnnotationMutationBehavior.Replace);
    }
}
