using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class CloudflareTunnelInstallerResource(
    [ResourceName] string name,
    CloudflareTunnelResource tunnel) : Resource(name), IResourceWithWaitSupport
{
    public CloudflareTunnelResource Tunnel { get; } = tunnel;

    public bool WasCreated { get; internal set; }
}
