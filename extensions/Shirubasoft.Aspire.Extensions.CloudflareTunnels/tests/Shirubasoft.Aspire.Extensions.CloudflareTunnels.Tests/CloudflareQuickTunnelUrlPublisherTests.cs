using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareQuickTunnelUrlPublisherTests
{
    [Fact]
    public async Task FindPublicUrlReturnsTheFirstTryCloudflareAddress()
    {
        var url = await CloudflareQuickTunnelUrlPublisher.FindPublicUrlAsync(
            Logs(
                "Starting connector",
                "INF https://small-river.trycloudflare.com connected"),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal("https://small-river.trycloudflare.com", url);
    }

    [Fact]
    public async Task FindPublicUrlReturnsNullWhenLogsCompleteWithoutAUrl()
    {
        var url = await CloudflareQuickTunnelUrlPublisher.FindPublicUrlAsync(
            Logs("Starting connector", "No public address"),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Null(url);
    }

    [Fact]
    public async Task FindPublicUrlReturnsNullAfterItsTimeout()
    {
        var url = await CloudflareQuickTunnelUrlPublisher.FindPublicUrlAsync(
            new NeverEndingLogStream(),
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);

        Assert.Null(url);
    }

    [Fact]
    public async Task PublishResultAddsThePublicUrl()
    {
        var tunnel = new CloudflareQuickTunnelResource("quick");
        string? publishedUrl = null;

        await CloudflareQuickTunnelUrlPublisher.PublishResultAsync(
            tunnel,
            "https://small-river.trycloudflare.com",
            url =>
            {
                publishedUrl = url;
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("https://small-river.trycloudflare.com", tunnel.PublicUrl);
        Assert.Equal(tunnel.PublicUrl, publishedUrl);
    }

    [Fact]
    public async Task PublishResultSkipsAnUnavailableUrl()
    {
        var tunnel = new CloudflareQuickTunnelResource("quick");
        var published = false;

        await CloudflareQuickTunnelUrlPublisher.PublishResultAsync(
            tunnel,
            null,
            _ =>
            {
                published = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(tunnel.PublicUrl);
        Assert.False(published);
    }

    private static async IAsyncEnumerable<IReadOnlyList<LogLine>> Logs(
        params string[] contents)
    {
        for (var index = 0; index < contents.Length; index++)
        {
            yield return [new LogLine(index, contents[index], false)];
            await Task.Yield();
        }
    }

    private sealed class NeverEndingLogStream :
        IAsyncEnumerable<IReadOnlyList<LogLine>>
    {
        public async IAsyncEnumerator<IReadOnlyList<LogLine>> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
