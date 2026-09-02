namespace Aspire.Hosting;

internal static class ModuleOperationTimeout
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            timeout,
            description,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TResult> RunAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{description} exceeded the configured timeout of {timeout}.",
                exception);
        }
    }
}
