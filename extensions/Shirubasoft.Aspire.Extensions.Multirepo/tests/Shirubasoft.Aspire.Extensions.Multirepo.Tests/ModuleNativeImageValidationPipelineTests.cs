using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleNativeImageValidationPipelineTests
{
    [Fact]
    public async Task Native_push_validation_rejects_dirty_source_but_accepts_clean_source()
    {
        using var repository = TemporaryDirectory.Create();
        await RunGitAsync(repository.Path, "init");
        await RunGitAsync(repository.Path, "config", "user.email", "native-images@example.test");
        await RunGitAsync(repository.Path, "config", "user.name", "Native Image Tests");
        await File.WriteAllTextAsync(
            Path.Combine(repository.Path, "tracked.txt"),
            "clean",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repository.Path, "add", "tracked.txt");
        await RunGitAsync(repository.Path, "-c", "commit.gpgsign=false", "commit", "-m", "initial");
        var resource = new ContainerResource("native-image");

        await ModuleNativeImageValidationPipeline.ValidateCleanSourceAsync(
            resource,
            repository.Path,
            "git",
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(repository.Path, "untracked.txt"),
            "dirty",
            TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleNativeImageValidationPipeline.ValidateCleanSourceAsync(
                resource,
                repository.Path,
                "git",
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken));

        Assert.Contains("cannot push", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dirty repository", exception.Message, StringComparison.Ordinal);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ModuleCliRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            TimeSpan.FromMinutes(1),
            "prepare native image validation repository",
            TestContext.Current.CancellationToken,
            static _ => { });
        Assert.True(result.IsSuccess, result.StandardError);
    }
}
