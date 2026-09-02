using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleRepositoryRefreshCoordinatorTests
{
    [Fact]
    public async Task Concurrent_refreshes_for_the_same_full_path_share_one_operation()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "shared-repository");
        var equivalentPath = Path.Combine(repositoryPath, ".");
        var coordinator = new ModuleRepositoryRefreshCoordinator();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;

        var first = coordinator.RefreshAsync(
            repositoryPath,
            async cancellationToken =>
            {
                Interlocked.Increment(ref refreshCount);
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken);
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var second = coordinator.RefreshAsync(
            equivalentPath,
            _ => throw new InvalidOperationException("A second refresh operation was started."),
            TestContext.Current.CancellationToken);
        releaseRefresh.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task Failed_refresh_is_removed_so_a_later_attempt_can_retry()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "shared-repository");
        var coordinator = new ModuleRepositoryRefreshCoordinator();
        var refreshCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(
            repositoryPath,
            _ =>
            {
                Interlocked.Increment(ref refreshCount);
                throw new InvalidOperationException("Refresh failed.");
            },
            TestContext.Current.CancellationToken));

        await coordinator.RefreshAsync(
            repositoryPath,
            _ =>
            {
                Interlocked.Increment(ref refreshCount);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public async Task Different_repository_paths_refresh_independently()
    {
        using var workspace = TemporaryDirectory.Create();
        var coordinator = new ModuleRepositoryRefreshCoordinator();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefreshes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        async Task RefreshAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                bothStarted.SetResult();
            }

            await releaseRefreshes.Task.WaitAsync(cancellationToken);
        }

        var first = coordinator.RefreshAsync(
            Path.Combine(workspace.Path, "catalog"),
            RefreshAsync,
            TestContext.Current.CancellationToken);
        var second = coordinator.RefreshAsync(
            Path.Combine(workspace.Path, "orders"),
            RefreshAsync,
            TestContext.Current.CancellationToken);

        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseRefreshes.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, startedCount);
    }
}
