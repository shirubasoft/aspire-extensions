using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Builds a logical resource group and registers resources within it.
/// </summary>
public interface IResourceGroupBuilder :
    IDistributedApplicationBuilder,
    IResourceBuilder<ResourceGroupResource>;
