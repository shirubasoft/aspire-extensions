using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class CloudflareQuickTunnelUrlPublisher(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService)
{
    private const string PublicEndpointName = "public";
    private static readonly TimeSpan PublicUrlDiscoveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex QuickTunnelUrlPattern = new(
        @"https://[a-z0-9-]+\.trycloudflare\.com",
        RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant
            | RegexOptions.NonBacktracking);

    public async Task PublishAsync(
        CloudflareQuickTunnelResource tunnel,
        CancellationToken cancellationToken)
    {
        var logger = loggerService.GetLogger(tunnel);
        var publicUrl = await FindPublicUrlAsync(
            loggerService.WatchAsync(tunnel),
            PublicUrlDiscoveryTimeout,
            cancellationToken).ConfigureAwait(false);

        await PublishResultAsync(
            tunnel,
            publicUrl,
            url => notificationService.PublishUpdateAsync(tunnel, snapshot => snapshot with
            {
                Urls =
                [
                    ..snapshot.Urls.Where(existing =>
                        !string.Equals(
                            existing.Name,
                            PublicEndpointName,
                            StringComparison.OrdinalIgnoreCase)),
                    new UrlSnapshot(PublicEndpointName, url, IsInternal: false)
                    {
                        DisplayProperties = new("Public endpoint"),
                    },
                ],
            }),
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string?> FindPublicUrlAsync(
        IAsyncEnumerable<IReadOnlyList<LogLine>> logBatches,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await foreach (var lines in logBatches.WithCancellation(timeoutSource.Token))
            {
                var publicUrl = FindPublicUrl(lines);
                if (publicUrl is not null)
                {
                    return publicUrl;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private static string? FindPublicUrl(IReadOnlyList<LogLine> lines) =>
        lines
            .Select(line => QuickTunnelUrlPattern.Match(line.Content))
            .FirstOrDefault(match => match.Success)
            ?.Value;

    internal static async Task PublishResultAsync(
        CloudflareQuickTunnelResource tunnel,
        string? publicUrl,
        Func<string, Task> publishUrl,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (publicUrl is null)
        {
            logger.LogWarning("Cloudflare Quick Tunnel did not report a public URL.");
            return;
        }

        tunnel.PublicUrl = publicUrl;
        await publishUrl(publicUrl).ConfigureAwait(false);

        logger.LogInformation(
            "Cloudflare Quick Tunnel is available at {PublicUrl}.",
            publicUrl);
    }

}
