using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Adds a remote image reference that <c>aspire do push</c> tags and pushes from the effective local image of a
/// resource with an image publish command.
/// </summary>
public sealed class ModuleImagePushTargetAnnotation : IResourceAnnotation
{
    /// <summary>Creates a push target for <paramref name="remoteImageReference"/>.</summary>
    /// <exception cref="ArgumentException">The reference is empty or names a digest instead of a tag.</exception>
    public ModuleImagePushTargetAnnotation(string remoteImageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteImageReference);
        var reference = remoteImageReference.Trim();
        if (reference.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Image push target '{reference}' names a digest. Use a tag reference such as " +
                "'registry.example.com/team/api:1.0', or omit the tag to reuse the local image's tag.",
                nameof(remoteImageReference));
        }

        RemoteImageReference = reference;
    }

    /// <summary>
    /// Gets the remote image reference that receives the image. A reference without a tag reuses the local image's tag.
    /// </summary>
    public string RemoteImageReference { get; }
}

public static partial class DistributedApplicationModuleExtensions
{
    /// <summary>
    /// Also pushes the declared container's image to <paramref name="remoteImageReference"/> after its registry target
    /// when <c>aspire do push</c> runs. Call this method once for each target. The container needs a registry and an
    /// image publish command, such as <see cref="IDistributedApplicationModuleContainerBuilder.WithImagePublishCommand"/>.
    /// A reference without a tag reuses the local image's tag, and registry settings never rewrite the reference.
    /// A consumer that runs the image without publishing it ignores the target.
    /// </summary>
    public static IDistributedApplicationModuleContainerBuilder WithImagePushTarget(
        this IDistributedApplicationModuleContainerBuilder resource,
        string remoteImageReference)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var target = new ModuleImagePushTargetAnnotation(remoteImageReference);
        return resource.Configure((_, container) => container.WithAnnotation(target));
    }

    /// <summary>
    /// Also pushes the resource's image to <paramref name="remoteImageReference"/> after its registry target when
    /// <c>aspire do push</c> runs. Call this method once for each target. The resource must be a module container with
    /// a registry and an image publish command.
    /// A reference without a tag reuses the local image's tag, and registry settings never rewrite the reference.
    /// A resource that does not publish its image ignores the target.
    /// </summary>
    public static IResourceBuilder<TResource> WithImagePushTarget<TResource>(
        this IResourceBuilder<TResource> resource,
        string remoteImageReference)
        where TResource : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.WithAnnotation(new ModuleImagePushTargetAnnotation(remoteImageReference));
    }
}
