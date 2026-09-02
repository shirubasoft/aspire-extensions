using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareRouteProvisionerTests
{
    [Fact]
    public async Task ConfigureRoutesUpsertsDnsAndPreservesUnmanagedIngress()
    {
        var api = new TestCloudflareApiClient
        {
            Configuration = new TunnelConfiguration
            {
                Ingress =
                [
                    new IngressRule
                    {
                        Hostname = "legacy.example.net",
                        Service = "http://legacy:80",
                    },
                    new IngressRule
                    {
                        Hostname = "app.example.com",
                        Service = "http://old:80",
                    },
                    new IngressRule
                    {
                        Service = "http_status:404",
                    },
                ],
            },
            ZoneResolver = name => name == "example.com"
                ? new("zone-id", name, "active")
                : null,
        };
        var (tunnel, route) = CreateRoute();
        var provisioner = new CloudflareRouteProvisioner(
            new TestCloudflareApiClientFactory(api));

        await provisioner.ConfigureRoutesAsync(
            tunnel,
            [route],
            _ => Task.FromResult("http://web:80"),
            _ => NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.True(route.DnsRecordCreated);
        Assert.Equal(
            [("zone-id", "app.example.com", "tunnel-id")],
            api.DnsUpserts);
        Assert.Equal(["app.example.com", "example.com"], api.ZoneLookups);
        var configuration = Assert.IsType<TunnelConfiguration>(api.UpdatedConfiguration);
        Assert.Collection(
            configuration.Ingress,
            rule =>
            {
                Assert.Equal("legacy.example.net", rule.Hostname);
                Assert.Equal("http://legacy:80", rule.Service);
            },
            rule =>
            {
                Assert.Equal("app.example.com", rule.Hostname);
                Assert.Contains("web", rule.Service, StringComparison.Ordinal);
            },
            rule =>
            {
                Assert.Null(rule.Hostname);
                Assert.Equal("http_status:404", rule.Service);
            });
        Assert.True(api.IsDisposed);
    }

    [Fact]
    public async Task ConfigureRoutesFailsWhenNoZoneOwnsTheHostname()
    {
        var api = new TestCloudflareApiClient
        {
            ZoneResolver = _ => null,
        };
        var (tunnel, route) = CreateRoute();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CloudflareRouteProvisioner(new TestCloudflareApiClientFactory(api))
                .ConfigureRoutesAsync(
                    tunnel,
                    [route],
                    _ => Task.FromResult("http://web:80"),
                    _ => NullLogger.Instance,
                    TestContext.Current.CancellationToken));

        Assert.Contains("app.example.com", exception.Message, StringComparison.Ordinal);
        Assert.False(route.DnsRecordCreated);
        Assert.Null(api.UpdatedConfiguration);
    }

    [Fact]
    public async Task ConfigurePipelineRoutesFindsTheExistingTunnel()
    {
        var api = new TestCloudflareApiClient
        {
            ExistingTunnel = new(
                "deployed-tunnel-id",
                "public",
                "healthy",
                null,
                null),
        };
        var (tunnel, route) = CreateRoute();
        var provisioner = new CloudflareRouteProvisioner(
            new TestCloudflareApiClientFactory(api));

        await provisioner.ConfigureRoutesForPipelineAsync(
            tunnel,
            [route],
            (_, _) => Task.FromResult("https://deployed.example.net"),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("deployed-tunnel-id", tunnel.TunnelId);
        Assert.Equal(
            [("zone-id", "app.example.com", "deployed-tunnel-id")],
            api.DnsUpserts);
        var configuration = Assert.IsType<TunnelConfiguration>(
            api.UpdatedConfiguration);
        Assert.Equal(
            "https://deployed.example.net",
            configuration.Ingress[0].Service);
    }

    [Fact]
    public void RequireTunnelIdRejectsAnUnprovisionedTunnel()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareRouteProvisioner.RequireTunnelId(
                new CloudflareTunnelResource("public")));

        Assert.Contains("public", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireExistingTunnelRejectsAMissingDeploymentTunnel()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareRouteProvisioner.RequireExistingTunnel(null, "public"));

        Assert.Contains("public", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireServiceUrlRejectsAnUnresolvedEndpoint()
    {
        var (_, route) = CreateRoute();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareRouteProvisioner.RequireServiceUrl(null, route));

        Assert.Contains("web", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveManagedIngressRulesRemovesDeclaredRoutesAndCatchAll()
    {
        var (_, route) = CreateRoute();
        var configuration = new TunnelConfiguration
        {
            Ingress =
            [
                new IngressRule
                {
                    Hostname = "APP.EXAMPLE.COM",
                    Service = "http://old",
                },
                new IngressRule
                {
                    Hostname = "other.example.com",
                    Service = "http://other",
                },
                new IngressRule
                {
                    Service = "http_status:404",
                },
            ],
        };

        CloudflareRouteProvisioner.RemoveManagedIngressRules(configuration, [route]);

        var remaining = Assert.Single(configuration.Ingress);
        Assert.Equal("other.example.com", remaining.Hostname);
    }

    [Fact]
    public async Task FindZoneChecksParentDomains()
    {
        var api = new TestCloudflareApiClient
        {
            ZoneResolver = name => name == "example.com"
                ? new("zone-id", name, "active")
                : null,
        };

        var zone = await CloudflareRouteProvisioner.FindZoneAsync(
            api,
            "api.dev.example.com.",
            TestContext.Current.CancellationToken);

        Assert.NotNull(zone);
        Assert.Equal(
            ["api.dev.example.com", "dev.example.com", "example.com"],
            api.ZoneLookups);
    }

    private static (CloudflareTunnelResource Tunnel, PublishedRouteResource Route) CreateRoute()
    {
        var builder = DistributedApplication.CreateBuilder();
        var target = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80, name: "http");
        var tunnel = new CloudflareTunnelResource("public")
        {
            TunnelId = "tunnel-id",
        };
        var route = new PublishedRouteResource(
            "public-route-app-example-com",
            "app.example.com",
            target.GetEndpoint(
                "http",
                KnownNetworkIdentifiers.DefaultAspireContainerNetwork),
            target.Resource,
            tunnel);

        return (tunnel, route);
    }
}
