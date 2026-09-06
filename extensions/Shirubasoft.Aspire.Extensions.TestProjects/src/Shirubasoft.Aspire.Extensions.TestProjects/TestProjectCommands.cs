using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class TestProjectCommands
{
    internal static void Register(IResourceBuilder<ProjectResource> builder, TestProjectOptions options)
    {
        var runs = new TestRun(builder.Resource, Path.GetFullPath(options.ResultsDirectory, builder.ApplicationBuilder.AppHostDirectory));
        ConfigureLifecycle(builder, runs);
        builder.WithCommand("run-tests", "Run tests", context => runs.ExecuteAsync(context), new CommandOptions
        {
            IconName = "Play",
            IsHighlighted = true,
            Description = "Start the MTP project through Aspire and display its test results.",
            Progress = new() { Title = "Running tests", Message = "Starting the test project and waiting for results…" },
            UpdateState = context => CanStart(context.ResourceSnapshot.State?.Text) ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
        });
        builder.WithCommand("test-results", "Test results", context => Task.FromResult(runs.GetLastResult(context)),
            new CommandOptions { IconName = "DocumentBulletList", Description = "Show the latest completed test report." });
    }

    private static void ConfigureLifecycle(IResourceBuilder<ProjectResource> builder, TestRun runs)
    {
        builder.OnBeforeResourceStarted((_, _, _) => { runs.Prepare(); return Task.CompletedTask; });
        builder.WithArgs(context => { foreach (var argument in runs.Arguments) { context.Args.Add(argument); } });
        builder.OnResourceStopped((_, stopped, token) => runs.CompleteAsync(stopped.ResourceEvent.Snapshot, token));
    }

    private static readonly HashSet<string?> StartableStates = [KnownResourceStates.NotStarted, KnownResourceStates.Exited, KnownResourceStates.Finished, KnownResourceStates.FailedToStart];
    internal static bool CanStart(string? state) => StartableStates.Contains(state);
}
