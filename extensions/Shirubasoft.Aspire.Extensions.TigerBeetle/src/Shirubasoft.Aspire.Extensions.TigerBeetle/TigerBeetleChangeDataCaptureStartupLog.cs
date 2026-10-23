using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.TigerBeetle;

// Reports where the CDC job publishes before it starts, and warns that `tigerbeetle amqp` has no native TLS.
internal static class TigerBeetleChangeDataCaptureStartupLog
{
    public static async Task WriteAsync(
        TigerBeetleChangeDataCaptureResource resource,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var connectionString = await resource.AmqpConnection
            .GetConnectionStringAsync(cancellationToken)
            .ConfigureAwait(false);
        Write(resource, logger, AmqpConnectionInfo.Parse(RequireConnectionString(resource, connectionString)));
    }

    public static void Write(TigerBeetleChangeDataCaptureResource resource, ILogger logger, AmqpConnectionInfo connection)
    {
        logger.LogInformation(
            "Starting TigerBeetle CDC resource {ResourceName} for cluster {ClusterId}. Publishing to exchange {PublishExchange} at {Scheme}://{Host}:{Port}.",
            resource.Name,
            resource.TigerBeetle.ClusterId,
            resource.PublishExchange,
            connection.Scheme,
            connection.Host,
            connection.Port);

        if (connection.UsesTls)
        {
            logger.LogWarning(
                "TigerBeetle CDC does not support native AMQP TLS. Resource {ResourceName} will connect to {Host}:{Port} without TLS. Configure a TLS tunnel for amqps:// connections.",
                resource.Name,
                connection.Host,
                connection.Port);
        }
    }

    private static string RequireConnectionString(TigerBeetleChangeDataCaptureResource resource, string? connectionString) =>
        connectionString ?? throw new DistributedApplicationException(
            $"The AMQP connection string for the '{resource.AmqpConnection.Name}' resource is unavailable.");
}
