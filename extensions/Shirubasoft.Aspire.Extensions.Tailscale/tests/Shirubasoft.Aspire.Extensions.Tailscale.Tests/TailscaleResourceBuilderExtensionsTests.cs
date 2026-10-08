using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class TailscaleResourceBuilderExtensionsTests
{
    [Fact]
    public void AddTailnetCreatesASecretParameterWithDefaultTags()
    {
        var builder = DistributedApplication.CreateBuilder();

        var tailnet = builder.AddTailnet("tailnet");

        Assert.Equal("tailnet", tailnet.Resource.Name);
        Assert.Equal(["tag:apps"], tailnet.Resource.Tags);
        var secret = Assert.Single(builder.Resources.OfType<ParameterResource>());
        Assert.Same(secret, tailnet.Resource.OAuthClientSecret);
        Assert.Equal("tailnet-oauth-client-secret", secret.Name);
        Assert.True(secret.Secret);
        Assert.Contains(
            tailnet.Resource.Annotations,
            annotation => ReferenceEquals(annotation, ManifestPublishingCallbackAnnotation.Ignore));
    }

    // Tags are a set: the node advertises them sorted and without duplicates so
    // that reordering an AppHost never looks like a change of identity.
    [Fact]
    public void AddTailnetCanonicalizesTheProvidedTags()
    {
        var builder = DistributedApplication.CreateBuilder();

        var tailnet = builder.AddTailnet("tailnet", tags: ["tag:web", "tag:apps", "tag:web"]);

        Assert.Equal(["tag:apps", "tag:web"], tailnet.Resource.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddTailnetRejectsBlankNames(string name)
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddTailnet(name));
    }

    // The grammar follows tailcfg.CheckTag: "tag:", a letter, then letters, digits,
    // or hyphens. Anything else would reach "tailscale up" as extra arguments or
    // fail registration later.
    [Theory]
    [InlineData]
    [InlineData("apps")]
    [InlineData("tag:apps", "")]
    [InlineData("tag:")]
    [InlineData("TAG:apps")]
    [InlineData("tag:123")]
    [InlineData("tag:apps --accept-dns=true")]
    [InlineData("tag:apps,tag:web")]
    [InlineData("tag:ap_ps")]
    [InlineData("tag:apps ")]
    [InlineData(" tag:apps")]
    public void AddTailnetRejectsInvalidTags(params string[] tags)
    {
        var builder = DistributedApplication.CreateBuilder();

        var exception = Assert.Throws<ArgumentException>(() => builder.AddTailnet("tailnet", tags));

        Assert.Contains("tag:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTailnetRejectsANullTag()
    {
        var builder = DistributedApplication.CreateBuilder();
        string[] tags = ["tag:apps", null!];

        Assert.Throws<ArgumentException>(() => builder.AddTailnet("tailnet", tags));
    }

    [Theory]
    [InlineData("tag:a")]
    [InlineData("tag:Apps")]
    [InlineData("tag:apps-2")]
    [InlineData("tag:a1-b2")]
    public void AddTailnetAcceptsTagsThatMatchTheTailscaleGrammar(string tag)
    {
        var builder = DistributedApplication.CreateBuilder();

        var tailnet = builder.AddTailnet("tailnet", tags: [tag]);

        Assert.Equal([tag], tailnet.Resource.Tags);
    }

    [Fact]
    public void WithTailscaleAddsASidecarNodeForTheResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        var result = web.WithTailscale(tailnet, hostname: "quadra-web");

        Assert.Same(web, result);
        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());
        Assert.Equal("web-ts", sidecar.Name);
        Assert.Same(tailnet.Resource, sidecar.Tailnet);
        Assert.Same(web.Resource, sidecar.Target);
        Assert.Equal("http", sidecar.TargetEndpoint.EndpointName);
        Assert.Equal("quadra-web-dev", sidecar.Hostname);
        Assert.Equal(["tag:apps"], sidecar.Tags);

        var image = Assert.Single(sidecar.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("docker.io", image.Registry);
        Assert.Equal("tailscale/tailscale", image.Image);
        Assert.Equal("v1.102.5", image.Tag);
        Assert.Equal("/bin/sh", sidecar.Entrypoint);
        Assert.Contains(
            sidecar.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => ReferenceEquals(relationship.Resource, web.Resource)
                && relationship.Type == "Parent");
        var wait = Assert.Single(sidecar.Annotations.OfType<WaitAnnotation>());
        Assert.Same(web.Resource, wait.Resource);
        Assert.Equal(WaitType.WaitUntilHealthy, wait.WaitType);
        Assert.Empty(sidecar.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Fact]
    public void WithTailscaleUsesTheNamedEndpointAndOverridesTags()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80)
            .WithHttpEndpoint(targetPort: 81, name: "admin");

        web.WithTailscale(tailnet, "quadra-admin", endpointName: "admin", tags: ["tag:ops", "tag:admin", "tag:ops"]);

        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());
        Assert.Equal("admin", sidecar.TargetEndpoint.EndpointName);
        Assert.Equal(["tag:admin", "tag:ops"], sidecar.Tags);
    }

    [Fact]
    public void WithTailscaleRejectsAMissingEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        var exception = Assert.Throws<InvalidOperationException>(
            () => web.WithTailscale(tailnet, "quadra-admin", endpointName: "admin"));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'admin'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(builder.Resources.OfType<TailscaleSidecarResource>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Quadra_Admin")]
    [InlineData("-web")]
    [InlineData("web-")]
    [InlineData("web.example")]
    [InlineData("a123456789b123456789c123456789d123456789e123456789f123456789")]
    public void WithTailscaleRejectsInvalidHostnames(string hostname)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        Assert.Throws<ArgumentException>(() => web.WithTailscale(tailnet, hostname));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("web1")]
    [InlineData("quadra-admin-2")]
    [InlineData("a123456789b123456789c123456789d123456789e123456789f12345678")]
    public void WithTailscaleAcceptsDnsLabelHostnames(string hostname)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        web.WithTailscale(tailnet, hostname);

        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());
        Assert.Equal($"{hostname}-dev", sidecar.Hostname);
    }

    [Theory]
    [InlineData("apps")]
    [InlineData("tag:apps --accept-dns=true")]
    [InlineData("tag:123")]
    public void WithTailscaleRejectsInvalidTags(string tag)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 80);

        Assert.Throws<ArgumentException>(() => web.WithTailscale(tailnet, "web", tags: [tag]));
    }

    [Fact]
    public async Task RunModeStartsAnEphemeralDevelopmentNodeWithoutState()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:tailnet-oauth-client-secret"] = "tskey-client-test";
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080);
        web.WithTailscale(tailnet, hostname: "quadra-web");
        AllocateOnContainerNetwork(web.Resource, "web", 8080);
        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());

        var configuration = await BuildRunConfigurationAsync(builder, sidecar);
        var environment = configuration.EnvironmentVariables;
        var arguments = configuration.Arguments;

        Assert.Equal("quadra-web-dev", environment["TS_HOSTNAME"]);
        Assert.Equal(
            "tskey-client-test?ephemeral=true&preauthorized=true",
            environment["TS_AUTHKEY"]);
        Assert.Equal("true", environment["TS_AUTH_ONCE"]);
        Assert.Equal("true", environment["TS_USERSPACE"]);
        Assert.Equal("--advertise-tags=tag:apps", environment["TS_EXTRA_ARGS"]);
        Assert.Equal("tag:apps", environment["TAILSCALE_TAGS"]);
        Assert.Equal("/etc/tailscale-serve.json", environment["TS_SERVE_CONFIG"]);
        Assert.DoesNotContain("TS_STATE_DIR", environment.Keys);
        Assert.Equal(
            TailscaleServeConfig.Create("http://web:8080", "${TS_CERT_DOMAIN}"),
            environment["TAILSCALE_SERVE_CONFIG_JSON"]);
        Assert.Contains("\"${TS_CERT_DOMAIN}:443\"", environment["TAILSCALE_SERVE_CONFIG_JSON"], StringComparison.Ordinal);

        // The script travels in an environment variable and the entrypoint evaluates
        // it, so the script itself is free to use every shell expansion.
        Assert.Equal(["-c", "eval \"$TAILSCALE_START_SCRIPT\""], arguments);
        var script = environment["TAILSCALE_START_SCRIPT"];
        Assert.Equal(TailscaleSidecarDefaults.StartScript, script);
        Assert.Contains("\"$TAILSCALE_SERVE_CONFIG_JSON\"", script, StringComparison.Ordinal);
        Assert.Contains("\"$TS_SERVE_CONFIG\"", script, StringComparison.Ordinal);
        Assert.Contains("\"${TS_STATE_DIR:-}\"", script, StringComparison.Ordinal);
        Assert.Contains("/tailscaled.state\"", script, StringComparison.Ordinal);
        Assert.EndsWith("exec containerboot", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunModeStartScriptUsesLfLineEndings()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:tailnet-oauth-client-secret"] = "tskey-client-test";
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080);
        web.WithTailscale(tailnet, "quadra-web");
        AllocateOnContainerNetwork(web.Resource, "web", 8080);
        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());

        var configuration = await BuildRunConfigurationAsync(builder, sidecar);
        var script = configuration.EnvironmentVariables["TAILSCALE_START_SCRIPT"];

        Assert.Contains("\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunModeAdvertisesTheOverriddenTags()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:tailnet-oauth-client-secret"] = "tskey-client-test";
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080);
        web.WithTailscale(tailnet, "quadra-web", tags: ["tag:web", "tag:admin", "tag:web"]);
        AllocateOnContainerNetwork(web.Resource, "web", 8080);
        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());

        var configuration = await BuildRunConfigurationAsync(builder, sidecar);

        Assert.Equal("--advertise-tags=tag:admin,tag:web", configuration.EnvironmentVariables["TS_EXTRA_ARGS"]);
        Assert.Equal("tag:admin,tag:web", configuration.EnvironmentVariables["TAILSCALE_TAGS"]);
    }

    [Fact]
    public async Task PublishModeWithoutADockerComposeEnvironmentFailsWithAClearError()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:tailnet-oauth-client-secret"] = "tskey-client-test";
        var tailnet = builder.AddTailnet("tailnet");
        builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTailscale(tailnet, hostname: "quadra-web");
        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildRunConfigurationAsync(builder, sidecar));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Docker Compose", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishModeToAnotherComputeEnvironmentNamesItInTheError()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:tailnet-oauth-client-secret"] = "tskey-client-test";
        var tailnet = builder.AddTailnet("tailnet");
        var web = builder
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTailscale(tailnet, hostname: "quadra-web");
        web.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeEnvironmentResource("cluster-service"))
        {
            ComputeEnvironment = new TestComputeEnvironmentResource("cluster"),
        });
        var sidecar = Assert.Single(builder.Resources.OfType<TailscaleSidecarResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildRunConfigurationAsync(builder, sidecar));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'cluster'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Docker Compose", exception.Message, StringComparison.Ordinal);
    }

    // Parameter values resolve through the application services, so the
    // execution context must come from a built application.
    private static async Task<RunConfiguration> BuildRunConfigurationAsync(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        using var app = builder.Build();
        var result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .WithArgumentsConfig()
            .BuildAsync(
                app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                cancellationToken: TestContext.Current.CancellationToken);

        return new RunConfiguration(
            result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal),
            result.Arguments.Select(argument => argument.Value).ToArray());
    }

    private sealed record RunConfiguration(
        Dictionary<string, string> EnvironmentVariables,
        string[] Arguments);

#pragma warning disable ASPIRECOMPUTE002
    private sealed class TestComputeEnvironmentResource(string name)
        : Resource(name), IComputeEnvironmentResource
    {
        public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) =>
            ReferenceExpression.Create($"{endpointReference.Resource.Name}.internal");
    }
#pragma warning restore ASPIRECOMPUTE002

    private static void AllocateOnContainerNetwork(IResource resource, string host, int port)
    {
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
            new AllocatedEndpoint(
                endpoint,
                host,
                port,
                EndpointBindingMode.SingleAddress,
                networkId: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
    }
}
