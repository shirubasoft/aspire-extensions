using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareQuickTunnelAllowedMailTests
{
    [Fact]
    public void WithAllowedMailEmitsEachNormalizedRuleAfterTheTargetUrl()
    {
        var tunnel = CreateQuickTunnel();

        var result = tunnel
            .WithAllowedMail(" Alice@Example.com ", "*@example.org")
            .WithAllowedMail("bob@example.net,alice@example.com");

        Assert.Same(tunnel, result);
        Assert.Single(tunnel.Resource.Annotations.OfType<CloudflareQuickTunnelAllowedMailAnnotation>());
        Assert.Equal(
            [
                "--allowed-mail", "alice@example.com",
                "--allowed-mail", "*@example.org",
                "--allowed-mail", "bob@example.net",
            ],
            BuildArguments(tunnel).Skip(6).Select(argument => argument.ToString()!).ToArray());
    }

    [Fact]
    public void WithAllowedMailAcceptsRepeatedRules()
    {
        var tunnel = CreateQuickTunnel()
            .WithAllowedMail("alice@example.com")
            .WithAllowedMail("ALICE@example.com");

        Assert.Equal(
            ["--allowed-mail", "alice@example.com"],
            BuildArguments(tunnel).Skip(6).Select(argument => argument.ToString()!).ToArray());
    }

    [Theory]
    [InlineData("*@xn--bcher-kva.example")]
    [InlineData("first.last+tag@sub.example.com")]
    public void WithAllowedMailAcceptsValidRules(string rule)
    {
        var tunnel = CreateQuickTunnel().WithAllowedMail(rule);

        Assert.Equal(
            [rule],
            Assert.Single(tunnel.Resource.Annotations.OfType<CloudflareQuickTunnelAllowedMailAnnotation>()).Rules);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("alice")]
    [InlineData("alice@example.com,")]
    [InlineData("Alice <alice@example.com>")]
    [InlineData("*@")]
    [InlineData("*@*.example.com")]
    [InlineData("*example.com")]
    [InlineData("alice@bücher.example")]
    public void WithAllowedMailRejectsInvalidRules(string rule)
    {
        var tunnel = CreateQuickTunnel();

        var exception = Assert.Throws<ArgumentException>(() => tunnel.WithAllowedMail(rule));

        Assert.Equal("recipients", exception.ParamName);
        Assert.Empty(tunnel.Resource.Annotations.OfType<CloudflareQuickTunnelAllowedMailAnnotation>());
    }

    [Fact]
    public void WithAllowedMailRejectsMissingArguments()
    {
        var tunnel = CreateQuickTunnel();

        Assert.Throws<ArgumentNullException>(
            () => CloudflareTunnelResourceBuilderExtensions.WithAllowedMail(null!, "alice@example.com"));
        Assert.Throws<ArgumentNullException>(() => tunnel.WithAllowedMail(null!));
        Assert.Throws<ArgumentException>(() => tunnel.WithAllowedMail());
        Assert.Throws<ArgumentNullException>(() => tunnel.WithAllowedMail([null!]));
    }

    [Fact]
    public void WithAllowedMailEnforcesCloudflaredLimits()
    {
        var tunnel = CreateQuickTunnel()
            .WithAllowedMail(Enumerable.Range(0, 100).Select(index => $"user{index}@example.com"));
        var longRule = $"*@{string.Join('.', Enumerable.Repeat(new string('b', 63), 5))}";

        Assert.Throws<ArgumentException>(() => tunnel.WithAllowedMail("user100@example.com"));
        Assert.Equal(
            100,
            Assert.Single(tunnel.Resource.Annotations.OfType<CloudflareQuickTunnelAllowedMailAnnotation>()).Rules.Length);
        var exception = Assert.Throws<ArgumentException>(() => CreateQuickTunnel().WithAllowedMail(longRule));
        Assert.Equal(321, longRule.Length);
        Assert.Contains("320-byte", exception.Message, StringComparison.Ordinal);
    }

    private static IResourceBuilder<CloudflareQuickTunnelResource> CreateQuickTunnel()
    {
        var builder = DistributedApplication.CreateBuilder();
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        return builder.AddCloudflareQuickTunnel("quick").WithReference(web);
    }

    private static List<object> BuildArguments(IResourceBuilder<CloudflareQuickTunnelResource> tunnel)
    {
        var arguments = new List<object>();
        CloudflareTunnelResourceBuilderExtensions.AddQuickTunnelArguments(
            new CommandLineArgsCallbackContext(
                arguments,
                tunnel.Resource,
                TestContext.Current.CancellationToken),
            tunnel.Resource);
        return arguments;
    }
}
