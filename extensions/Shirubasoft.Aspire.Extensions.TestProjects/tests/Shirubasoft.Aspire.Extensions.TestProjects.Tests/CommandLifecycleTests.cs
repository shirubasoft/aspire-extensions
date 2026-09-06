using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CommandLifecycleTests
{
    [Fact]
    public async Task LifecycleRetainsCompletedReportsInSeparateRunDirectories()
    {
        using var directory = new TestDirectory();
        var run = new TestRun(new ProjectResource("tests"), directory.Path);
        await run.CompleteAsync(Snapshot(0), TestContext.Current.CancellationToken);
        Assert.Contains("No tests run yet", run.LastResult.Data!.Value);
        run.Prepare();
        var first = run.Current!;
        ReportTests.Document("Passed", "NotExecuted").Save(Path.Combine(first.DirectoryPath, "results.trx"));
        await run.CompleteAsync(Snapshot(0), TestContext.Current.CancellationToken);
        var result = await first.Completion.Task;
        Assert.True(result.Success);
        Assert.Same(result, run.LastResult);
        Assert.Contains("| 1 | 0 | 1 | 2 |", result.Data!.Value);
        Assert.Equal(result.Data.Value, await File.ReadAllTextAsync(Path.Combine(first.DirectoryPath, "results.md"), TestContext.Current.CancellationToken));
        run.Prepare();
        Assert.NotEqual(first.DirectoryPath, run.Current!.DirectoryPath);
        Assert.Contains(run.Current.DirectoryPath, run.Arguments);
        await run.CompleteAsync(Snapshot(1), TestContext.Current.CancellationToken);
        Assert.False(run.LastResult.Success);
        Assert.Contains("did not produce a TRX report", run.LastResult.Data!.Value);
    }

    [Fact]
    public async Task CancellationAndArtifactErrorsRemainUnsuccessfulMarkdown()
    {
        using var directory = new TestDirectory();
        var session = new TestRunSession(directory.Path);
        session.Cancel();
        var canceled = await TestRunArtifacts.ReadAndSaveAsync("tests", session, Snapshot(null), TestContext.Current.CancellationToken);
        Assert.True(canceled.Canceled);
        Assert.False(canceled.Success);
        Assert.Contains("🟡 Test run canceled", canceled.Data!.Value);
        var fresh = new TestRunSession(directory.Path);
        ReportTests.Document("Passed").Save(Path.Combine(directory.Path, "results.trx"));
        var unknownExit = await TestRunArtifacts.ReadAndSaveAsync("tests", fresh, Snapshot(null), TestContext.Current.CancellationToken);
        Assert.False(unknownExit.Success);
        File.Delete(Path.Combine(directory.Path, "results.md"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "results.md"));
        var writeError = await TestRunArtifacts.ReadAndSaveAsync("tests", fresh, Snapshot(0), TestContext.Current.CancellationToken);
        Assert.False(writeError.Success);
        Assert.Contains("Could not save", writeError.Message);
    }

    [Fact]
    public async Task ResultsCommandExplainsAnUnrunSuite()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject("tests", new Projects.TestProjects_Tests().ProjectPath).Resource;
        await using var app = builder.Build();
        var result = await resource.Annotations.OfType<ResourceCommandAnnotation>().Single(command => command.Name == "test-results").ExecuteCommand(Context(app.Services));
        Assert.Contains("🟡 No tests run yet", result.Data!.Value);
    }

    [Fact]
    public async Task CanceledStartDoesNotCreateOrStopARun()
    {
        using var directory = new TestDirectory();
        var builder = DistributedApplication.CreateBuilder();
        await using var app = builder.Build();
        var run = new TestRun(new ProjectResource("tests"), directory.Path);
        var context = Context(app.Services);
        context = new ExecuteCommandContext
        {
            Services = context.Services,
            ResourceName = context.ResourceName,
            Logger = context.Logger,
            Arguments = context.Arguments,
            CancellationToken = new CancellationToken(true),
        };
        var result = await run.ExecuteAsync(context);
        Assert.True(result.Canceled);
        Assert.Null(run.Current);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        run.Prepare();
        ReportTests.Document("Passed").Save(Path.Combine(run.Current!.DirectoryPath, "results.trx"));
        await run.CompleteAsync(Snapshot(0), new CancellationToken(true));
        Assert.True((await run.Current.Completion.Task).Canceled);
        Assert.Contains("Result collection canceled", run.LastResult.Data!.Value);
    }

    internal static CustomResourceSnapshot Snapshot(int? exitCode) => new() { ResourceType = "Project", Properties = [], State = new(KnownResourceStates.Exited, null), ExitCode = exitCode };
    internal static ExecuteCommandContext Context(IServiceProvider services) => new()
    {
        Services = services,
        ResourceName = "tests",
        Logger = NullLogger.Instance,
        CancellationToken = TestContext.Current.CancellationToken,
        Arguments = new([]),
    };
}
