using Aspire.Hosting.ApplicationModel;

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
