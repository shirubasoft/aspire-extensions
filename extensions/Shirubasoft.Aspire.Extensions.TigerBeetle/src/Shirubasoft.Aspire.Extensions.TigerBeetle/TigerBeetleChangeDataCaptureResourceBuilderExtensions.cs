using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.TigerBeetle;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Adds and configures TigerBeetle change data capture resources.
/// </summary>
public static class TigerBeetleChangeDataCaptureResourceBuilderExtensions
{
    /// <summary>Adds a child change data capture resource that publishes TigerBeetle events to RabbitMQ.</summary>
    /// <param name="tigerBeetle">The TigerBeetle resource builder.</param>
    /// <param name="name">The change data capture resource name.</param>
    /// <param name="rabbitMq">The RabbitMQ server to which events are published.</param>
    /// <param name="publishExchange">The pre-existing RabbitMQ exchange to which events are published.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <remarks>
    /// This overload waits for RabbitMQ before starting. The exchange must exist before the change data capture resource starts.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="publishExchange" /> is empty or contains only white-space characters.</exception>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the exported connection-resource union overload.")]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> AddChangeDataCapture(
        this IResourceBuilder<TigerBeetleResource> tigerBeetle,
        [ResourceName] string name,
        IResourceBuilder<RabbitMQServerResource> rabbitMq,
        string publishExchange)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);

        return AddChangeDataCapture(
                tigerBeetle,
                name,
                (IResourceBuilder<IResourceWithConnectionString>)rabbitMq,
                publishExchange)
            .WaitFor(rabbitMq);
    }

    /// <summary>Adds a child change data capture resource that publishes TigerBeetle events to an AMQP 0.9.1 connection.</summary>
    /// <param name="tigerBeetle">The TigerBeetle resource builder.</param>
    /// <param name="name">The change data capture resource name.</param>
    /// <param name="amqpConnection">A resource that supplies an <c>amqp://</c> or <c>amqps://</c> connection string.</param>
    /// <param name="publishExchange">The pre-existing AMQP exchange to which events are published.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <remarks>
    /// This overload adds a reference relationship to <paramref name="amqpConnection" /> but does not wait for it.
    /// Add <c>WaitFor</c> at the call site when the connection resource has a lifecycle managed by the AppHost.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="publishExchange" /> is empty or contains only white-space characters.</exception>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the exported connection-resource union overload.")]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> AddChangeDataCapture(
        this IResourceBuilder<TigerBeetleResource> tigerBeetle,
        [ResourceName] string name,
        IResourceBuilder<IResourceWithConnectionString> amqpConnection,
        string publishExchange)
    {
        ArgumentNullException.ThrowIfNull(tigerBeetle);
        ArgumentNullException.ThrowIfNull(amqpConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishExchange);

        var resource = new TigerBeetleChangeDataCaptureResource(
            name,
            tigerBeetle.Resource,
            amqpConnection.Resource,
            publishExchange);

        tigerBeetle.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            (@event, cancellationToken) => TigerBeetleChangeDataCaptureStartupLog.WriteAsync(
                resource,
                @event.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource),
                cancellationToken));

        return tigerBeetle.ApplicationBuilder.AddResource(resource)
            .WithImage(TigerBeetleContainerImageTags.Image)
            .WithImageRegistry(TigerBeetleContainerImageTags.Registry)
            .WithImageTag(TigerBeetleContainerImageTags.Tag)
            .WithIconName("ArrowSync")
            .WithEntrypoint("/bin/sh")
            .WithArgs(context =>
            {
                context.Args.Add("-ec");
                context.Args.Add(TigerBeetleChangeDataCaptureStartupScript.Build(resource));
            })
            .WithEnvironment(
                TigerBeetleChangeDataCaptureStartupScript.TigerBeetleAddressesEnvironmentVariable,
                tigerBeetle.Resource.ClientAddressesExpression)
            .WithEnvironment(
                TigerBeetleChangeDataCaptureStartupScript.AmqpUriEnvironmentVariable,
                amqpConnection.Resource.ConnectionStringExpression)
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--security-opt");
                context.Args.Add("seccomp=unconfined");
            })
            .WithParentRelationship(tigerBeetle)
            .WithReferenceRelationship(amqpConnection)
            .WaitFor(tigerBeetle);
    }

    /// <summary>Adds a child change data capture resource for a polyglot AppHost.</summary>
    [AspireExport]
    internal static IResourceBuilder<TigerBeetleChangeDataCaptureResource> AddChangeDataCapture(
        this IResourceBuilder<TigerBeetleResource> tigerBeetle,
        [ResourceName] string name,
        [AspireUnion(
            typeof(IResourceBuilder<RabbitMQServerResource>),
            typeof(IResourceBuilder<IResourceWithConnectionString>))]
        object amqpConnection,
        string publishExchange)
    {
        return amqpConnection switch
        {
            IResourceBuilder<RabbitMQServerResource> rabbitMq =>
                AddChangeDataCapture(tigerBeetle, name, rabbitMq, publishExchange),
            IResourceBuilder<IResourceWithConnectionString> connection =>
                AddChangeDataCapture(tigerBeetle, name, connection, publishExchange),
            _ => throw new ArgumentException(
                "The AMQP connection must be a RabbitMQ resource or a resource with a connection string.",
                nameof(amqpConnection)),
        };
    }

    /// <summary>Overrides the RabbitMQ virtual host parsed from the connection string.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="virtualHost">The RabbitMQ virtual host.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="virtualHost" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithVirtualHost(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        string virtualHost)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);

        builder.Resource.VirtualHost = virtualHost;
        return builder;
    }

    /// <summary>Sets the routing key used when publishing change data capture events.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="publishRoutingKey">The AMQP routing key.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="publishRoutingKey" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithPublishRoutingKey(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        string publishRoutingKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishRoutingKey);

        builder.Resource.PublishRoutingKey = publishRoutingKey;
        return builder;
    }

    /// <summary>Overrides the last published timestamp from which the change data capture job resumes.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="timestampLast">A TigerBeetle timestamp in unsigned 64-bit decimal form. Use <c>0</c> to replay all events.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="timestampLast" /> is not an unsigned 64-bit decimal value.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithTimestampLast(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        string timestampLast)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!ulong.TryParse(timestampLast, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("The last timestamp must be an unsigned 64-bit decimal value.", nameof(timestampLast));
        }

        builder.Resource.TimestampLast = timestampLast;
        return builder;
    }

    /// <summary>Overrides the last published timestamp from which the change data capture job resumes.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="timestampLast">A TigerBeetle timestamp. Use <c>0</c> to replay all events.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    [AspireExportIgnore(Reason = "System.UInt64 has no portable JavaScript representation. Use the decimal string overload in polyglot AppHosts.")]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithTimestampLast(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        ulong timestampLast)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithTimestampLast(timestampLast.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Adds raw arguments to the end of the <c>tigerbeetle amqp</c> command.</summary>
    /// <param name="builder">The TigerBeetle change data capture resource builder.</param>
    /// <param name="args">The arguments to append.</param>
    /// <returns>The TigerBeetle change data capture resource builder.</returns>
    /// <remarks>
    /// Arguments are appended after integration-owned options. TigerBeetle performs final validation when arguments conflict.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> or <paramref name="args" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">An argument is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleChangeDataCaptureResource> WithCdcArgs(
        this IResourceBuilder<TigerBeetleChangeDataCaptureResource> builder,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        foreach (var argument in args)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument);
            builder.Resource.AdditionalArguments.Add(argument);
        }

        return builder;
    }
}
