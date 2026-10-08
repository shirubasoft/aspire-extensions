using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

internal sealed class TestCloudflareApiClientFactory(ICloudflareApiClient client)
    : ICloudflareApiClientFactory
{
    public int CallCount { get; private set; }

    public ValueTask<ICloudflareApiClient> CreateAsync(
        CloudflareTunnelResource tunnel,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult(client);
    }
}

internal sealed class TestCloudflareApiClient : ICloudflareApiClient
{
    public CloudflareTunnelInfo? ExistingTunnel { get; set; }

    public CloudflareTunnelInfo CreatedTunnel { get; set; } =
        new("created-id", "tunnel", "inactive", null, null);

    public string TunnelToken { get; set; } = "tunnel-token";

    public TunnelConfiguration? Configuration { get; set; }

    public TunnelConfiguration? UpdatedConfiguration { get; private set; }

    public Func<string, CloudflareZoneInfo?> ZoneResolver { get; set; } =
        _ => new("zone-id", "example.com", "active");

    public List<string> ZoneLookups { get; } = [];

    public List<(string ZoneId, string Hostname, string TunnelId)> DnsUpserts { get; } = [];

    public Exception? FindTunnelException { get; set; }

    public int CreateTunnelCallCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task<CloudflareTunnelInfo?> FindTunnelByNameAsync(
        string name,
        CancellationToken cancellationToken) =>
        FindTunnelException is null
            ? Task.FromResult(ExistingTunnel)
            : Task.FromException<CloudflareTunnelInfo?>(FindTunnelException);

    public Task<CloudflareTunnelInfo> CreateTunnelAsync(
        string name,
        CancellationToken cancellationToken)
    {
        CreateTunnelCallCount++;
        return Task.FromResult(CreatedTunnel with { Name = name });
    }

    public Task<string> GetTunnelTokenAsync(
        string tunnelId,
        CancellationToken cancellationToken) =>
        Task.FromResult(TunnelToken);

    public Task<TunnelConfiguration?> GetTunnelConfigurationAsync(
        string tunnelId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Configuration);

    public Task UpdateTunnelConfigurationAsync(
        string tunnelId,
        TunnelConfiguration configuration,
        CancellationToken cancellationToken)
    {
        UpdatedConfiguration = configuration;
        return Task.CompletedTask;
    }

    public Task<CloudflareZoneInfo?> FindZoneByNameAsync(
        string domainName,
        CancellationToken cancellationToken)
    {
        ZoneLookups.Add(domainName);
        return Task.FromResult(ZoneResolver(domainName));
    }

    public Task<CloudflareDnsRecord> UpsertTunnelDnsRecordAsync(
        string zoneId,
        string hostname,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        DnsUpserts.Add((zoneId, hostname, tunnelId));
        return Task.FromResult(
            new CloudflareDnsRecord(
                "record-id",
                "CNAME",
                hostname,
                $"{tunnelId}.cfargotunnel.com",
                true,
                1));
    }

    public void Dispose() => IsDisposed = true;
}

#pragma warning disable ASPIREPIPELINES001
internal sealed class RecordingReportingStep : IReportingStep
{
    public List<RecordingReportingTask> Tasks { get; } = [];

    public Task<IReportingTask> CreateTaskAsync(
        string statusText,
        CancellationToken cancellationToken = default)
    {
        var task = new RecordingReportingTask(statusText);
        Tasks.Add(task);
        return Task.FromResult<IReportingTask>(task);
    }

    public Task<IReportingTask> CreateTaskAsync(
        MarkdownString statusText,
        CancellationToken cancellationToken = default) =>
        CreateTaskAsync(statusText.Value, cancellationToken);

    [Obsolete("Use Log(LogLevel, string) or Log(LogLevel, MarkdownString) instead.")]
    public void Log(LogLevel logLevel, string message, bool enableMarkdown)
    {
    }

    public void Log(LogLevel logLevel, string message)
    {
    }

    public void Log(LogLevel logLevel, MarkdownString message)
    {
    }

    public Task CompleteAsync(
        string completionText,
        CompletionState completionState = CompletionState.Completed,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CompleteAsync(
        MarkdownString completionText,
        CompletionState completionState = CompletionState.Completed,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingReportingTask(string statusText) : IReportingTask
{
    public string StatusText { get; } = statusText;

    public string? CompletionMessage { get; private set; }

    public CompletionState? CompletionState { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task UpdateAsync(
        string statusText,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateAsync(
        MarkdownString statusText,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CompleteAsync(
        string? completionMessage = null,
        CompletionState completionState = Pipelines.CompletionState.Completed,
        CancellationToken cancellationToken = default)
    {
        CompletionMessage = completionMessage;
        CompletionState = completionState;
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        MarkdownString completionMessage,
        CompletionState completionState = Pipelines.CompletionState.Completed,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(completionMessage.Value, completionState, cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
#pragma warning restore ASPIREPIPELINES001
