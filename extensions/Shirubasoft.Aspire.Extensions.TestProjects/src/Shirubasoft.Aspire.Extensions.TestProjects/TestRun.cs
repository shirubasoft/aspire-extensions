using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

internal sealed class TestRun(TestProjectResource resource, TestProjectOptions options, string appHostDirectory)
{
    private int _running;
    private ExecuteCommandResult _lastResult = TestResultMarkdown.Create(true, "No test results", "# 🟡 No tests run yet\n\nChoose **Run tests** to run this suite.");

    internal bool IsRunning => Volatile.Read(ref _running) != 0;
    internal string DirectoryPath { get; private set; } = "";
    internal ExecuteCommandResult LastResult => Volatile.Read(ref _lastResult);

    internal async Task<ExecuteCommandResult> ExecuteAsync(ExecuteCommandContext context, Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return TestResultMarkdown.Error("Tests already running", "Wait for the active run or cancel it in its progress dialog.");
        }
        try
        {
            await PublishAsync(context, new("Running", KnownResourceStateStyles.Info));
            var result = await ExecuteAndSaveAsync(context, execute);
            Volatile.Write(ref _lastResult, result);
            await PublishAsync(context, ResultState(result));
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<ExecuteCommandResult> ExecuteAndSaveAsync(ExecuteCommandContext context, Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute)
    {
        try
        {
            DirectoryPath = Path.Combine(Path.GetFullPath(options.ResultsDirectory, appHostDirectory), resource.Name, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            var result = await ExecuteWithTimeoutAsync(context, execute);
            using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "results.md"), result.Data!.Value, saveTimeout.Token);
            return result;
        }
        catch (Exception exception)
        {
            return TestResultMarkdown.Error("Could not run tests or save results", exception.Message);
        }
    }

    private async Task<ExecuteCommandResult> ExecuteWithTimeoutAsync(ExecuteCommandContext context, Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute)
    {
        var lifetime = context.Services.GetRequiredService<IHostApplicationLifetime>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        timeout.CancelAfter(options.Timeout);
        var invocation = new ExecuteCommandContext
        {
            Services = context.Services,
            ResourceName = context.ResourceName,
            Logger = context.Logger,
            Arguments = context.Arguments,
            CancellationToken = timeout.Token,
        };
        var result = await execute(invocation);
        return Normalize(result, context.CancellationToken.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested);
    }

    internal static ExecuteCommandResult Normalize(ExecuteCommandResult result, bool canceled)
    {
        if (result.Canceled)
        {
            return CancellationResult(canceled);
        }
        return result.Data is null ? RunnerError(result) : result;
    }

    private static ExecuteCommandResult CancellationResult(bool canceled) =>
        TestResultMarkdown.Error(canceled ? "Test run canceled" : "Test run timed out",
            "The test process was stopped. Any partial TRX files remain in the run directory.", canceled);

    private static ExecuteCommandResult RunnerError(ExecuteCommandResult result) =>
        TestResultMarkdown.Error("Test runner error", result.Message ?? "The runner did not return results.");

    private Task PublishAsync(ExecuteCommandContext context, ResourceStateSnapshot state) =>
        context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(resource,
            snapshot => snapshot with { State = state });

    private static ResourceStateSnapshot ResultState(ExecuteCommandResult result) => result.Canceled
        ? new("Canceled", KnownResourceStateStyles.Warn)
        : new(result.Message ?? "Finished", ResultStyle(result.Success));

    private static string ResultStyle(bool success) => success ? KnownResourceStateStyles.Success : KnownResourceStateStyles.Error;
}
