using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace Aspire.Hosting;

/// <summary>
/// Adds a module project through one of Aspire's .NET project APIs. Materialization is independent of the
/// resource type; only adding the resource and converting it to a Dockerfile build differ.
/// </summary>
internal sealed record ModuleProjectBackend(
    Func<IDistributedApplicationBuilder, string, string, Action<ProjectResourceOptions>, IResourceBuilder<IResourceWithEndpoints>> AddProject,
    Action<IResourceBuilder<IResourceWithEndpoints>, Action<IResourceBuilder<ContainerResource>>> PublishAsDockerFile)
{
    internal static ModuleProjectBackend Project { get; } = new(
        static (builder, name, path, options) => builder.AddProject(name, path, options),
        static (resource, configureContainer) => As<ProjectResource>(resource).PublishAsDockerFile(configureContainer));

    internal static ModuleProjectBackend DotnetProject { get; } = new(
        static (builder, name, path, options) => builder.AddDotnetProject(name, path, options),
        static (resource, configureContainer) => As<DotnetProjectResource>(resource).PublishAsDockerFile(configureContainer));

    /// <summary>Returns a builder typed to the resource that the backend added.</summary>
    internal static IResourceBuilder<TResource> As<TResource>(IResourceBuilder<IResourceWithEndpoints> resource)
        where TResource : IResourceWithEndpoints =>
        resource.ApplicationBuilder.CreateResourceBuilder((TResource)resource.Resource);
}
