using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

// The AppHost owns its tunnel's ingress rules and the DNS records that carry the
// tunnel's owner comment. Reconciliation makes Cloudflare match the declared routes
// and leaves every record without that comment unchanged.
internal static class CloudflareRouteOwnership
{
    private const string RecordType = "CNAME";

    public static string OwnerComment(CloudflareTunnelResource tunnel) =>
        $"managed-by=aspire:{tunnel.Name}";

    public static CloudflareDnsRecordRequest DesiredRecord(
        CloudflareTunnelResource tunnel,
        string tunnelId,
        string hostname) =>
        new(
            RecordType,
            hostname.ToLowerInvariant(),
            $"{tunnelId}.cfargotunnel.com",
            Proxied: true,
            Ttl: 1,
            OwnerComment(tunnel));

    // A CNAME admits no other record with the same name, so any record that the
    // tunnel cannot manage blocks the route's record.
    public static DnsRecordChange Plan(
        CloudflareDnsRecordRequest desired,
        IReadOnlyList<CloudflareDnsRecord> existing) =>
        existing.FirstOrDefault(record => !CanManage(record, desired)) is { } unmanaged
            ? new DnsRecordChange.Conflict(unmanaged)
            : PlanManagedRecord(desired, existing.FirstOrDefault());

    public static IEnumerable<CloudflareDnsRecord> FindStaleRecords(
        IEnumerable<CloudflareDnsRecord> ownedRecords,
        IEnumerable<PublishedRouteResource> routes)
    {
        var declared = routes
            .Select(route => route.Hostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ownedRecords.Where(record => !declared.Contains(record.Name));
    }

    // The declared routes are the complete ingress, followed by the required catch-all rule.
    public static TunnelConfiguration DesiredConfiguration(IEnumerable<IngressRule> rules) =>
        new()
        {
            Ingress =
            [
                .. rules,
                new IngressRule
                {
                    Service = "http_status:404",
                },
            ],
        };

    public static bool IsUnchanged(TunnelConfiguration current, TunnelConfiguration desired) =>
        current.Ingress.SequenceEqual(desired.Ingress);

    private static DnsRecordChange PlanManagedRecord(
        CloudflareDnsRecordRequest desired,
        CloudflareDnsRecord? current) =>
        current is null
            ? new DnsRecordChange.Create()
            : PlanExistingRecord(desired, current);

    private static DnsRecordChange PlanExistingRecord(
        CloudflareDnsRecordRequest desired,
        CloudflareDnsRecord current) =>
        ToRequest(current) == desired
            ? new DnsRecordChange.Unchanged()
            : new DnsRecordChange.Update(current.Id);

    private static CloudflareDnsRecordRequest ToRequest(CloudflareDnsRecord record) =>
        new(record.Type, record.Name, record.Content, record.Proxied, record.Ttl, record.Comment);

    private static bool CanManage(CloudflareDnsRecord record, CloudflareDnsRecordRequest desired) =>
        record.Comment == desired.Comment || IsUnclaimedTunnelRecord(record, desired);

    // An uncommented CNAME that already points at this tunnel routes the hostname the
    // same way, so the tunnel adopts it by adding the owner comment.
    private static bool IsUnclaimedTunnelRecord(
        CloudflareDnsRecord record,
        CloudflareDnsRecordRequest desired) =>
        string.IsNullOrEmpty(record.Comment)
        && record.Type == RecordType
        && string.Equals(record.Content, desired.Content, StringComparison.OrdinalIgnoreCase);
}

internal abstract record DnsRecordChange
{
    private DnsRecordChange()
    {
    }

    public abstract Task ApplyAsync(DnsRecordChangeContext context, CancellationToken cancellationToken);

    public sealed record Create : DnsRecordChange
    {
        public override async Task ApplyAsync(
            DnsRecordChangeContext context,
            CancellationToken cancellationToken)
        {
            await context.Client
                .CreateDnsRecordAsync(context.ZoneId, context.Desired, cancellationToken)
                .ConfigureAwait(false);
            context.Route.DnsRecordCreated = true;
        }
    }

    public sealed record Update(string RecordId) : DnsRecordChange
    {
        public override async Task ApplyAsync(
            DnsRecordChangeContext context,
            CancellationToken cancellationToken)
        {
            await context.Client
                .UpdateDnsRecordAsync(context.ZoneId, RecordId, context.Desired, cancellationToken)
                .ConfigureAwait(false);
            context.Route.DnsRecordCreated = true;
        }
    }

    public sealed record Unchanged : DnsRecordChange
    {
        public override Task ApplyAsync(
            DnsRecordChangeContext context,
            CancellationToken cancellationToken)
        {
            context.Route.DnsRecordCreated = true;
            return Task.CompletedTask;
        }
    }

    public sealed record Conflict(CloudflareDnsRecord Record) : DnsRecordChange
    {
        public override Task ApplyAsync(
            DnsRecordChangeContext context,
            CancellationToken cancellationToken) =>
            context.ReportWarning(
                context.Route,
                new RouteWarning(
                    $"Configure DNS for {context.Route.Hostname}",
                    $"The {Record.Type} record for {context.Route.Hostname} points to " +
                    $"{Record.Content} and does not carry the comment '{context.Desired.Comment}', " +
                    "so the route left it unchanged. Delete the record to let the tunnel manage it."),
                cancellationToken);
    }
}

internal sealed record DnsRecordChangeContext
{
    public required ICloudflareApiClient Client { get; init; }

    public required string ZoneId { get; init; }

    public required PublishedRouteResource Route { get; init; }

    public required CloudflareDnsRecordRequest Desired { get; init; }

    public required Func<PublishedRouteResource, RouteWarning, CancellationToken, Task> ReportWarning { get; init; }
}

// Activity names the reporting task, and Message explains the warning.
internal sealed record RouteWarning(string Activity, string Message);
