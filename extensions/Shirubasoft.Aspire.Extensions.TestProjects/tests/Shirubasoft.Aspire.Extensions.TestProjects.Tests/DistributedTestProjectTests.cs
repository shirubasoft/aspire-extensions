using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class DistributedTestProjectTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MtpRunsOnlyOnRequestAndSupportsFailureStopAndRerun()
    {
        using var directory = new TestDirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestProjects_AppHost>(
            [$"TestDemo:ResultsDirectory={directory.Path}"], token);
        var tests = builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "api-tests");
        var fail = false;
        var failStart = false;
        var slow = false;
        var filter = "*";
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.CreateResourceBuilder(tests)
            .WithEnvironment(context =>
            {
                if (failStart) throw new InvalidOperationException("Intentional startup failure");
                context.EnvironmentVariables["TestDemo__Fail"] = fail.ToString();
                context.EnvironmentVariables["TestDemo__Slow"] = slow.ToString();
            })
            .WithArgs(context => { context.Args.Add("--filter-method"); context.Args.Add(filter); })
            .OnResourceStopped((_, _, _) => { stopped.TrySetResult(); return Task.CompletedTask; });
        await using var app = await builder.BuildAsync(token);
        await app.StartAsync(token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", token);
        await app.ResourceNotifications.WaitForResourceAsync(tests.Name, KnownResourceStates.NotStarted, token);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var initial = await commands.ExecuteCommandAsync(tests, "test-results", token);
        Assert.Contains("No tests run yet", initial.Data!.Value);

        var passed = await commands.ExecuteCommandAsync(tests, "run-tests", token);
        output.WriteLine(passed.Data?.Value ?? passed.Message ?? "No result");
        Assert.True(passed.Success, passed.Data?.Value ?? passed.Message);
        Assert.Equal(CommandResultFormat.Markdown, passed.Data!.Format);
        Assert.True(passed.Data.DisplayImmediately);
        Assert.Contains("| 2 | 0 | 2 | 4 |", passed.Data.Value);

        await stopped.Task.WaitAsync(token);
        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fail = true;
        var failed = await commands.ExecuteCommandAsync(tests, "run-tests", token);
        Assert.False(failed.Success);
        Assert.Contains("| 1 | 1 | 2 | 4 |", failed.Data!.Value);
        Assert.Contains("Intentional demonstration failure", failed.Data.Value);
        await stopped.Task.WaitAsync(token);
        fail = false;
        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True((await commands.ExecuteCommandAsync(tests, KnownResourceCommands.StartCommand, token)).Success);
        await stopped.Task.WaitAsync(token);
        var direct = await commands.ExecuteCommandAsync(tests, "test-results", token);
        Assert.True(direct.Success, direct.Data?.Value ?? direct.Message);
        Assert.Contains("| 2 | 0 | 2 | 4 |", direct.Data!.Value);

        slow = true;
        filter = "*CanBeCanceled";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = commands.ExecuteCommandAsync(tests, "run-tests", cancel.Token);
        using var client = app.CreateHttpClient("api", "http");
        using var started = await client.GetAsync("/slow-started", token);
        started.EnsureSuccessStatusCode();
        var duplicate = await commands.ExecuteCommandAsync(tests, "run-tests", token);
        Assert.False(duplicate.Success);
        await cancel.CancelAsync();
        var canceled = await running;
        Assert.True(canceled.Canceled, canceled.Data?.Value ?? canceled.Message);
        Assert.Contains("🟡 Test run canceled", (await commands.ExecuteCommandAsync(tests, "test-results", token)).Data!.Value);
        Assert.Equal("Hello from Aspire", await client.GetStringAsync("/greeting", token));
        filter = "*GreetingComesFromTheReferencedApi";
        var rerun = await commands.ExecuteCommandAsync(tests, "run-tests", token);
        Assert.True(rerun.Success, rerun.Data?.Value ?? rerun.Message);
        failStart = true;
        var startFailure = await commands.ExecuteCommandAsync(tests, "run-tests", token);
        Assert.False(startFailure.Success);
        Assert.False((await commands.ExecuteCommandAsync(tests, "test-results", token)).Success);
        Assert.Equal(5, Directory.GetFiles(directory.Path, "results.md", SearchOption.AllDirectories).Length);
    }
}
