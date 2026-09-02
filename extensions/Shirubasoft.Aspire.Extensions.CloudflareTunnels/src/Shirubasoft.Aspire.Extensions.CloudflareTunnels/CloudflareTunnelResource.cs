using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a Cloudflare Tunnel backed by a cloudflared container.
/// </summary>
public sealed class CloudflareTunnelResource([ResourceName] string name) : ContainerResource(name)
{
    private EndpointReference? _metricsEndpoint;

    /// <summary>
    /// Gets the cloudflared metrics endpoint.
    /// </summary>
    public EndpointReference MetricsEndpoint =>
        _metricsEndpoint ??= new(this, CloudflareTunnelContainerDefaults.MetricsEndpointName);

    /// <summary>
    /// Gets or sets the Cloudflare tunnel ID (UUID) after creation/discovery.
    /// </summary>
    public string? TunnelId { get; internal set; }

    internal string? TunnelToken { get; set; }
}
