using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareTunnelProvisionerTests
{
    [Fact]
    public async Task ProvisionerCreatesAMissingTunnelAndRetrievesItsToken()
    {
        var api = new TestCloudflareApiClient
        {
            ExistingTunnel = null,
            CreatedTunnel = new("new-id", "ignored", "inactive", null, null),
            TunnelToken = "secret-token",
        };
        var installer = CreateInstaller();

        await new CloudflareTunnelProvisioner(new TestCloudflareApiClientFactory(api))
            .ProvisionAsync(
                installer,
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

        Assert.True(installer.WasCreated);
        Assert.Equal(1, api.CreateTunnelCallCount);
        Assert.Equal("new-id", installer.Tunnel.TunnelId);
        Assert.Equal("secret-token", installer.Tunnel.TunnelToken);
        Assert.True(api.IsDisposed);
    }

    [Fact]
    public async Task ProvisionerReusesAnExistingTunnel()
    {
        var api = new TestCloudflareApiClient
        {
            ExistingTunnel = new("existing-id", "public", "healthy", null, null),
        };
        var installer = CreateInstaller();

        await new CloudflareTunnelProvisioner(new TestCloudflareApiClientFactory(api))
            .ProvisionAsync(
                installer,
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

        Assert.False(installer.WasCreated);
        Assert.Equal(0, api.CreateTunnelCallCount);
        Assert.Equal("existing-id", installer.Tunnel.TunnelId);
    }

    [Fact]
    public async Task ProvisionerPropagatesCloudflareFailures()
    {
        var failure = new InvalidOperationException("Cloudflare unavailable");
        var api = new TestCloudflareApiClient
        {
            FindTunnelException = failure,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CloudflareTunnelProvisioner(new TestCloudflareApiClientFactory(api))
                .ProvisionAsync(
                    CreateInstaller(),
                    NullLogger.Instance,
                    TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.True(api.IsDisposed);
    }

    private static CloudflareTunnelInstallerResource CreateInstaller()
    {
        var tunnel = new CloudflareTunnelResource("public");
        return new CloudflareTunnelInstallerResource("public-installer", tunnel);
    }
}
