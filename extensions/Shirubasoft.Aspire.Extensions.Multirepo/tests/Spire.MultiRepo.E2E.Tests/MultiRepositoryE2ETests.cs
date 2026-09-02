using Aspire.Hosting;
using Aspire.Hosting.Testing;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Spire.MultiRepo.E2E.Support;
using Xunit;

namespace Spire.MultiRepo.E2E.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MultiRepositoryE2ECollection
{
    public const string Name = "Multi-repository E2E";
}

[Collection(MultiRepositoryE2ECollection.Name)]
public sealed class MultiRepositoryE2ETests
{
    private const string ResourceName = "multi-repo-api";
    private const string ExpectedMarker = "multi-repo-resource-pinned-revision";
    private static readonly TimeSpan AppHostTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FullScenarioTimeout = TimeSpan.FromMinutes(25);

    [Fact]
    [Trait("Category", "MultiRepoE2E")]
    public async Task Checked_in_AppHosts_work_with_their_default_configuration()
    {
        RequireE2E();

        await VerifyAppHostAsync<Projects.Spire_Consumer_AppHost>();
        await VerifyAppHostAsync<Projects.Spire_Producer_AppHost>();
    }

    [Fact]
    [Trait("Category", "MultiRepoE2E")]
    public async Task Isolated_repositories_obey_initialization_and_runtime_policies()
    {
        RequireE2E();
        var repositoryRoot = FindRepositoryRoot();
        var supportExecutable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "Spire.MultiRepo.E2E.Support.exe"
                : "Spire.MultiRepo.E2E.Support");
        Assert.True(File.Exists(supportExecutable), $"Missing E2E support executable '{supportExecutable}'.");

        var arguments = new List<string>
        {
            "--repository-root",
            repositoryRoot
        };
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var runtime = configuration["ASPIRE_CONTAINER_RUNTIME"];
        if (runtime is "docker" or "podman")
        {
            arguments.Add("--container-runtime");
            arguments.Add(runtime);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(FullScenarioTimeout);
        var result = await Cli.Wrap(supportExecutable)
            .WithArguments(arguments)
            .WithWorkingDirectory(repositoryRoot)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(timeout.Token);

        Assert.True(
            result.ExitCode == 0,
            $"The isolated multi-repository scenario failed with exit code {result.ExitCode}." +
            $"{Environment.NewLine}{result.StandardOutput}" +
            $"{Environment.NewLine}{result.StandardError}");
    }

    private static async Task VerifyAppHostAsync<TEntryPoint>()
        where TEntryPoint : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(AppHostTimeout);
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<TEntryPoint>([], timeout.Token);
        await using var application = await builder.BuildAsync(timeout.Token)
            .WaitAsync(AppHostTimeout, timeout.Token);

        await application.StartAsync(timeout.Token).WaitAsync(AppHostTimeout, timeout.Token);
        await application.ResourceNotifications.WaitForResourceHealthyAsync(
            ResourceName,
            timeout.Token).WaitAsync(AppHostTimeout, timeout.Token);
        using var client = application.CreateHttpClient(ResourceName, "http");

        var marker = await client.GetStringAsync("/marker.txt", timeout.Token);

        Assert.Equal(ExpectedMarker, marker.Trim());
    }

    private static void RequireE2E()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        Assert.SkipUnless(
            string.Equals(
                configuration["MULTI_REPO_E2E"],
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase),
            "Set MULTI_REPO_E2E=true after confirming Docker or Podman is available.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Aspire.Extensions.slnx")) &&
                File.Exists(Path.Combine(
                    current.FullName,
                    "extensions",
                    "Shirubasoft.Aspire.Extensions.Multirepo",
                    "Shirubasoft.Aspire.Extensions.Multirepo.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to find the repository root.");
    }

}
