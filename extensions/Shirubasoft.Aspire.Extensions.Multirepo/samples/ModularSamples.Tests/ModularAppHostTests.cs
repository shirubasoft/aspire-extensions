using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ModularSamples.Tests;

[Collection(ModularAppHostTestsCollection.Name)]
public sealed class ModularAppHostTests
{
    private const string ExpectedMessage = "Hello from an arbitrary exported Aspire resource.";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Both_AppHosts_apply_module_callbacks_and_run_the_exported_resources()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        Assert.SkipUnless(
            string.Equals(
                configuration["MODULAR_SAMPLES_E2E"],
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase),
            "Set MODULAR_SAMPLES_E2E=true after confirming Docker or Podman is available.");
        await VerifyAppHostAsync<Projects.ModularSample_AppHostA>(hasGateway: false);
        await VerifyAppHostAsync<Projects.ModularSample_AppHostB>(hasGateway: true);
    }

    [Fact]
    public async Task AppHost_pipeline_switches_native_project_between_project_and_container_modes()
    {
        SkipUnlessContainerTestsEnabled();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var appHost = Path.Combine(
            FindRepositoryRoot(),
            "extensions",
            "Shirubasoft.Aspire.Extensions.Multirepo",
            "samples",
            "AppHostA",
            "ModularSample.AppHostA.csproj");
        try
        {
            await RunAspireStepAsync(appHost, "build-sample-api", timeout.Token);
            await RunAspireStepAsync(appHost, "use-containers", timeout.Token);
            await VerifyAppHostAsync<Projects.ModularSample_AppHostA>(
                hasGateway: false,
                expectedApiType: typeof(ContainerResource));

            await RunAspireStepAsync(appHost, "use-project-sample-api", timeout.Token);
            await VerifyAppHostModelAsync<Projects.ModularSample_AppHostA>(
                typeof(ProjectResource),
                timeout.Token);

            await RunAspireStepAsync(appHost, "use-configured-sample-api", timeout.Token);
            await VerifyAppHostModelAsync<Projects.ModularSample_AppHostA>(
                typeof(ProjectResource),
                timeout.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await RunAspireStepAsync(appHost, "use-configured-modes", cleanup.Token);
        }
    }

    private static async Task VerifyAppHostAsync<TEntryPoint>(
        bool hasGateway,
        Type? expectedApiType = null)
        where TEntryPoint : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<TEntryPoint>([], timeout.Token);
        await using var application = await builder.BuildAsync(timeout.Token)
            .WaitAsync(TestTimeout, timeout.Token);

        if (expectedApiType is not null)
        {
            Assert.Equal(
                expectedApiType,
                application.Services
                    .GetRequiredService<DistributedApplicationModel>()
                    .Resources
                    .Single(resource => resource.Name == "sample-api")
                    .GetType());
        }

        await application.StartAsync(timeout.Token).WaitAsync(TestTimeout, timeout.Token);
        await WaitForHealthyAsync(application, "sample-api", timeout.Token);
        await WaitForHealthyAsync(application, "sample-static", timeout.Token);
        await WaitForHealthyAsync(application, "sample-generated-static", timeout.Token);
        if (hasGateway)
        {
            await WaitForHealthyAsync(application, "dependency-gateway", timeout.Token);
        }

        using var apiClient = application.CreateHttpClient("sample-api", "http");
        var api = await apiClient.GetFromJsonAsync<JsonElement>("/", timeout.Token);
        Assert.Equal("sample-api", api.GetProperty("service").GetString());
        Assert.Equal(ExpectedMessage, api.GetProperty("message").GetString());

        using var staticClient = application.CreateHttpClient("sample-static", "http");
        Assert.Equal(ExpectedMessage, await staticClient.GetStringAsync("/", timeout.Token));

        using var generatedClient = application.CreateHttpClient("sample-generated-static", "http");
        var generated = await generatedClient.GetStringAsync("/", timeout.Token);
        Assert.Contains("published by a modular AppHost", generated, StringComparison.Ordinal);

        if (hasGateway)
        {
            using var gatewayClient = application.CreateHttpClient("dependency-gateway", "http");
            var gateway = await gatewayClient.GetFromJsonAsync<JsonElement>("/", timeout.Token);
            Assert.Equal("dependency-gateway", gateway.GetProperty("service").GetString());
            Assert.Equal(ExpectedMessage, gateway.GetProperty("message").GetString());
            Assert.StartsWith("http", gateway.GetProperty("api").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("http", gateway.GetProperty("staticSite").GetString(), StringComparison.Ordinal);
            Assert.StartsWith(
                "http",
                gateway.GetProperty("generatedStaticSite").GetString(),
                StringComparison.Ordinal);
        }
    }

    private static async Task WaitForHealthyAsync(
        DistributedApplication application,
        string resourceName,
        CancellationToken cancellationToken)
        => await application.ResourceNotifications.WaitForResourceHealthyAsync(
            resourceName,
            cancellationToken).WaitAsync(TestTimeout, cancellationToken);

    private static void SkipUnlessContainerTestsEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        Assert.SkipUnless(
            string.Equals(
                configuration["MODULAR_SAMPLES_E2E"],
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase),
            "Set MODULAR_SAMPLES_E2E=true after confirming Docker or Podman is available.");
    }

    private static async Task VerifyAppHostModelAsync<TEntryPoint>(
        Type expectedApiType,
        CancellationToken cancellationToken)
        where TEntryPoint : class
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<TEntryPoint>([], cancellationToken);
        await using var application = await builder.BuildAsync(cancellationToken)
            .WaitAsync(TestTimeout, cancellationToken);
        Assert.Equal(
            expectedApiType,
            application.Services
                .GetRequiredService<DistributedApplicationModel>()
                .Resources
                .Single(resource => resource.Name == "sample-api")
                .GetType());
    }

    private static async Task RunAspireStepAsync(
        string appHost,
        string step,
        CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("dotnet")
            .WithArguments(
            [
                "tool", "run", "aspire", "--", "do", step,
                "--apphost", appHost,
                "--non-interactive"
            ])
            .WithWorkingDirectory(FindRepositoryRoot())
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        Assert.True(
            result.IsSuccess,
            $"aspire do {step} failed with exit code {result.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{result.StandardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException(
            $"Could not find the repository root from '{AppContext.BaseDirectory}'.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModularAppHostTestsCollection
{
    public const string Name = "Modular AppHost E2E";
}
