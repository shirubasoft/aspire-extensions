using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class TailscaleServeConfigTests
{
    [Fact]
    public void CreateServesHttpsOn443AndProxiesToTheTarget()
    {
        var json = TailscaleServeConfig.Create("http://web:8080", "${TS_CERT_DOMAIN}");

        var config = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.True(config["TCP"]!["443"]!["HTTPS"]!.GetValue<bool>());
        Assert.Equal(
            "http://web:8080",
            config["Web"]!["${TS_CERT_DOMAIN}:443"]!["Handlers"]!["/"]!["Proxy"]!.GetValue<string>());
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void CreateKeepsTheComposeEscapedCertDomainVerbatim()
    {
        var json = TailscaleServeConfig.Create(
            "http://api:8080",
            TailscaleServeConfig.ComposeCertDomainPlaceholder);

        Assert.Contains("\"$${TS_CERT_DOMAIN}:443\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeUrlUsesTheExplicitContainerTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080, name: "http");

        var url = TailscaleProxyTarget.GetComposeUrl(web.Resource, "http");

        Assert.Equal("http://web:8080", await url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeUrlFallsBackToTheContainerHostPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(port: 5000, name: "http");

        var url = TailscaleProxyTarget.GetComposeUrl(web.Resource, "http");

        Assert.Equal("http://web:5000", await url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ComposeUrlUsesTheDefaultProjectContainerPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder
            .AddResource(new ProjectResource("api"))
            .WithHttpEndpoint(name: "http");

        var url = TailscaleProxyTarget.GetComposeUrl(api.Resource, "http");

        Assert.Equal("http://api:8080", await url.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ComposeUrlRejectsAnAllocatedTargetPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(name: "http");

        var exception = Assert.Throws<InvalidOperationException>(
            () => TailscaleProxyTarget.GetComposeUrl(web.Resource, "http"));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'http'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("target port", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeUrlRejectsAMissingEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080, name: "http");

        var exception = Assert.Throws<InvalidOperationException>(
            () => TailscaleProxyTarget.GetComposeUrl(web.Resource, "admin"));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'admin'", exception.Message, StringComparison.Ordinal);
    }
}
