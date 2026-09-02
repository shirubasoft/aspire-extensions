using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class CloudflareResourceLifecycle
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        Func<string, Task> publishState,
        string successState,
        ILogger logger,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await publishState(ApplicationModel.KnownResourceStates.Starting).ConfigureAwait(false);

        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            await publishState(successState).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "{FailureMessage}", failureMessage);
            await publishState(ApplicationModel.KnownResourceStates.FailedToStart).ConfigureAwait(false);
            throw;
        }
    }

    public static Task RunIfAnyAsync<T>(
        IReadOnlyCollection<T> items,
        Func<CancellationToken, Task> operation,
        Func<string, Task> publishState,
        string successState,
        ILogger logger,
        string failureMessage,
        CancellationToken cancellationToken) =>
        items.Count == 0
            ? Task.CompletedTask
            : RunAsync(
                operation,
                publishState,
                successState,
                logger,
                failureMessage,
                cancellationToken);
}
