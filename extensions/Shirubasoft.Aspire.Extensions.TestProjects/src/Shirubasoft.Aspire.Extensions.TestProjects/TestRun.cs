using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

internal sealed class TestRun(ProjectResource resource, string resultsDirectory)
{
    private TestRunSession? _current;
    private int _commandRunning;
    private ExecuteCommandResult _lastResult = TestResultMarkdown.Create(true, "No test results", "# 🟡 No tests run yet\n\nChoose **Run tests** or **Start** to run this suite.");

    internal TestRunSession? Current => Volatile.Read(ref _current);
    internal ExecuteCommandResult LastResult => Volatile.Read(ref _lastResult);
    internal string[] Arguments => ["--report-trx", "--report-trx-filename", "results.trx", "--results-directory", Current!.DirectoryPath];

    internal void Prepare()
    {
        var session = new TestRunSession(Path.Combine(resultsDirectory, resource.Name, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(session.DirectoryPath);
        Volatile.Write(ref _current, session);
    }

    internal async Task CompleteAsync(CustomResourceSnapshot snapshot, CancellationToken token)
    {
        var session = Current;
        if (session is null)
        {
            return;
        }
        var result = await TestRunArtifacts.ReadAndSaveAsync(resource.Name, session, snapshot, token);
        Volatile.Write(ref _lastResult, result);
        session.Completion.TrySetResult(result);
    }

    internal async Task<ExecuteCommandResult> ExecuteAsync(ExecuteCommandContext context)
    {
        if (Interlocked.CompareExchange(ref _commandRunning, 1, 0) != 0)
        {
            return TestResultMarkdown.Error("Tests already running", "Wait for the active run or stop the test resource.");
        }
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return await StartAndWaitAsync(context);
        }
        catch (OperationCanceledException)
        {
            return TestResultMarkdown.Error("Test run canceled", "The start request was canceled.", canceled: true);
        }
        finally
        {
            Interlocked.Exchange(ref _commandRunning, 0);
        }
    }

    private async Task<ExecuteCommandResult> StartAndWaitAsync(ExecuteCommandContext context)
    {
        var commands = context.Services.GetRequiredService<ResourceCommandService>();
        var previous = Current;
        try
        {
            var started = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand, context.CancellationToken);
            started = CheckStartup(context, started);
            if (!started.Success)
            {
                return RecordStartFailure(started);
            }
            var session = Current;
            if (session == previous)
            {
                return TestResultMarkdown.Error("Tests already running", "The test project did not start a new run.");
            }
            return await session!.Completion.Task.WaitAsync(context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await StopAsync(commands, previous);
        }
    }

    internal ExecuteCommandResult GetLastResult(ExecuteCommandContext context) => CheckStartup(context, LastResult);

    private ExecuteCommandResult CheckStartup(ExecuteCommandContext context, ExecuteCommandResult result)
    {
        if (GetResourceState(context) == KnownResourceStates.FailedToStart)
        {
            return TestResultMarkdown.Error("Could not start tests", "See the test resource's console logs in Aspire for the startup error.");
        }
        return result;
    }

    private static string? GetResourceState(ExecuteCommandContext context)
    {
        var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
        notifications.TryGetCurrentState(context.ResourceName, out var state);
        return state?.Snapshot.State?.Text;
    }

    private ExecuteCommandResult RecordStartFailure(ExecuteCommandResult started)
    {
        var result = TestResultMarkdown.Error("Could not start tests", started.Message ?? "Aspire could not start the test project.");
        Volatile.Write(ref _lastResult, result);
        Current?.Completion.TrySetResult(result);
        return result;
    }

    private async Task<ExecuteCommandResult> StopAsync(ResourceCommandService commands, TestRunSession? previous)
    {
        var session = Current;
        if (session == previous)
        {
            return TestResultMarkdown.Error("Test run canceled", "The start request was canceled.", canceled: true);
        }
        session!.Cancel();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StopCommand, cleanup.Token);
        return await session.Completion.Task.WaitAsync(cleanup.Token);
    }
}

internal sealed class TestRunSession(string directoryPath)
{
    private int _canceled;
    internal string DirectoryPath { get; } = directoryPath;
    internal TaskCompletionSource<ExecuteCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Canceled => Volatile.Read(ref _canceled) != 0;
    internal void Cancel() => Interlocked.Exchange(ref _canceled, 1);
}
