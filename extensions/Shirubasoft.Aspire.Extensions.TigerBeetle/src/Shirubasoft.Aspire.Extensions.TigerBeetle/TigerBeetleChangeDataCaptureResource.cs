namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a TigerBeetle change data capture job that publishes events to an AMQP 0.9.1 broker.
/// </summary>
[AspireExport]
public sealed class TigerBeetleChangeDataCaptureResource(
    [ResourceName] string name,
    TigerBeetleResource tigerBeetle,
    IResourceWithConnectionString amqpConnection,
    string publishExchange)
    : ContainerResource(name)
{
    /// <summary>Gets the TigerBeetle cluster read by this change data capture job.</summary>
    public TigerBeetleResource TigerBeetle { get; } = tigerBeetle;

    /// <summary>Gets the resource that supplies the AMQP connection string.</summary>
    public IResourceWithConnectionString AmqpConnection { get; } = amqpConnection;

    /// <summary>Gets the pre-existing AMQP exchange to which events are published.</summary>
    public string PublishExchange { get; } = publishExchange;

    /// <summary>Gets the optional AMQP virtual host override.</summary>
    public string? VirtualHost { get; internal set; }

    /// <summary>Gets the optional routing key used when publishing events.</summary>
    public string? PublishRoutingKey { get; internal set; }

    /// <summary>Gets the optional last published TigerBeetle timestamp.</summary>
    public string? TimestampLast { get; internal set; }

    internal IList<string> AdditionalArguments { get; } = [];
}
