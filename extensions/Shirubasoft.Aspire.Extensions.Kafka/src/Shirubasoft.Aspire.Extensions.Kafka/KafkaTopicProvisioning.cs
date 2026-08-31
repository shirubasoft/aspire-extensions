using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal interface IKafkaTopicProvisioner
{
    Task RegisterAsync(KafkaTopicResource resource, CancellationToken cancellationToken);
}

internal sealed class KafkaTopicProvisioner(IKafkaTopicClientFactory clientFactory) : IKafkaTopicProvisioner
{
    public async Task RegisterAsync(KafkaTopicResource resource, CancellationToken cancellationToken)
    {
        var connectionString = await ((IResourceWithConnectionString)resource.Parent)
            .GetConnectionStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var bootstrapServers = RequireBootstrapServers(connectionString, resource.Parent.Name);

        using var client = clientFactory.Create(bootstrapServers);
        await client.CreateIfMissingAsync(
            resource.TopicName,
            resource.PartitionCount,
            resource.ReplicationFactor,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string RequireBootstrapServers(string? connectionString, string resourceName) =>
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new InvalidOperationException(
                $"The Kafka connection string for '{resourceName}' is unavailable.");
}

internal interface IKafkaTopicClientFactory
{
    IKafkaTopicClient Create(string bootstrapServers);
}

internal sealed class KafkaTopicClientFactory : IKafkaTopicClientFactory
{
    public IKafkaTopicClient Create(string bootstrapServers)
    {
        var adminClient = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = bootstrapServers,
        }).Build();
        return new KafkaTopicClient(adminClient);
    }
}

internal interface IKafkaTopicClient : IDisposable
{
    Task CreateIfMissingAsync(
        string topicName,
        int partitionCount,
        short replicationFactor,
        CancellationToken cancellationToken);
}

internal sealed class KafkaTopicClient(IAdminClient adminClient) : IKafkaTopicClient
{
    public async Task CreateIfMissingAsync(
        string topicName,
        int partitionCount,
        short replicationFactor,
        CancellationToken cancellationToken)
    {
        var topic = new TopicSpecification
        {
            Name = topicName,
            NumPartitions = partitionCount,
            ReplicationFactor = replicationFactor,
        };

        try
        {
            await adminClient
                .CreateTopicsAsync([topic])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CreateTopicsException exception) when (AllTopicsAlreadyExist(exception))
        {
        }
    }

    public void Dispose() => adminClient.Dispose();

    private static bool AllTopicsAlreadyExist(CreateTopicsException exception) =>
        exception.Results.Count > 0
        && exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists);
}
