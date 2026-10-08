namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a Tailscale node container that serves one resource endpoint on a tailnet.
/// </summary>
/// <param name="name">The resource name.</param>
/// <param name="tailnet">The tailnet that the node joins.</param>
/// <param name="target">The resource that the node serves.</param>
/// <param name="targetEndpoint">The endpoint that receives tailnet traffic.</param>
/// <param name="hostname">The node hostname on the tailnet.</param>
/// <param name="tags">The tags that the node advertises.</param>
public sealed class TailscaleSidecarResource(
    [ResourceName] string name,
    TailnetResource tailnet,
    IResourceWithEndpoints target,
    EndpointReference targetEndpoint,
    string hostname,
    IReadOnlyList<string> tags) : ContainerResource(name)
{
    /// <summary>
    /// Gets the tailnet that the node joins.
    /// </summary>
    public TailnetResource Tailnet { get; } = tailnet;

    /// <summary>
    /// Gets the resource that the node serves.
    /// </summary>
    public IResourceWithEndpoints Target { get; } = target;

    /// <summary>
    /// Gets the endpoint that receives tailnet traffic.
    /// </summary>
    public EndpointReference TargetEndpoint { get; } = targetEndpoint;

    /// <summary>
    /// Gets the node hostname on the tailnet.
    /// </summary>
    public string Hostname { get; } = hostname;

    /// <summary>
    /// Gets the tags that the node advertises.
    /// </summary>
    public IReadOnlyList<string> Tags { get; } = tags;
}
