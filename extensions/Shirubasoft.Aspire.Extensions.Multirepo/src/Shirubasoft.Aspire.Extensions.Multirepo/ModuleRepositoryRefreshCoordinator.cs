using System.Collections.Concurrent;

namespace Aspire.Hosting;

internal sealed class ModuleRepositoryRefreshCoordinator
{
    private readonly ConcurrentDictionary<string, RefreshOperation> _operations =
        new(PathSafety.Comparer);

    public Task RefreshAsync(
        string repositoryPath,
        Func<CancellationToken, Task> refreshAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(refreshAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var candidate = new RefreshOperation(refreshAsync, cancellationToken);
        var operation = _operations.GetOrAdd(key, candidate);
        var sharedTask = operation.Task;
        if (ReferenceEquals(operation, candidate))
        {
            _ = sharedTask.ContinueWith(
                completed => RemoveFailedOperation(key, operation, completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return sharedTask.WaitAsync(cancellationToken);
    }

    private void RemoveFailedOperation(
        string key,
        RefreshOperation operation,
        Task completed)
    {
        if (completed.IsCompletedSuccessfully)
        {
            return;
        }

        ((ICollection<KeyValuePair<string, RefreshOperation>>)_operations)
            .Remove(new KeyValuePair<string, RefreshOperation>(key, operation));
    }

    private sealed class RefreshOperation
    {
        private readonly Lazy<Task> _task;

        public RefreshOperation(
            Func<CancellationToken, Task> refreshAsync,
            CancellationToken cancellationToken)
        {
            _task = new Lazy<Task>(
                () => ExecuteAsync(refreshAsync, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task Task => _task.Value;

        private static async Task ExecuteAsync(
            Func<CancellationToken, Task> refreshAsync,
            CancellationToken cancellationToken) =>
            await refreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
