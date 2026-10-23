using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.TigerBeetle;

internal sealed class TigerBeetleTcpHealthCheck(Func<(string Host, int Port)?> primaryEndpointProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var primaryEndpoint = primaryEndpointProvider();
        if (primaryEndpoint is not { } endpoint)
        {
            return HealthCheckResult.Unhealthy("The TigerBeetle primary endpoint is not available yet.");
        }

        var endpointDisplay = FormatEndpoint(endpoint.Host, endpoint.Port);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"The TigerBeetle primary endpoint '{endpointDisplay}' is accepting connections.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                $"The TigerBeetle primary endpoint '{endpointDisplay}' is unavailable.",
                exception);
        }
    }

    private static string FormatEndpoint(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port}" : $"{host}:{port}";
}
