#pragma warning disable CS0618 // Aspire's public test inspection helpers are obsolete in favor of a lower-level configuration builder.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.TigerBeetle.Tests;

public sealed class TigerBeetleStartupTests
{
    [Fact]
    public async Task PrimaryEndpointCapturesTheAllocatedHostAndPort()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var primaryEndpoint = new TigerBeetlePrimaryEndpoint(tigerBeetle.Resource);

        Assert.Null(primaryEndpoint.Value);
        await primaryEndpoint.CaptureAsync();

        Assert.Equal(("127.0.0.1", 4567), primaryEndpoint.Value);
    }

    [Fact]
    public async Task StartupScriptsUseLfLineEndings()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");
        var cdc = tigerBeetle.AddChangeDataCapture("tigerbeetle-cdc", builder.AddRabbitMQ("rabbitmq"), "tigerbeetle");

        var replicaScript = (await tigerBeetle.Resource.GetArgumentValuesAsync())[1];
        var cdcScript = (await cdc.Resource.GetArgumentValuesAsync())[1];

        Assert.DoesNotContain('\r', replicaScript);
        Assert.DoesNotContain('\r', cdcScript);
        Assert.StartsWith("set -eu\n", cdcScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeDataCaptureWithoutAVirtualHostOverrideKeepsTheUriVirtualHost()
    {
        var builder = DistributedApplication.CreateBuilder();
        var cdc = builder.AddTigerBeetle("tigerbeetle")
            .AddChangeDataCapture("tigerbeetle-cdc", builder.AddRabbitMQ("rabbitmq"), "tigerbeetle");

        var script = (await cdc.Resource.GetArgumentValuesAsync())[1];

        Assert.DoesNotContain("amqp_vhost='", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--publish-routing-key", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("amqp://broker.example", false)]
    [InlineData("amqps://broker.example", true)]
    public async Task ChangeDataCaptureStartupLogWarnsOnlyForTls(string connectionString, bool warns)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:amqp"] = connectionString;
        var cdc = builder.AddTigerBeetle("tigerbeetle")
            .AddChangeDataCapture("tigerbeetle-cdc", builder.AddConnectionString("amqp"), "ledger-events");
        var logger = new RecordingLogger();

        await TigerBeetleChangeDataCaptureStartupLog.WriteAsync(
            cdc.Resource,
            logger,
            TestContext.Current.CancellationToken);

        var information = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains("ledger-events", information.Message, StringComparison.Ordinal);
        Assert.Equal(warns, logger.Entries.Any(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task WithReferenceSkipsPropertiesWhenTheConsumerExcludesConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReferenceEnvironment(ReferenceEnvironmentInjectionFlags.None)
            .WithReference(tigerBeetle);

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("TIGERBEETLE_", StringComparison.Ordinal));
        Assert.Contains(
            consumer.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, tigerBeetle.Resource));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("1ledger", "_1LEDGER_")]
    [InlineData("ledger.east-2", "LEDGER_EAST_2_")]
    public void ReferenceEnvironmentPrefixEncodesTheConnectionName(string connectionName, string expected)
    {
        Assert.Equal(expected, TigerBeetleReferenceEnvironment.Prefix(connectionName));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
