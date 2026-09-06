#pragma warning disable ASPIREPROCESSCOMMAND001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CommandLifecycleTests
{
    [Fact]
    public async Task OverlappingRunsAreRejectedAndLatestMarkdownIsRetained()
    {
        using var directory = new TestDirectory();
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject("tests", "tests.csproj").Resource;
        await using var app = builder.Build();
        var run = new TestRun(resource, new() { ResultsDirectory = directory.Path }, builder.AppHostDirectory);
        var context = Context(app.Services);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ExecuteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = run.ExecuteAsync(context, _ => { entered.SetResult(); return release.Task; });
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(run.IsRunning);
        var duplicate = await run.ExecuteAsync(context, _ => throw new InvalidOperationException("Must not execute"));
        Assert.False(duplicate.Success);
        Assert.Equal("Tests already running", duplicate.Message);
        var expected = TestResultMarkdown.Report("tests", TrxReportReader.Parse(ReportTests.Document("Passed")), 0);
        release.SetResult(expected);
        Assert.Same(expected, await running);
        Assert.False(run.IsRunning);
        Assert.Same(expected, run.LastResult);
        Assert.Equal(expected.Data!.Value, await File.ReadAllTextAsync(Path.Combine(run.DirectoryPath, "results.md"), TestContext.Current.CancellationToken));
        var firstPath = run.DirectoryPath;
        var failed = await run.ExecuteAsync(context, _ => Task.FromResult(CommandResults.Failure("Build failed")));
        Assert.False(failed.Success);
        Assert.NotEqual(firstPath, run.DirectoryPath);
        Assert.Equal(CommandResultFormat.Markdown, failed.Data!.Format);
        Assert.Contains("Build failed", failed.Data.Value);
        Assert.Same(failed, run.LastResult);
    }

    [Fact]
    public async Task CancellationTimeoutAndPreparationErrorsReleaseTheRunGate()
    {
        using var directory = new TestDirectory();
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject("tests", "tests.csproj").Resource;
        await using var app = builder.Build();
        var run = new TestRun(resource, new() { ResultsDirectory = directory.Path, Timeout = TimeSpan.FromMilliseconds(20) }, builder.AppHostDirectory);
        var timedOut = await run.ExecuteAsync(Context(app.Services), async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
            catch (OperationCanceledException) { return CommandResults.Canceled(); }
            throw new InvalidOperationException("Expected cancellation");
        });
        Assert.Equal("Test run timed out", timedOut.Message);
        Assert.False(run.IsRunning);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var result = await run.ExecuteAsync(Context(app.Services, canceled.Token), _ => Task.FromResult(CommandResults.Canceled()));
        Assert.True(result.Canceled);
        Assert.Contains("🟡", result.Data!.Value);
        var error = await run.ExecuteAsync(Context(app.Services), _ => throw new IOException("Preparation failed"));
        Assert.False(error.Success);
        Assert.Contains("Preparation failed", error.Data!.Value);
        Assert.False(run.IsRunning);
    }

    [Fact]
    public async Task ArtifactReadFailuresAndRunnerExitCodesReturnMarkdown()
    {
        using var directory = new TestDirectory();
        var builder = DistributedApplication.CreateBuilder();
        await using var app = builder.Build();
        var context = new ProcessCommandResultContext
        {
            Services = app.Services,
            ResourceName = "tests",
            Logger = NullLogger.Instance,
            CancellationToken = TestContext.Current.CancellationToken,
            ProcessCommandSpec = new("dotnet"),
            ExitCode = 1,
            Output = ["Build failed"],
            TotalOutputLineCount = 1,
        };
        var missing = await TestRunArtifacts.ReadResultAsync(context, directory.Path);
        Assert.False(missing.Success);
        Assert.Contains("Build failed", missing.Data!.Value);
        Assert.True(File.Exists(Path.Combine(directory.Path, "runner-output.txt")));
        ReportTests.Document("Passed", "Failed", "NotExecuted").Save(Path.Combine(directory.Path, "results.trx"));
        var mixed = await TestRunArtifacts.ReadResultAsync(context, directory.Path);
        Assert.False(mixed.Success);
        Assert.Contains("| 1 | 1 | 1 | 3 |", mixed.Data!.Value);
        Assert.Contains(directory.Path, mixed.Data.Value);
    }

    [Fact]
    public async Task ResultsCommandExplainsAnUnrunSuite()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject("tests", "tests.csproj").Resource;
        await using var app = builder.Build();
        var result = await resource.Annotations.OfType<ResourceCommandAnnotation>().Single(command => command.Name == "test-results").ExecuteCommand(Context(app.Services));
        Assert.Contains("🟡 No tests run yet", result.Data!.Value);
    }

    [Fact]
    public async Task UnresolvableReferencesProduceAnActionableMarkdownError()
    {
        using var directory = new TestDirectory();
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject("tests", "tests.csproj", new() { ResultsDirectory = directory.Path })
            .WithEnvironment(context => context.EnvironmentVariables["UNRESOLVABLE"] = new FailingValueProvider()).Resource;
        await using var app = builder.Build();
        var result = await resource.Annotations.OfType<ResourceCommandAnnotation>().Single(command => command.Name == "run-tests")
            .ExecuteCommand(Context(app.Services));
        Assert.False(result.Success);
        Assert.Equal(CommandResultFormat.Markdown, result.Data!.Format);
        Assert.Contains("Could not resolve", result.Data.Value);
    }

    private sealed class FailingValueProvider : IValueProvider
    {
        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<string?>(new InvalidOperationException("Unavailable reference"));
    }

    internal static ExecuteCommandContext Context(IServiceProvider services, CancellationToken? token = null) => new()
    {
        Services = services,
        ResourceName = "tests",
        Logger = NullLogger.Instance,
        CancellationToken = token ?? TestContext.Current.CancellationToken,
        Arguments = new([]),
    };
}
