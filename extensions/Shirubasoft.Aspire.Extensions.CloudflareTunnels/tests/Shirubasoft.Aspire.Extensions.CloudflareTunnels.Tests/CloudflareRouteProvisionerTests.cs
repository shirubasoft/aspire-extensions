using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
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

    // Route configuration is not transactional.
    [Fact]
    public async Task FailedRouteKeepsEarlierDnsRecordsAndSkipsTheIngressUpdate()
    {
        var api = new TestCloudflareApiClient
        {
            ZoneResolver = name => name == "example.com"
                ? new("zone-id", name, "active")
                : null,
        };
        var (tunnel, route) = CreateRoute();
        var unknownZoneRoute = new PublishedRouteResource(
            "public-route-app-example-org",
            "app.example.org",
            route.TargetEndpoint,
            route.TargetResource,
            tunnel);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CloudflareRouteProvisioner(new TestCloudflareApiClientFactory(api))
                .ConfigureRoutesAsync(
                    tunnel,
                    [route, unknownZoneRoute],
                    _ => Task.FromResult("http://web:80"),
                    _ => NullLogger.Instance,
                    TestContext.Current.CancellationToken));

        Assert.Equal([("zone-id", "app.example.com", "tunnel-id")], api.DnsUpserts);
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

    [Theory]
    [InlineData(8080, "http://web:8080")]
    [InlineData(null, "http://web")]
    public async Task ComposeDeploymentRoutesToTheContainerTargetPort(
        int? targetPort,
        string expectedService)
    {
        var api = new TestCloudflareApiClient
        {
            ExistingTunnel = new("deployed-tunnel-id", "public", "healthy", null, null),
        };

        var pipeline = new TunnelDeploymentPipeline
        {
            Api = api,
            TargetPort = targetPort,
        };

        await pipeline.RunAsync(pipeline.RouteStepName);

        var configuration = Assert.IsType<TunnelConfiguration>(api.UpdatedConfiguration);
        Assert.Equal(expectedService, configuration.Ingress[0].Service);
    }

    [Fact]
    public async Task ServiceUrlKeepsTheComputeEnvironmentUrlOutsideCompose()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080, name: "http"));

        var expression = CloudflareRouteProvisioner.GetServiceUrlExpression(
            new TestComputeEnvironmentResource("env"),
            route);

        Assert.False(expression.HasUnknownTargetPort);
        Assert.Equal(
            "http://web.internal",
            await expression.Url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeServiceUrlFallsBackToTheContainerPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(port: 5000, name: "http"));

        var expression = CloudflareRouteProvisioner.GetServiceUrlExpression(
            new DockerComposeEnvironmentResource("env"),
            route);

        Assert.False(expression.HasUnknownTargetPort);
        Assert.Equal(
            "http://web:5000",
            await expression.Url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeServiceUrlUsesTheDefaultProjectContainerPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddResource(new ProjectResource("api"))
            .WithHttpEndpoint(name: "http"));

        var expression = CloudflareRouteProvisioner.GetServiceUrlExpression(
            new DockerComposeEnvironmentResource("env"),
            route);

        Assert.False(expression.HasUnknownTargetPort);
        Assert.Equal(
            "http://api:8080",
            await expression.Url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeServiceUrlKeepsTheDefaultUrlForAnAllocatedPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(name: "http"));

        var expression = CloudflareRouteProvisioner.GetServiceUrlExpression(
            new DockerComposeEnvironmentResource("env"),
            route);

        Assert.True(expression.HasUnknownTargetPort);
        Assert.Equal(
            "http://web",
            await expression.Url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportUnknownTargetPortWarnsAboutTheRoute()
    {
        var (_, route) = CreateRoute();
        var warnings = new List<(PublishedRouteResource Route, string Warning)>();

        await CloudflareRouteProvisioner.ReportUnknownTargetPortAsync(
            new(ReferenceExpression.Empty, HasUnknownTargetPort: true),
            route,
            "http://web",
            (warnedRoute, warning, _) =>
            {
                warnings.Add((warnedRoute, warning));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var (warnedRoute, warning) = Assert.Single(warnings);
        Assert.Same(route, warnedRoute);
        Assert.Contains("'http'", warning, StringComparison.Ordinal);
        Assert.Contains("'web'", warning, StringComparison.Ordinal);
        Assert.Contains("app.example.com routes to http://web", warning, StringComparison.Ordinal);
        Assert.Contains("target port", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportUnknownTargetPortSkipsAKnownPort()
    {
        var (_, route) = CreateRoute();
        var warned = false;

        await CloudflareRouteProvisioner.ReportUnknownTargetPortAsync(
            new(ReferenceExpression.Empty),
            route,
            "http://web:80",
            (_, _, _) =>
            {
                warned = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.False(warned);
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
        var route = CreateRoute(builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80, name: "http"));

        return (route.Tunnel, route);
    }

    private static PublishedRouteResource CreateRoute<T>(IResourceBuilder<T> target)
        where T : IResourceWithEndpoints =>
        new(
            "public-route-app-example-com",
            "app.example.com",
            target.GetEndpoint(
                "http",
                KnownNetworkIdentifiers.DefaultAspireContainerNetwork),
            target.Resource,
            new CloudflareTunnelResource("public")
            {
                TunnelId = "tunnel-id",
            });
}
