using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareRouteReconciliationTests
{
    private const string OwnerComment = "managed-by=aspire:public";

    [Fact]
    public async Task RenamingARouteReplacesItsIngressRuleAndDnsRecord()
    {
        var cloudflare = new FakeCloudflare();

        await ConfigureAsync(cloudflare, "old.example.com");
        await ConfigureAsync(cloudflare, "new.example.com");

        var record = Assert.Single(cloudflare.DnsRecords);
        Assert.Equal("new.example.com", record.Name);
        Assert.Equal(FakeCloudflare.TunnelTarget, record.Content);
        Assert.Equal(OwnerComment, record.Comment);
        Assert.Equal(
            [("new.example.com", "http://web:8080"), (null, "http_status:404")],
            cloudflare.Ingress);
    }

    [Fact]
    public async Task RemovingTheFinalRouteClearsItsIngressRuleAndDnsRecord()
    {
        var cloudflare = new FakeCloudflare();
        await ConfigureAsync(cloudflare, "app.example.com");
        var version = cloudflare.ConfigurationVersion;

        await ConfigureAsync(cloudflare);

        Assert.Empty(cloudflare.DnsRecords);
        Assert.Equal([(null, "http_status:404")], cloudflare.Ingress);
        Assert.Equal(version + 1, cloudflare.ConfigurationVersion);
    }

    [Fact]
    public async Task ReconcilingTheDeclaredRoutesAgainChangesNothing()
    {
        var cloudflare = new FakeCloudflare();
        await ConfigureAsync(cloudflare, "app.example.com", "api.example.com");
        var records = cloudflare.DnsRecords.Select(record => record.ToJson().ToJsonString()).ToArray();
        var ingress = cloudflare.Ingress.ToArray();
        cloudflare.Writes.Clear();

        await ConfigureAsync(cloudflare, "app.example.com", "api.example.com");

        Assert.Empty(cloudflare.Writes);
        Assert.Equal(records, cloudflare.DnsRecords.Select(record => record.ToJson().ToJsonString()));
        Assert.Equal(ingress, cloudflare.Ingress);
    }

    [Theory]
    [InlineData("elsewhere.example.net", null)]
    [InlineData("other-tunnel.cfargotunnel.com", "managed-by=aspire:other")]
    public async Task AnUnownedRecordForADeclaredHostnameIsLeftAloneAndReported(
        string content,
        string? comment)
    {
        var cloudflare = new FakeCloudflare();
        var unowned = cloudflare.AddDnsRecord("app.example.com", content, comment);
        var logger = new RecordingLogger();

        await ConfigureAsync(cloudflare, logger, "app.example.com");

        var record = Assert.Single(cloudflare.DnsRecords);
        Assert.Same(unowned, record);
        Assert.Equal(content, record.Content);
        Assert.Equal(comment, record.Comment);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("app.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnownedRecordsAreNeverDeleted()
    {
        var cloudflare = new FakeCloudflare
        {
            Ingress = [("manual.example.com", "http://manual:80"), (null, "http_status:404")],
        };
        cloudflare.AddDnsRecord("manual.example.com", FakeCloudflare.TunnelTarget);
        cloudflare.AddDnsRecord("unrelated.example.com", "elsewhere.example.net", "kept by hand");

        await ConfigureAsync(cloudflare, "app.example.com");

        Assert.Equal(
            ["manual.example.com", "unrelated.example.com", "app.example.com"],
            cloudflare.DnsRecords.Select(record => record.Name));
        Assert.Equal(
            [("app.example.com", "http://web:8080"), (null, "http_status:404")],
            cloudflare.Ingress);
    }

    [Fact]
    public async Task ARecordThatAlreadyPointsAtTheTunnelIsAdopted()
    {
        var cloudflare = new FakeCloudflare();
        var legacy = cloudflare.AddDnsRecord("app.example.com", FakeCloudflare.TunnelTarget);

        await ConfigureAsync(cloudflare, "app.example.com");

        Assert.Same(legacy, Assert.Single(cloudflare.DnsRecords));
        Assert.Equal(OwnerComment, legacy.Comment);

        await ConfigureAsync(cloudflare);

        Assert.Empty(cloudflare.DnsRecords);
    }

    [Fact]
    public async Task StaleRecordsOnEveryPageOfTheDnsListAreDeleted()
    {
        var cloudflare = new FakeCloudflare
        {
            MaxPageSize = 2,
            Ingress = [("one.example.com", "http://web:8080"), (null, "http_status:404")],
        };
        cloudflare.AddDnsRecord("kept-1.example.com", "elsewhere.example.net");
        cloudflare.AddDnsRecord("one.example.com", FakeCloudflare.TunnelTarget, OwnerComment);
        cloudflare.AddDnsRecord("kept-2.example.com", "elsewhere.example.net");
        cloudflare.AddDnsRecord("two.example.com", FakeCloudflare.TunnelTarget, OwnerComment);
        cloudflare.AddDnsRecord("three.example.com", FakeCloudflare.TunnelTarget, OwnerComment);

        await ConfigureAsync(cloudflare);

        Assert.Equal(
            ["kept-1.example.com", "kept-2.example.com"],
            cloudflare.DnsRecords.Select(record => record.Name));
    }

    [Theory]
    [InlineData("DELETE dns_records")]
    [InlineData("PUT configurations")]
    public async Task AnInterruptedUpdateIsRecoveredOnTheNextRun(string failingRequest)
    {
        var cloudflare = new FakeCloudflare();
        await ConfigureAsync(cloudflare, "old.example.com");
        cloudflare.FailNext = failingRequest;

        await Assert.ThrowsAnyAsync<Exception>(() => ConfigureAsync(cloudflare, "new.example.com"));
        await ConfigureAsync(cloudflare, "new.example.com");

        Assert.Equal("new.example.com", Assert.Single(cloudflare.DnsRecords).Name);
        Assert.Equal(
            [("new.example.com", "http://web:8080"), (null, "http_status:404")],
            cloudflare.Ingress);
    }

    [Fact]
    public async Task DeploymentReportsAConflictAsAWarning()
    {
        var cloudflare = new FakeCloudflare();
        cloudflare.AddDnsRecord("app.example.com", "elsewhere.example.net");
        var tunnel = cloudflare.CreateTunnel();
        var warnings = new List<RouteWarning>();

        await new CloudflareRouteProvisioner(cloudflare.CreateClientFactory())
            .ConfigureRoutesForPipelineAsync(
                tunnel,
                CreateRoutes(tunnel, "app.example.com"),
                (_, _) => Task.FromResult("http://web:8080"),
                (_, warning, _) =>
                {
                    warnings.Add(warning);
                    return Task.CompletedTask;
                },
                new RecordingLogger(),
                TestContext.Current.CancellationToken);

        var warning = Assert.Single(warnings);
        Assert.Equal("Configure DNS for app.example.com", warning.Activity);
        Assert.Contains("elsewhere.example.net", warning.Message, StringComparison.Ordinal);
        Assert.Contains(OwnerComment, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostnameOutsideTheAccountsZonesFailsBeforeAnyChange()
    {
        var cloudflare = new FakeCloudflare();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConfigureAsync(cloudflare, "app.example.com", "app.example.net"));

        Assert.Contains("app.example.net", exception.Message, StringComparison.Ordinal);
        Assert.Empty(cloudflare.Writes);
    }

    private static Task ConfigureAsync(FakeCloudflare cloudflare, params string[] hostnames) =>
        ConfigureAsync(cloudflare, new RecordingLogger(), hostnames);

    private static async Task ConfigureAsync(
        FakeCloudflare cloudflare,
        ILogger logger,
        params string[] hostnames)
    {
        var tunnel = cloudflare.CreateTunnel();

        await new CloudflareRouteProvisioner(cloudflare.CreateClientFactory())
            .ConfigureRoutesAsync(
                tunnel,
                CreateRoutes(tunnel, hostnames),
                _ => Task.FromResult("http://web:8080"),
                _ => logger,
                TestContext.Current.CancellationToken);
    }

    private static PublishedRouteResource[] CreateRoutes(
        CloudflareTunnelResource tunnel,
        params string[] hostnames)
    {
        var target = DistributedApplication.CreateBuilder()
            .AddContainer("web", "nginx")
            .WithHttpEndpoint(targetPort: 8080, name: "http");

        return
        [
            .. hostnames.Select(hostname => new PublishedRouteResource(
                $"route-{hostname.Replace('.', '-')}",
                hostname,
                target.GetEndpoint("http"),
                target.Resource,
                tunnel)),
        ];
    }
}
