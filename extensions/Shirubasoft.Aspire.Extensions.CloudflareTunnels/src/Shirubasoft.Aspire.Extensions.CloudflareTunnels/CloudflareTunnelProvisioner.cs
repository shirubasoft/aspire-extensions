using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class CloudflareTunnelProvisioner(ICloudflareApiClientFactory clientFactory)
{
    public async Task ProvisionAsync(
        CloudflareTunnelInstallerResource installer,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installer);

        using var client = await clientFactory
            .CreateAsync(installer.Tunnel, cancellationToken)
            .ConfigureAwait(false);

        var tunnelInfo = await client
            .FindTunnelByNameAsync(installer.Tunnel.Name, cancellationToken)
            .ConfigureAwait(false);

        if (tunnelInfo is null)
        {
            logger.LogInformation(
                "Creating Cloudflare tunnel '{TunnelName}'.",
                installer.Tunnel.Name);
            tunnelInfo = await client
                .CreateTunnelAsync(installer.Tunnel.Name, cancellationToken)
                .ConfigureAwait(false);
            installer.WasCreated = true;
        }
        else
        {
            logger.LogInformation(
                "Using Cloudflare tunnel '{TunnelName}' with ID {TunnelId}.",
                installer.Tunnel.Name,
                tunnelInfo.Id);
        }

        installer.Tunnel.TunnelId = tunnelInfo.Id;
        installer.Tunnel.TunnelToken = await client
            .GetTunnelTokenAsync(tunnelInfo.Id, cancellationToken)
            .ConfigureAwait(false);
    }
}
