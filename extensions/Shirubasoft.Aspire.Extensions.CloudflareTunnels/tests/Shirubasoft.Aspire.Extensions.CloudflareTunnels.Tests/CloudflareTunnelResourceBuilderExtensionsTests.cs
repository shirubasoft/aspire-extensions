using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareTunnelResourceBuilderExtensionsTests
{
    [Fact]
    public void AddCloudflareTunnelConfiguresContainerInstallerAndCredentials()
    {
        var builder = DistributedApplication.CreateBuilder();

        var tunnel = builder.AddCloudflareTunnel("public", metricsPort: 16012);

        AssertContainer(tunnel.Resource, 16012);
        Assert.NotNull(tunnel.Resource.MetricsEndpoint);
        Assert.Contains(
            tunnel.Resource.Annotations,
            annotation => annotation is CloudflareTunnelCredentialsAnnotation);
        Assert.Contains(
            tunnel.Resource.Annotations,
            annotation => annotation is WaitAnnotation wait
                && wait.WaitType == WaitType.WaitForCompletion);

        var installer = Assert.Single(
            builder.Resources.OfType<CloudflareTunnelInstallerResource>());
        Assert.Same(tunnel.Resource, installer.Tunnel);
        Assert.Contains(
            installer.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => ReferenceEquals(relationship.Resource, tunnel.Resource));
        Assert.Contains(
            installer.Annotations,
            annotation => ReferenceEquals(
                annotation,
                ManifestPublishingCallbackAnnotation.Ignore));

        var parameters = builder.Resources.OfType<ParameterResource>().ToDictionary(resource => resource.Name);
        Assert.False(parameters["public-account-id"].Secret);
        Assert.True(parameters["public-api-token"].Secret);
    }

    [Fact]
    public void AddCloudflareQuickTunnelConfiguresADevelopmentOnlyContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var tunnel = builder.AddCloudflareQuickTunnel("quick", metricsPort: 16013);

        AssertContainer(tunnel.Resource, 16013);
        Assert.NotNull(tunnel.Resource.MetricsEndpoint);
        Assert.Contains(
            tunnel.Resource.Annotations,
            annotation => ReferenceEquals(
                annotation,
                ManifestPublishingCallbackAnnotation.Ignore));
    }

    [Fact]
    public void QuickTunnelReferenceSelectsOneEndpointAndBuildsArguments()
    {
        var builder = DistributedApplication.CreateBuilder();
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);
        var tunnel = builder
            .AddCloudflareQuickTunnel("quick")
            .WithReference(web);

        var arguments = new List<object>();
        CloudflareTunnelResourceBuilderExtensions.AddQuickTunnelArguments(
            new CommandLineArgsCallbackContext(
                arguments,
                tunnel.Resource,
                TestContext.Current.CancellationToken),
            tunnel.Resource);

        Assert.Equal(
            ["tunnel", "--no-autoupdate", "--metrics", "0.0.0.0:60123", "--url"],
            arguments.Take(5).Select(argument => argument.ToString()!).ToArray());
        var endpoint = Assert.IsType<EndpointReference>(arguments[5]);
        Assert.Same(web.Resource, endpoint.Resource);
        Assert.Equal("http", endpoint.EndpointName);
        Assert.Contains(
            tunnel.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => ReferenceEquals(relationship.Resource, web.Resource));

        Assert.Throws<InvalidOperationException>(() => tunnel.WithReference(web));
    }

    [Fact]
    public void WithCloudflareTunnelAddsRouteRelationshipAndDashboardUrl()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tunnel = builder.AddCloudflareTunnel("public");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        var result = web.WithCloudflareTunnel(tunnel, "app.example.com");

        Assert.Same(web, result);
        var route = Assert.Single(builder.Resources.OfType<PublishedRouteResource>());
        Assert.Equal("public-route-app-example-com", route.Name);
        Assert.Equal("app.example.com", route.Hostname);
        Assert.Same(web.Resource, route.TargetResource);
        Assert.Same(tunnel.Resource, route.Tunnel);
        Assert.Contains(
            route.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => ReferenceEquals(relationship.Resource, tunnel.Resource));
        Assert.Contains(
            tunnel.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => ReferenceEquals(relationship.Resource, web.Resource));
        Assert.Contains(
            tunnel.Resource.Annotations,
            annotation => annotation is ResourceUrlsCallbackAnnotation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BuilderMethodsRejectBlankNames(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tunnel = builder.AddCloudflareTunnel("public");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        Assert.Throws<ArgumentException>(() => builder.AddCloudflareTunnel(value));
        Assert.Throws<ArgumentException>(() => builder.AddCloudflareQuickTunnel(value));
        Assert.Throws<ArgumentException>(() => web.WithCloudflareTunnel(tunnel, value));
    }

    [Fact]
    public void PublishModeUsesAPreProvisionedTunnelToken()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);

        var tunnel = builder.AddCloudflareTunnel("public");

        Assert.Empty(builder.Resources.OfType<CloudflareTunnelInstallerResource>());
        var parameters = builder.Resources.OfType<ParameterResource>().ToDictionary(resource => resource.Name);
        Assert.True(parameters["public-tunnel-token"].Secret);
        Assert.DoesNotContain(
            tunnel.Resource.Annotations,
            annotation => annotation is WaitAnnotation);
    }

    private static void AssertContainer(ContainerResource resource, int expectedPort)
    {
        var image = Assert.Single(resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("docker.io", image.Registry);
        Assert.Equal("cloudflare/cloudflared", image.Image);
        Assert.Equal("2026.8.3", image.Tag);

        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("metrics", endpoint.Name);
        Assert.Equal(expectedPort, endpoint.Port);
        Assert.Equal(60123, endpoint.TargetPort);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
    }
}
