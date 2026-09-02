using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareResourceLifecycleTests
{
    [Fact]
    public async Task RunAsyncPublishesStartingAndSuccess()
    {
        var states = new List<string>();
        var operationCount = 0;

        await CloudflareResourceLifecycle.RunAsync(
            _ =>
            {
                operationCount++;
                return Task.CompletedTask;
            },
            state =>
            {
                states.Add(state);
                return Task.CompletedTask;
            },
            KnownResourceStates.Finished,
            NullLogger.Instance,
            "failed",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, operationCount);
        Assert.Equal([KnownResourceStates.Starting, KnownResourceStates.Finished], states);
    }

    [Fact]
    public async Task RunAsyncPublishesFailureAndRethrows()
    {
        var states = new List<string>();
        var failure = new InvalidOperationException("failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CloudflareResourceLifecycle.RunAsync(
                _ => Task.FromException(failure),
                state =>
                {
                    states.Add(state);
                    return Task.CompletedTask;
                },
                KnownResourceStates.Running,
                NullLogger.Instance,
                "route failed",
                TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(
            [KnownResourceStates.Starting, KnownResourceStates.FailedToStart],
            states);
    }

    [Fact]
    public async Task RunIfAnySkipsAnEmptyCollection()
    {
        var called = false;

        await CloudflareResourceLifecycle.RunIfAnyAsync(
            Array.Empty<string>(),
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            KnownResourceStates.Running,
            NullLogger.Instance,
            "failed",
            TestContext.Current.CancellationToken);

        Assert.False(called);
    }
}
