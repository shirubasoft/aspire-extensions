using Xunit;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareApiClientFactoryTests
{
    [Theory]
    [InlineData(null, "account")]
    [InlineData("", "account")]
    [InlineData("token", null)]
    [InlineData("token", " ")]
    public void RequireCredentialsRejectsMissingValues(
        string? apiToken,
        string? accountId)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareApiClientFactory.RequireCredentials(
                apiToken,
                accountId,
                "public"));

        Assert.Contains("public", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireCredentialsReturnsValidatedValues()
    {
        var credentials = CloudflareApiClientFactory.RequireCredentials(
            "token",
            "account",
            "public");

        Assert.Equal("token", credentials.ApiToken);
        Assert.Equal("account", credentials.AccountId);
    }

    [Fact]
    public void RequireCredentialsAnnotationRejectsAnUnconfiguredTunnel()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareApiClientFactory.RequireCredentialsAnnotation(
                new CloudflareTunnelResource("public")));

        Assert.Contains("public", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireCredentialsAnnotationReturnsTheLastAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tunnel = new CloudflareTunnelResource("public");
        var credentials = new CloudflareTunnelCredentialsAnnotation(
            builder.AddParameter("token", secret: true).Resource,
            builder.AddParameter("account").Resource);
        builder.AddResource(tunnel).WithAnnotation(credentials);

        var result = CloudflareApiClientFactory.RequireCredentialsAnnotation(tunnel);

        Assert.Same(credentials, result);
    }
}
