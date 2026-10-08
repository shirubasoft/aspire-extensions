namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a Tailscale tailnet that resources join through sidecar nodes.
/// </summary>
/// <param name="name">The resource name.</param>
/// <param name="oauthClientSecret">The OAuth client secret that registers sidecar nodes.</param>
/// <param name="tags">The tags that sidecar nodes advertise by default.</param>
public sealed class TailnetResource(
    [ResourceName] string name,
    ParameterResource oauthClientSecret,
    IReadOnlyList<string> tags) : Resource(name)
{
    /// <summary>
    /// Gets the OAuth client secret parameter used as the node auth key.
    /// </summary>
    public ParameterResource OAuthClientSecret { get; } = oauthClientSecret;

    /// <summary>
    /// Gets the tags that sidecar nodes advertise unless a resource overrides them.
    /// </summary>
    public IReadOnlyList<string> Tags { get; } = tags;
}
