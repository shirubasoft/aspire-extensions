using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task ComposeDeploymentRoutesToTheContainerTargetPort()
    {
        var api = new TestCloudflareApiClient
        {
            ExistingTunnel = new("deployed-tunnel-id", "public", "healthy", null, null),
        };
        var outputPath = Directory.CreateTempSubdirectory();
        try
        {
            var builder = DistributedApplication.CreateBuilder(
            [
                "--operation", "publish",
                "--step", "configure-public-cloudflare-routes",
                "--output-path", outputPath.FullName,
            ]);
            builder.Configuration["Parameters:public-account-id"] = "account-id";
            builder.Configuration["Parameters:public-api-token"] = "api-token";
            builder.Configuration["Parameters:public-tunnel-token"] = "tunnel-token";
            builder.AddDockerComposeEnvironment("env");
            var web = builder
                .AddContainer("web", "docker.io/traefik/whoami", "v1.10")
                .WithArgs("--port", "8080")
                .WithHttpEndpoint(targetPort: 8080, name: "http");
            var tunnel = builder.AddCloudflareTunnel("public");
            web.WithCloudflareTunnel(tunnel, "app.example.com");
            builder.Services.AddSingleton<ICloudflareApiClientFactory>(
                new TestCloudflareApiClientFactory(api));

            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            outputPath.Delete(recursive: true);
        }

        var configuration = Assert.IsType<TunnelConfiguration>(api.UpdatedConfiguration);
        Assert.Equal("http://web:8080", configuration.Ingress[0].Service);
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

        Assert.Equal(
            "http://web.internal",
            await expression.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeTargetPortFallsBackToTheContainerPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(port: 5000, name: "http"));

        var port = CloudflareRouteProvisioner.GetComposeTargetPortExpression(route);

        Assert.Equal(
            "5000",
            await port.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeTargetPortUsesTheDefaultProjectContainerPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddResource(new ProjectResource("api"))
            .WithHttpEndpoint(name: "http"));

        var port = CloudflareRouteProvisioner.GetComposeTargetPortExpression(route);

        Assert.Equal(
            "8080",
            await port.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ComposeTargetPortRejectsAnAllocatedPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var route = CreateRoute(builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(name: "http"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareRouteProvisioner.GetComposeTargetPortExpression(route));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
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

#pragma warning disable ASPIRECOMPUTE002
    private sealed class TestComputeEnvironmentResource(string name)
        : Resource(name), IComputeEnvironmentResource
    {
        public ReferenceExpression GetHostAddressExpression(
            EndpointReference endpointReference) =>
            ReferenceExpression.Create($"{endpointReference.Resource.Name}.internal");
    }
#pragma warning restore ASPIRECOMPUTE002
}
