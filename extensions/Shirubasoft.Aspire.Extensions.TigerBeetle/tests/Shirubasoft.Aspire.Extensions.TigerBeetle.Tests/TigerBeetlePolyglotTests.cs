#pragma warning disable CS0618 // Aspire's public test inspection helpers are obsolete in favor of a lower-level configuration builder.

using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.TigerBeetle.Tests;

public sealed class TigerBeetlePolyglotTests
{
    [Fact]
    public async Task CustomWithReferenceInjectsTigerBeetlePropertiesForATigerBeetleSource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = AddAllocatedTigerBeetle(builder);
        var consumer = builder.AddExecutable("consumer", "echo", ".");

        var result = TryWithReference(consumer, tigerBeetle, "ledger", optional: false, name: null);

        Assert.Same(consumer, result);
        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();
        Assert.Equal("127.0.0.1:4567", environment["LEDGER_ADDRESSES"]);
    }

    [Fact]
    public void CustomWithReferenceIgnoresOtherSources()
    {
        var builder = DistributedApplication.CreateBuilder();
        var consumer = builder.AddExecutable("consumer", "echo", ".");
        var other = builder.AddContainer("other", "nginx");

        Assert.Null(TryWithReference(consumer, other, connectionName: null, optional: false, name: null));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "named")]
    public void CustomWithReferenceRejectsOptionalAndNamedReferences(bool optional, string? name)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");
        var consumer = builder.AddExecutable("consumer", "echo", ".");

        Assert.Throws<InvalidOperationException>(() =>
            TryWithReference(consumer, tigerBeetle, connectionName: null, optional, name));
    }

    [Fact]
    public void UnionChangeDataCaptureOverloadWaitsForARabbitMqServer()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");
        var rabbitMq = builder.AddRabbitMQ("rabbitmq");

        var cdc = TigerBeetleChangeDataCaptureResourceBuilderExtensions.AddChangeDataCapture(
            tigerBeetle,
            "tigerbeetle-cdc",
            (object)rabbitMq,
            "tigerbeetle");

        Assert.Same(rabbitMq.Resource, cdc.Resource.AmqpConnection);
        Assert.Contains(
            cdc.Resource.Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, rabbitMq.Resource));
    }

    [Fact]
    public void UnionChangeDataCaptureOverloadAcceptsAConnectionStringWithoutWaiting()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");
        var connection = builder.AddConnectionString("amqp");

        var cdc = TigerBeetleChangeDataCaptureResourceBuilderExtensions.AddChangeDataCapture(
            tigerBeetle,
            "tigerbeetle-cdc",
            (object)connection,
            "tigerbeetle");

        Assert.Same(connection.Resource, cdc.Resource.AmqpConnection);
        Assert.DoesNotContain(
            cdc.Resource.Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, connection.Resource));
    }

    [Fact]
    public void UnionChangeDataCaptureOverloadRejectsOtherResources()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        var exception = Assert.Throws<ArgumentException>(() =>
            TigerBeetleChangeDataCaptureResourceBuilderExtensions.AddChangeDataCapture(
                tigerBeetle,
                "tigerbeetle-cdc",
                builder.AddContainer("other", "nginx"),
                "tigerbeetle"));

        Assert.Equal("amqpConnection", exception.ParamName);
    }

    private static IResourceBuilder<TigerBeetleResource> AddAllocatedTigerBeetle(IDistributedApplicationBuilder builder) =>
        builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));

    private static IResourceBuilder<TDestination>? TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
        where TDestination : IResourceWithEnvironment =>
        TryWithReference<TigerBeetleResource, TDestination>(builder, source, connectionName, optional, name);

    private static IResourceBuilder<TDestination>? TryWithReference<TSource, TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
        where TSource : IResourceWithCustomWithReference<TSource>
        where TDestination : IResourceWithEnvironment =>
        TSource.TryWithReference(builder, source, connectionName, optional, name);
}
