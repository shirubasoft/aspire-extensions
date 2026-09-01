using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Adds logical resource groups to Aspire applications.
/// </summary>
public static class ResourceGroupBuilderExtensions
{
    /// <summary>
    /// Adds a logical resource group whose builder can register resources through standard Aspire extension methods.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource group name.</param>
    /// <returns>A builder that registers resources as children of the group.</returns>
    public static IResourceGroupBuilder AddResourceGroup(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = builder
            .AddResource(new ResourceGroupResource(name))
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Resource group",
                State = KnownResourceStates.Running,
                Properties = [],
            });

        return new ResourceGroupBuilder(resource);
    }
}
