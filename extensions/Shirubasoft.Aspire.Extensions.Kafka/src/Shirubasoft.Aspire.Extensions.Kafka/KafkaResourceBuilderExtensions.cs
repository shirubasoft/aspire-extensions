using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Adds Schema Registry and topic resources to Aspire Kafka brokers.
/// </summary>
public static class KafkaResourceBuilderExtensions
{
    private const int SchemaRegistryPort = 8081;

    /// <summary>
    /// Adds a Confluent Schema Registry container for the Kafka broker.
    /// </summary>
    /// <param name="builder">The Kafka broker resource.</param>
    /// <param name="name">The Schema Registry resource name.</param>
    /// <param name="port">The optional host port.</param>
    /// <returns>The Schema Registry resource builder.</returns>
    public static IResourceBuilder<KafkaSchemaRegistryResource> AddSchemaRegistry(
        this IResourceBuilder<KafkaServerResource> builder,
        [ResourceName] string name,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = new KafkaSchemaRegistryResource(name);
        return builder.ApplicationBuilder
            .AddResource(resource)
            .WithImage(SchemaRegistryContainerImageTags.Image, SchemaRegistryContainerImageTags.Tag)
            .WithImageRegistry(SchemaRegistryContainerImageTags.Registry)
            .WithHttpEndpoint(port: port, targetPort: SchemaRegistryPort, name: KafkaSchemaRegistryResource.PrimaryEndpointName)
            .WithEnvironment(context => ConfigureSchemaRegistry(context, builder.Resource, resource))
            .WithHttpHealthCheck("/subjects", endpointName: KafkaSchemaRegistryResource.PrimaryEndpointName)
            .WithParentRelationship(builder.Resource)
            .WaitFor(builder);
    }

    /// <summary>
    /// Adds a Kafka topic that Aspire creates when the AppHost starts.
    /// </summary>
    /// <param name="builder">The Kafka broker resource.</param>
    /// <param name="name">The Aspire resource name and default Kafka topic name.</param>
    /// <param name="topicName">The Kafka topic name. The resource name is used when omitted.</param>
    /// <param name="partitionCount">The number of partitions created for a new topic.</param>
    /// <param name="replicationFactor">The replication factor created for a new topic.</param>
    /// <returns>The Kafka topic resource builder.</returns>
    public static IResourceBuilder<KafkaTopicResource> AddTopic(
        this IResourceBuilder<KafkaServerResource> builder,
        [ResourceName] string name,
        string? topicName = null,
        int partitionCount = 1,
        short replicationFactor = 1)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = new KafkaTopicResource(
            name,
            builder.Resource,
            topicName ?? name,
            partitionCount,
            replicationFactor);

        builder.ApplicationBuilder.Services.TryAddSingleton<IKafkaTopicClientFactory, KafkaTopicClientFactory>();
        builder.ApplicationBuilder.Services.TryAddSingleton<IKafkaTopicProvisioner, KafkaTopicProvisioner>();

        var topic = builder.ApplicationBuilder
            .AddResource(resource)
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Kafka topic",
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new("Topic", resource.TopicName),
                    new("Partitions", resource.PartitionCount),
                    new("Replication factor", resource.ReplicationFactor),
                ],
            })
            .WaitFor(builder);

        return topic.OnInitializeResource((registeredTopic, @event, cancellationToken) =>
            KafkaTopicLifecycle.InitializeAsync(
                registeredTopic,
                token => @event.Eventing.PublishAsync(
                    new BeforeResourceStartedEvent(registeredTopic, @event.Services),
                    token),
                @event.Services.GetRequiredService<IKafkaTopicProvisioner>(),
                state => @event.Notifications.PublishUpdateAsync(
                    registeredTopic,
                    snapshot => snapshot with
                    {
                        StartTimeStamp = state == KnownResourceStates.Starting
                            ? DateTime.UtcNow
                            : snapshot.StartTimeStamp,
                        State = state,
                    }),
                @event.Logger,
                cancellationToken));
    }

    /// <summary>
    /// References a Schema Registry from a resource and injects both its HTTP address and connection string.
    /// </summary>
    /// <typeparam name="TDestination">The destination resource type.</typeparam>
    /// <param name="builder">The destination resource.</param>
    /// <param name="source">The Schema Registry resource.</param>
    /// <param name="connectionName">An optional connection string name.</param>
    /// <param name="optional">Whether a missing connection string is allowed.</param>
    /// <returns>The destination resource builder.</returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<KafkaSchemaRegistryResource> source,
        string? connectionName = null,
        bool optional = false)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        builder.WithReference((IResourceBuilder<IResourceWithConnectionString>)source, connectionName, optional);
        return builder.WithReference(source.Resource.PrimaryEndpoint);
    }

    private static void ConfigureSchemaRegistry(
        EnvironmentCallbackContext context,
        KafkaServerResource kafka,
        KafkaSchemaRegistryResource schemaRegistry)
    {
        context.EnvironmentVariables["SCHEMA_REGISTRY_HOST_NAME"] = schemaRegistry.Name;
        context.EnvironmentVariables["SCHEMA_REGISTRY_LISTENERS"] =
            $"http://0.0.0.0:{SchemaRegistryPort.ToString(CultureInfo.InvariantCulture)}";
        context.EnvironmentVariables["SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS"] =
            ReferenceExpression.Create(
                $"PLAINTEXT://{kafka.InternalEndpoint.Property(EndpointProperty.HostAndPort)}");
        context.EnvironmentVariables["SCHEMA_REGISTRY_KAFKASTORE_TOPIC_REPLICATION_FACTOR"] = "1";
    }

    private static class SchemaRegistryContainerImageTags
    {
        public const string Registry = "docker.io";
        public const string Image = "confluentinc/cp-schema-registry";
        public const string Tag = "8.1.0";
    }
}
