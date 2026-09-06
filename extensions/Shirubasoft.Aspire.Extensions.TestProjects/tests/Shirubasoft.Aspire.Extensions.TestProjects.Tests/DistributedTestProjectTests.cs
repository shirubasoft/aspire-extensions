using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class DistributedTestProjectTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealRunnersUseInjectedEndpointsAndCanFailCancelAndRerun()
    {
        using var directory = new TestDirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestProjects_AppHost>(
            ["TestDemo:Fail=true", "TestDemo:Slow=true", $"TestDemo:ResultsDirectory={directory.Path}"], token);
        await using var app = await builder.BuildAsync(token);
        await app.StartAsync(token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", token);
        var tests = builder.Resources.OfType<TestProjectResource>().Single(resource => resource.Name == "api-tests");
        var mtp = builder.Resources.OfType<TestProjectResource>().Single(resource => resource.Name == "mtp-tests");
        var commands = app.Services.GetRequiredService<ResourceCommandService>();

        var failed = await commands.ExecuteCommandAsync(tests, "run-tests", Filter("Category!=Slow"), token);
        output.WriteLine(failed.Data?.Value ?? failed.Message ?? "No result");
        Assert.False(failed.Success);
        Assert.Equal(CommandResultFormat.Markdown, failed.Data!.Format);
        Assert.Contains("| 1 | 1 | 1 | 3 |", failed.Data.Value);
        Assert.Contains("Intentional demonstration failure", failed.Data.Value);

        var passed = await commands.ExecuteCommandAsync(tests, "run-tests", Filter("FullyQualifiedName!~FailsWhenRequested&Category!=Slow"), token);
        output.WriteLine(passed.Data?.Value ?? passed.Message ?? "No result");
        Assert.True(passed.Success, passed.Data?.Value ?? passed.Message);
        Assert.Contains("| 1 | 0 | 1 | 2 |", passed.Data!.Value);
        var latest = await commands.ExecuteCommandAsync(tests, "test-results", token);
        Assert.Equal(passed.Data.Value, latest.Data!.Value);

        var mtpResult = await commands.ExecuteCommandAsync(mtp, "run-tests", token);
        output.WriteLine(mtpResult.Data?.Value ?? mtpResult.Message ?? "No result");
        Assert.True(mtpResult.Success, mtpResult.Data?.Value ?? mtpResult.Message);
        Assert.Contains("| 2 | 0 | 2 | 4 |", mtpResult.Data!.Value);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var slowRun = commands.ExecuteCommandAsync(tests, "run-tests", Filter("FullyQualifiedName~CanBeCanceled"), cancel.Token);
        using var client = app.CreateHttpClient("api", "http");
        using var started = await client.GetAsync("/slow-started", token);
        started.EnsureSuccessStatusCode();
        await cancel.CancelAsync();
        var canceled = await slowRun;
        Assert.True(canceled.Canceled, canceled.Data?.Value ?? canceled.Message);
        var canceledResult = await commands.ExecuteCommandAsync(tests, "test-results", token);
        Assert.Contains("🟡 Test run canceled", canceledResult.Data!.Value);
        Assert.Equal("Hello from Aspire", await client.GetStringAsync("/greeting", token));
        var rerun = await commands.ExecuteCommandAsync(tests, "run-tests", Filter("FullyQualifiedName~GreetingComesFromTheReferencedApi"), token);
        Assert.True(rerun.Success, rerun.Data?.Value ?? rerun.Message);
        Assert.Equal(5, Directory.GetFiles(directory.Path, "results.md", SearchOption.AllDirectories).Length);
    }

    private static InteractionInputCollection Filter(string value) => new([
        new() { Name = "filter", Label = "Test filter", InputType = InputType.Text, Value = value },
    ]);
}
