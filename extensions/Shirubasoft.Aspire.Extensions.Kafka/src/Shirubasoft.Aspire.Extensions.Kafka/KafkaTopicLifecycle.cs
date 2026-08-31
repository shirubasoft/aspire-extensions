using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class KafkaTopicLifecycle
{
    public static async Task InitializeAsync(
        KafkaTopicResource resource,
        Func<CancellationToken, Task> waitForDependencies,
        IKafkaTopicProvisioner provisioner,
        Func<string, Task> publishState,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await publishState(ApplicationModel.KnownResourceStates.Starting);

        try
        {
            await waitForDependencies(cancellationToken);
            await provisioner.RegisterAsync(resource, cancellationToken);
            await publishState(ApplicationModel.KnownResourceStates.Running);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to register Kafka topic '{TopicName}'.", resource.TopicName);
            await publishState(ApplicationModel.KnownResourceStates.FailedToStart);
        }
    }
}
