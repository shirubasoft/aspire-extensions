using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A Kafka topic that Aspire registers before dependent resources start.
/// </summary>
public sealed class KafkaTopicResource : Resource, IResourceWithWaitSupport
{
    /// <summary>
    /// Initializes a Kafka topic resource.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="parent">The Kafka broker that owns the topic.</param>
    /// <param name="topicName">The topic name registered with Kafka.</param>
    /// <param name="partitionCount">The number of partitions created for a new topic.</param>
    /// <param name="replicationFactor">The replication factor created for a new topic.</param>
    public KafkaTopicResource(
        [ResourceName] string name,
        KafkaServerResource parent,
        string topicName,
        int partitionCount,
        short replicationFactor)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replicationFactor);

        Parent = parent;
        TopicName = topicName;
        PartitionCount = partitionCount;
        ReplicationFactor = replicationFactor;
    }

    /// <summary>
    /// Gets the Kafka broker that owns the topic.
    /// </summary>
    public KafkaServerResource Parent { get; }

    /// <summary>
    /// Gets the name registered with Kafka.
    /// </summary>
    public string TopicName { get; }

    /// <summary>
    /// Gets the partition count used when Aspire creates the topic.
    /// </summary>
    public int PartitionCount { get; }

    /// <summary>
    /// Gets the replication factor used when Aspire creates the topic.
    /// </summary>
    public short ReplicationFactor { get; }
}
