using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A logical parent used to group resources in the Aspire resource graph.
/// </summary>
/// <param name="name">The resource group name.</param>
public sealed class ResourceGroupResource([ResourceName] string name) : Resource(name);
