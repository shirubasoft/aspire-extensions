using System.Diagnostics;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Spire.ModuleContract;
using Xunit;

namespace Spire.Consumer.Tests;

public sealed class PublisherFallbackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Publisher_uses_the_published_image_without_its_build_repository()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        var imageReference = configuration["MODULAR_PUBLISHER_FALLBACK_IMAGE"];
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(imageReference),
            "Set MODULAR_PUBLISHER_FALLBACK_IMAGE to a test-owned image in a running registry.");
        var image = ParseImageReference(imageReference);
        var repositoryRoot = FindRepositoryRoot();
        var appHostPath = Path.Combine(
            repositoryRoot,
            "samples",
            "MultiRepoE2E",
            "Spire.Consumer.AppHost",
            "Spire.Consumer.AppHost.csproj");
        var missingBuildRepository = Path.Combine(
            Path.GetTempPath(),
            $"missing-publisher-build-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"publisher-description-{Guid.NewGuid():N}");
        var section =
            $"Aspire__ModularAppHosts__Modules__{SpireModule.Name}__Containers__{SpireModule.ApiResourceName}";
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{section}__BuildRepository"] = missingBuildRepository,
            [$"{section}__ImageRegistry"] = image.Registry,
            [$"{section}__ImageName"] = image.Name,
            [$"{section}__ImageTag"] = image.Tag,
            [$"{section}__PullBeforeBuild"] = bool.TrueString
        };
        var localAlias = $"{image.Registry}/{image.Name}:aspire-run";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);

        try
        {
            var description = await RunRequiredAsync(
                "dotnet",
                [
                    "tool", "run", "aspire", "--",
                    "do", "describe-images",
                    "--apphost", appHostPath,
                    "--output-path", outputPath,
                    "--non-interactive"
                ],
                repositoryRoot,
                environment,
                timeout.Token);
            Assert.Contains(imageReference, description, StringComparison.Ordinal);
            Assert.False(Directory.Exists(missingBuildRepository));

            await RemoveImagesAsync(
                configuration["ASPIRE_CONTAINER_RUNTIME"] ?? "docker",
                repositoryRoot,
                [imageReference, localAlias],
                timeout.Token);
            await RunRequiredAsync(
                "dotnet",
                [
                    "tool", "run", "aspire", "--",
                    "do", $"build-{SpireModule.ApiResourceName}",
                    "--apphost", appHostPath,
                    "--non-interactive"
                ],
                repositoryRoot,
                environment,
                timeout.Token);
            Assert.False(Directory.Exists(missingBuildRepository));

            var missingTagEnvironment = new Dictionary<string, string?>(environment, StringComparer.Ordinal)
            {
                [$"{section}__BuildRepository"] =
                    $"https://example.invalid/publisher-fallback-{Guid.NewGuid():N}.git",
                [$"{section}__ImageTag"] = $"missing-{Guid.NewGuid():N}"
            };
            var missingTag = await RunAsync(
                "dotnet",
                [
                    "tool", "run", "aspire", "--",
                    "do", $"build-{SpireModule.ApiResourceName}",
                    "--apphost", appHostPath,
                    "--non-interactive"
                ],
                repositoryRoot,
                missingTagEnvironment,
                timeout.Token);
            Assert.NotEqual(0, missingTag.ExitCode);
            var normalizedFailure = string.Join(
                ' ',
                missingTag.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(
                $"aspire do initialize --apphost \"{Path.GetDirectoryName(Path.GetFullPath(appHostPath))}\" --non-interactive",
                normalizedFailure,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to start a process with file path 'git'", normalizedFailure);

            await RemoveImagesAsync(
                configuration["ASPIRE_CONTAINER_RUNTIME"] ?? "docker",
                repositoryRoot,
                [imageReference, localAlias],
                timeout.Token);
            var builderArguments = environment
                .Select(pair => $"{pair.Key.Replace("__", ":", StringComparison.Ordinal)}={pair.Value}")
                .ToArray();
            await using var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.Spire_Consumer_AppHost>(builderArguments, timeout.Token);
            await using var application = await builder.BuildAsync(timeout.Token)
                .WaitAsync(TestTimeout, timeout.Token);
            await application.StartAsync(timeout.Token).WaitAsync(TestTimeout, timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync(
                SpireModule.ApiResourceName,
                timeout.Token);
            using var client = application.CreateHttpClient(SpireModule.ApiResourceName, "http");

            var marker = await client.GetStringAsync("/marker.txt", timeout.Token);

            Assert.Equal("multi-repo-resource-pinned-revision", marker.Trim());
            Assert.False(Directory.Exists(missingBuildRepository));
        }
        finally
        {
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }
    }

    private static async Task RemoveImagesAsync(
        string containerRuntime,
        string workingDirectory,
        IEnumerable<string> references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references.Distinct(StringComparer.Ordinal))
        {
            await RunAsync(
                containerRuntime,
                ["image", "rm", "--force", reference],
                workingDirectory,
                environment: null,
                cancellationToken);
        }
    }

    private static async Task<string> RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            fileName,
            arguments,
            workingDirectory,
            environment,
            cancellationToken);
        Assert.True(
            result.ExitCode == 0,
            $"Command '{fileName} {string.Join(' ', arguments)}' failed with exit code {result.ExitCode}." +
            $"{Environment.NewLine}{result.Output}");
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start '{fileName}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (
            process.ExitCode,
            $"{await standardOutput}{Environment.NewLine}{await standardError}");
    }

    private static (string Registry, string Name, string Tag) ParseImageReference(string reference)
    {
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        var colon = reference.LastIndexOf(':');
        if (slash <= 0 || colon <= slash + 1 || colon == reference.Length - 1)
        {
            throw new InvalidOperationException(
                $"MODULAR_PUBLISHER_FALLBACK_IMAGE must be a registry-qualified tagged reference, but was '{reference}'.");
        }

        return (reference[..slash], reference[(slash + 1)..colon], reference[(colon + 1)..]);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Shirubasoft.Aspire.Extensions.Multirepo.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to find the Multirepo extension root.");
    }
}
