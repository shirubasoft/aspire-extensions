using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class KafkaResourceBuilderExtensionsTests
{
    [Fact]
    public async Task AddSchemaRegistryConfiguresAWaitableContainer()
    {
        var builder = DistributedApplication.CreateBuilder();
        var kafka = builder.AddKafka("kafka");

        var registry = kafka.AddSchemaRegistry("schema-registry", port: 18081);

        Assert.IsAssignableFrom<IResourceWithWaitSupport>(registry.Resource);
        Assert.IsAssignableFrom<IResourceWithConnectionString>(registry.Resource);
        var image = Assert.Single(registry.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("docker.io", image.Registry);
        Assert.Equal("confluentinc/cp-schema-registry", image.Image);
        Assert.Equal("8.1.0", image.Tag);
        var endpoint = Assert.Single(registry.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(18081, endpoint.Port);
        Assert.Equal(8081, endpoint.TargetPort);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Contains(registry.Resource.Annotations, annotation => annotation is WaitAnnotation);
#pragma warning disable CS0618 // The public test helper is the supported assertion API in Aspire 13.4.
        var environment = await registry.Resource.GetEnvironmentVariableValuesAsync(
            DistributedApplicationOperation.Publish);
#pragma warning restore CS0618
        Assert.Equal("schema-registry", environment["SCHEMA_REGISTRY_HOST_NAME"]);
        Assert.Equal("http://0.0.0.0:8081", environment["SCHEMA_REGISTRY_LISTENERS"]);
        Assert.Contains(
            "kafka.bindings.internal",
            environment["SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS"],
            StringComparison.Ordinal);
        Assert.Equal("1", environment["SCHEMA_REGISTRY_KAFKASTORE_TOPIC_REPLICATION_FACTOR"]);
    }

    [Fact]
    public async Task SchemaRegistryExposesAddressAndConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();
        var kafka = builder.AddKafka("kafka");
        var registry = kafka
            .AddSchemaRegistry("schema-registry")
            .WithEndpoint("http", endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 18081));

        var registryConnection = (IResourceWithConnectionString)registry.Resource;
        var connectionString = await registryConnection.GetConnectionStringAsync(
            TestContext.Current.CancellationToken);
        var properties = registryConnection
            .GetConnectionProperties()
            .ToDictionary(property => property.Key, property => property.Value.ValueExpression);

        Assert.Equal("http://localhost:18081", connectionString);
        Assert.Equal("{schema-registry.bindings.http.url}", registry.Resource.Address.ValueExpression);
        Assert.Equal("{schema-registry.bindings.http.url}", properties["Address"]);
        Assert.Equal("{schema-registry.bindings.http.host}", properties["Host"]);
        Assert.Equal("{schema-registry.bindings.http.port}", properties["Port"]);
    }

    [Fact]
    public async Task WithReferenceAddsAddressAndConnectionStringValues()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder
            .AddKafka("kafka")
            .AddSchemaRegistry("schema-registry")
            .WithEndpoint("http", endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 18081));
        var consumer = builder.AddContainer("consumer", "alpine");

        consumer.WithReference(registry);

        Assert.Equal(2, consumer.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
        var relationships = consumer.Resource.Annotations.OfType<ResourceRelationshipAnnotation>();
        Assert.Contains(relationships, relationship => relationship.Resource == registry.Resource);
#pragma warning disable CS0618 // The public test helper is the supported assertion API in Aspire 13.4.
        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync(
            DistributedApplicationOperation.Publish);
#pragma warning restore CS0618
        Assert.Equal("{schema-registry.connectionString}", environment["ConnectionStrings__schema-registry"]);
        Assert.Equal("{schema-registry.bindings.http.url}", environment["SCHEMA_REGISTRY_ADDRESS"]);
        Assert.Contains("schema-registry.bindings.http", environment["SCHEMA_REGISTRY_HTTP"], StringComparison.Ordinal);
        Assert.Contains(
            "schema-registry.bindings.http",
            environment["services__schema-registry__http__0"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddTopicUsesTheResourceNameByDefaultAndWaitsForKafka()
    {
        var builder = DistributedApplication.CreateBuilder();
        var kafka = builder.AddKafka("kafka");

        var topic = kafka.AddTopic("orders", partitionCount: 3);

        Assert.IsAssignableFrom<IResourceWithWaitSupport>(topic.Resource);
        Assert.Equal(kafka.Resource, topic.Resource.Parent);
        Assert.Equal("orders", topic.Resource.TopicName);
        Assert.Equal(3, topic.Resource.PartitionCount);
        Assert.Equal(1, topic.Resource.ReplicationFactor);
        Assert.Contains(topic.Resource.Annotations, annotation => annotation is WaitAnnotation);
    }

    [Fact]
    public void AddTopicPreservesAnExplicitTopicName()
    {
        var builder = DistributedApplication.CreateBuilder();

        var topic = builder
            .AddKafka("kafka")
            .AddTopic("orders-resource", topicName: "orders.events", replicationFactor: 2);

        Assert.Equal("orders.events", topic.Resource.TopicName);
        Assert.Equal(2, topic.Resource.ReplicationFactor);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void AddTopicRejectsNonPositiveCreationSettings(int partitionCount, short replicationFactor)
    {
        var builder = DistributedApplication.CreateBuilder();
        var kafka = builder.AddKafka("kafka");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            kafka.AddTopic(
                "orders",
                partitionCount: partitionCount,
                replicationFactor: replicationFactor));
    }

    [Fact]
    public void AddTopicRejectsABlankKafkaTopicName()
    {
        var builder = DistributedApplication.CreateBuilder();
        var kafka = builder.AddKafka("kafka");

        Assert.Throws<ArgumentException>(() => kafka.AddTopic("orders", topicName: " "));
    }

    [Fact]
    public async Task TopicLifecycleMarksSuccessfulRegistrationAsRunning()
    {
        var resource = CreateTopicResource();
        var provisioner = new TestTopicProvisioner();
        var states = new List<string>();

        await KafkaTopicLifecycle.InitializeAsync(
            resource,
            _ => Task.CompletedTask,
            provisioner,
            state =>
            {
                states.Add(state);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, provisioner.CallCount);
        Assert.Equal([KnownResourceStates.Starting, KnownResourceStates.Running], states);
    }

    [Fact]
    public async Task TopicLifecycleMarksFailedRegistrationAsFailedToStart()
    {
        var resource = CreateTopicResource();
        var provisioner = new TestTopicProvisioner(new InvalidOperationException("registration failed"));
        var states = new List<string>();

        await KafkaTopicLifecycle.InitializeAsync(
            resource,
            _ => Task.CompletedTask,
            provisioner,
            state =>
            {
                states.Add(state);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal([KnownResourceStates.Starting, KnownResourceStates.FailedToStart], states);
    }

    [Fact]
    public async Task TopicProvisionerUsesTheBrokerConnectionString()
    {
        var builder = DistributedApplication.CreateBuilder();
        var kafka = builder
            .AddKafka("kafka")
            .WithEndpoint("tcp", endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 19092));
        var resource = new KafkaTopicResource("orders", kafka.Resource, "orders.events", 3, 1);
        var factory = new TestTopicClientFactory();

        await new KafkaTopicProvisioner(factory).RegisterAsync(
            resource,
            TestContext.Current.CancellationToken);

        Assert.Equal("localhost:19092", factory.BootstrapServers);
        Assert.Equal(("orders.events", 3, (short)1), factory.Client.Request);
        Assert.True(factory.Client.IsDisposed);
    }

    [Fact]
    public void TopicProvisionerRejectsAnUnavailableConnectionString()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            KafkaTopicProvisioner.RequireBootstrapServers(null, "kafka"));

        Assert.Contains("kafka", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ErrorCode.TopicAlreadyExists, false)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, true)]
    public async Task TopicClientOnlySuppressesExistingTopicErrors(ErrorCode errorCode, bool shouldThrow)
    {
        var adminClient = Substitute.For<IAdminClient>();
        var exception = new CreateTopicsException(
        [
            new CreateTopicReport
            {
                Topic = "orders",
                Error = new Error(errorCode),
            },
        ]);
        adminClient
            .CreateTopicsAsync(
                Arg.Any<IEnumerable<TopicSpecification>>(),
                Arg.Any<CreateTopicsOptions>())
            .Returns(Task.FromException(exception));
        using var client = new KafkaTopicClient(adminClient);

        var operation = client.CreateIfMissingAsync(
            "orders",
            3,
            1,
            TestContext.Current.CancellationToken);

        if (shouldThrow)
        {
            await Assert.ThrowsAsync<CreateTopicsException>(() => operation);
        }
        else
        {
            await operation;
        }
    }

    private static KafkaTopicResource CreateTopicResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        return new KafkaTopicResource("orders", builder.AddKafka("kafka").Resource, "orders", 1, 1);
    }

    private sealed class TestTopicProvisioner(Exception? exception = null) : IKafkaTopicProvisioner
    {
        public int CallCount { get; private set; }

        public Task RegisterAsync(KafkaTopicResource resource, CancellationToken cancellationToken)
        {
            CallCount++;
            return exception is null ? Task.CompletedTask : Task.FromException(exception);
        }
    }

    private sealed class TestTopicClientFactory : IKafkaTopicClientFactory
    {
        public string? BootstrapServers { get; private set; }

        public TestTopicClient Client { get; } = new();

        public IKafkaTopicClient Create(string bootstrapServers)
        {
            BootstrapServers = bootstrapServers;
            return Client;
        }
    }

    private sealed class TestTopicClient : IKafkaTopicClient
    {
        public (string TopicName, int PartitionCount, short ReplicationFactor)? Request { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task CreateIfMissingAsync(
            string topicName,
            int partitionCount,
            short replicationFactor,
            CancellationToken cancellationToken)
        {
            Request = (topicName, partitionCount, replicationFactor);
            return Task.CompletedTask;
        }

        public void Dispose() => IsDisposed = true;
    }
}
