using Aspire.Hosting.ApplicationModel;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Aspire.Hosting.Tests;

public sealed class TailscaleComposePublishingTests
{
    [Fact]
    public async Task ComposeSidecarPersistsStateAndServesTheContainerTargetPort()
    {
        var output = await PublishAsync(builder =>
        {
            var tailnet = builder.AddTailnet("tailnet");
            builder
                .AddContainer("web", "docker.io/traefik/whoami", "v1.10")
                .WithHttpEndpoint(targetPort: 8080)
                .WithTailscale(tailnet, hostname: "quadra-web");
        });

        var service = output.GetService("web-ts");
        Assert.Equal("docker.io/tailscale/tailscale:v1.102.5", service.Scalar("image"));

        var environment = service.Mapping("environment");
        Assert.Equal("quadra-web", environment.Scalar("TS_HOSTNAME"));
        Assert.Equal("true", environment.Scalar("TS_AUTH_ONCE"));
        Assert.Equal("true", environment.Scalar("TS_USERSPACE"));
        Assert.Equal("/var/lib/tailscale", environment.Scalar("TS_STATE_DIR"));
        Assert.Equal("--advertise-tags=tag:apps", environment.Scalar("TS_EXTRA_ARGS"));
        Assert.Equal("tag:apps", environment.Scalar("TAILSCALE_TAGS"));
        Assert.Equal(
            "${TAILNET_OAUTH_CLIENT_SECRET}?ephemeral=false&preauthorized=true",
            environment.Scalar("TS_AUTHKEY"));
        Assert.Equal("/etc/tailscale-serve.json", environment.Scalar("TS_SERVE_CONFIG"));
        Assert.Equal(
            TailscaleServeConfig.Create("http://web:8080", "$${TS_CERT_DOMAIN}"),
            environment.Scalar("TAILSCALE_SERVE_CONFIG_JSON"));

        var volume = Assert.Single(service.Sequence("volumes").Children.Cast<YamlMappingNode>());
        Assert.Equal("volume", volume.Scalar("type"));
        Assert.Equal("web-ts-state", volume.Scalar("source"));
        Assert.Equal("/var/lib/tailscale", volume.Scalar("target"));
        Assert.Contains("web-ts-state", output.Root.Mapping("volumes").Children.Keys.Select(key => key.ToString()));

        Assert.Equal(["/bin/sh"], service.Sequence("entrypoint").Children.Select(node => node.ToString()));
        Assert.Equal(
            ["-c", "eval \"$$TAILSCALE_START_SCRIPT\""],
            service.Sequence("command").Children.Select(node => node.ToString()));
        // Compose interpolates environment values, so every "$" in the script is
        // doubled and reaches the shell as a single "$".
        var script = environment.Scalar("TAILSCALE_START_SCRIPT");
        Assert.Equal(TailscaleSidecarDefaults.StartScript.Replace("$", "$$", StringComparison.Ordinal), script);
        Assert.Contains("\"$$TS_STATE_DIR/tailscaled.state\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$", script.Replace("$$", "", StringComparison.Ordinal), StringComparison.Ordinal);

        Assert.Contains("web", service.Mapping("depends_on").Children.Keys.Select(key => key.ToString()));
        Assert.Contains("TAILNET_OAUTH_CLIENT_SECRET", output.EnvFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeSidecarServesTheDefaultProjectContainerPort()
    {
        var output = await PublishAsync(builder =>
        {
            var tailnet = builder.AddTailnet("tailnet");
            builder
                .AddResource(new ProjectResource("api"))
                .WithHttpEndpoint(name: "http")
                .WithTailscale(tailnet, hostname: "quadra-api");
        });

        var environment = output.GetService("api-ts").Mapping("environment");
        Assert.Equal(
            TailscaleServeConfig.Create("http://api:8080", "$${TS_CERT_DOMAIN}"),
            environment.Scalar("TAILSCALE_SERVE_CONFIG_JSON"));
    }

    [Fact]
    public async Task ComposeSidecarWithoutAFixedTargetPortFailsWithAClearError()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => PublishAsync(builder =>
        {
            var tailnet = builder.AddTailnet("tailnet");
            builder
                .AddContainer("web", "nginx")
                .WithHttpEndpoint()
                .WithTailscale(tailnet, hostname: "quadra-web");
        }));

        Assert.Contains("'web'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'http'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("target port", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<ComposeOutput> PublishAsync(Action<IDistributedApplicationBuilder> configure)
    {
        var outputPath = Directory.CreateTempSubdirectory();
        try
        {
            // The Aspire CLI runs the publish operation as "--step publish"; without a
            // step the pipeline would also schedule deploy steps that need a runtime.
            var builder = DistributedApplication.CreateBuilder(
            [
                "--operation", "publish",
                "--step", "publish",
                "--output-path", outputPath.FullName,
            ]);
            builder.Configuration["Parameters:tailnet-oauth-client-secret"] = "tskey-client-test";
            builder.AddDockerComposeEnvironment("env");
            configure(builder);

            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);

            return new ComposeOutput(
                await File.ReadAllTextAsync(
                    Path.Combine(outputPath.FullName, "docker-compose.yaml"),
                    TestContext.Current.CancellationToken),
                await File.ReadAllTextAsync(
                    Path.Combine(outputPath.FullName, ".env"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            outputPath.Delete(recursive: true);
        }
    }

    private sealed class ComposeOutput
    {
        public ComposeOutput(string composeFile, string envFile)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(composeFile));
            Root = (YamlMappingNode)stream.Documents[0].RootNode;
            EnvFile = envFile;
        }

        public YamlMappingNode Root { get; }

        public string EnvFile { get; }

        public YamlMappingNode GetService(string name) => Root.Mapping("services").Mapping(name);
    }
}

internal static class YamlNodeExtensions
{
    public static YamlMappingNode Mapping(this YamlMappingNode node, string key) =>
        (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    public static YamlSequenceNode Sequence(this YamlMappingNode node, string key) =>
        (YamlSequenceNode)node.Children[new YamlScalarNode(key)];

    public static string Scalar(this YamlMappingNode node, string key) =>
        ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value
        ?? throw new InvalidOperationException($"'{key}' has no value.");
}
