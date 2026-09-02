using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class PublishedRouteResource(
    [ResourceName] string name,
    string hostname,
    EndpointReference targetEndpoint,
    IResource targetResource,
    CloudflareTunnelResource tunnel)
    : Resource(name), IResourceWithWaitSupport
{
    public string Hostname { get; } = hostname;

    public EndpointReference TargetEndpoint { get; } = targetEndpoint;

    public IResource TargetResource { get; } = targetResource;

    public CloudflareTunnelResource Tunnel { get; } = tunnel;

    public bool DnsRecordCreated { get; internal set; }
}
