#pragma warning disable CS0618 // Aspire's public test inspection helpers are obsolete in favor of a lower-level configuration builder.

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Aspire.Hosting.TigerBeetle.Tests;

public sealed class TigerBeetleResourceTests
{
    [Fact]
    public async Task AddChangeDataCaptureConfiguresAChildRabbitMqPublisher()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var rabbitMq = builder.AddRabbitMQ("rabbitmq")
            .WithEndpoint("tcp", endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 5672));

        var cdc = tigerBeetle.AddChangeDataCapture(
            "tigerbeetle-cdc",
            rabbitMq,
            "tigerbeetle");

        Assert.Same(tigerBeetle.Resource, cdc.Resource.TigerBeetle);
        Assert.Same(rabbitMq.Resource, cdc.Resource.AmqpConnection);
        Assert.Equal("tigerbeetle", cdc.Resource.PublishExchange);
        Assert.Null(cdc.Resource.VirtualHost);

        var image = Assert.Single(cdc.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("tigerbeetle/tigerbeetle", image.Image);
        Assert.Equal("0.17.9", image.Tag);
        Assert.Equal("/bin/sh", cdc.Resource.Entrypoint);

        var arguments = await cdc.Resource.GetArgumentValuesAsync();
        Assert.Equal("-ec", arguments[0]);
        Assert.Contains("exec /sbin/tini -- /tigerbeetle amqp", arguments[1]);
        Assert.Contains("--addresses=\"$tigerbeetle_addresses\"", arguments[1]);
        Assert.Contains("--cluster='0'", arguments[1]);
        Assert.Contains("--host=\"$amqp_host\"", arguments[1]);
        Assert.Contains("--vhost=\"$amqp_vhost\"", arguments[1]);
        Assert.Contains("--publish-exchange='tigerbeetle'", arguments[1]);
        Assert.Contains("getent ahostsv4", arguments[1]);
        Assert.Contains(
            "TigerBeetle CDC starting: cluster=%s addresses=%s amqp_host=%s vhost=%s publish_exchange=%s",
            arguments[1]);
        Assert.True(
            arguments[1].IndexOf("TigerBeetle CDC starting", StringComparison.Ordinal) <
            arguments[1].IndexOf("exec /sbin/tini -- /tigerbeetle amqp", StringComparison.Ordinal));

        var runtimeArgs = Assert.Single(cdc.Resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>());
        var values = new List<object>();
        await runtimeArgs.Callback(new ContainerRuntimeArgsCallbackContext(values));
        Assert.Equal(["--security-opt", "seccomp=unconfined"], values);

        var relationships = cdc.Resource.Annotations.OfType<ResourceRelationshipAnnotation>();
        Assert.Contains(relationships, annotation =>
            ReferenceEquals(annotation.Resource, tigerBeetle.Resource) &&
            annotation.Type == "Parent");
        Assert.Contains(relationships, annotation =>
            ReferenceEquals(annotation.Resource, tigerBeetle.Resource) &&
            annotation.Type == "WaitFor");
        Assert.Contains(relationships, annotation =>
            ReferenceEquals(annotation.Resource, rabbitMq.Resource) &&
            annotation.Type == "Reference");
        Assert.Contains(relationships, annotation =>
            ReferenceEquals(annotation.Resource, rabbitMq.Resource) &&
            annotation.Type == "WaitFor");
    }

    [Fact]
    public async Task ChangeDataCaptureConfigurationMethodsAffectTheAmqpCommandInOrder()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithClusterId("340282366920938463463374607431768211455");
        var rabbitMq = builder.AddRabbitMQ("rabbitmq");
        var cdc = tigerBeetle.AddChangeDataCapture(
                "tigerbeetle-cdc",
                rabbitMq,
                "ledger-events")
            .WithVirtualHost("ledger")
            .WithPublishRoutingKey("transfers")
            .WithTimestampLast("1")
            .WithTimestampLast(ulong.MaxValue)
            .WithCdcArgs("--event-count-max=100", "--idle-interval-ms=250");

        var arguments = await cdc.Resource.GetArgumentValuesAsync();
        var script = arguments[1];

        Assert.Contains("--cluster='340282366920938463463374607431768211455'", script);
        Assert.Contains("amqp_vhost='ledger'", script);
        Assert.Contains("--publish-exchange='ledger-events'", script);
        Assert.Contains("--publish-routing-key='transfers'", script);
        Assert.Contains("--timestamp-last='18446744073709551615'", script);
        Assert.EndsWith("'--event-count-max=100' '--idle-interval-ms=250'", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("--timestamp-last", StringComparison.Ordinal) <
            script.IndexOf("--event-count-max", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddChangeDataCaptureAcceptsAGenericConnectionStringWithoutAddingAWait()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var rabbitMq = builder.AddConnectionString(
            "rabbitmq",
            ReferenceExpression.Create($"amqp://alice:secret@rabbit.example:5678/%2F?heartbeat=30#ignored"));

        var cdc = tigerBeetle.AddChangeDataCapture(
            "tigerbeetle-cdc",
            rabbitMq,
            "tigerbeetle-events");

        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, cdc.Resource, environment);
        foreach (var annotation in cdc.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        Assert.Same(
            rabbitMq.Resource.ConnectionStringExpression,
            environment[TigerBeetleChangeDataCaptureStartupScript.AmqpUriEnvironmentVariable]);

        var relationships = cdc.Resource.Annotations.OfType<ResourceRelationshipAnnotation>();
        Assert.Contains(relationships, annotation =>
            ReferenceEquals(annotation.Resource, rabbitMq.Resource) &&
            annotation.Type == "Reference");
        Assert.DoesNotContain(relationships, annotation =>
            ReferenceEquals(annotation.Resource, rabbitMq.Resource) &&
            annotation.Type == "WaitFor");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AddChangeDataCaptureRejectsAMissingPublishExchange(string? exchange)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");
        var rabbitMq = builder.AddRabbitMQ("rabbitmq");

        Assert.ThrowsAny<ArgumentException>(() => tigerBeetle.AddChangeDataCapture(
            "tigerbeetle-cdc",
            rabbitMq,
            exchange!));
    }

    [Theory]
    [InlineData("amqp://user%40name:p%3Ass@broker.example/%2F?heartbeat=30#ignored", "amqp", "broker.example", 5672, "user@name", "p:ss", "/", false)]
    [InlineData("amqps://broker.example/ledger%2Fprimary?ignored=true", "amqps", "broker.example", 5671, "guest", "guest", "ledger/primary", true)]
    [InlineData("amqp://broker.example:6000", "amqp", "broker.example", 6000, "guest", "guest", "/", false)]
    public void ParseAmqpConnectionStringAppliesUriDefaultsAndDecoding(
        string value,
        string scheme,
        string host,
        int port,
        string userName,
        string password,
        string virtualHost,
        bool usesTls)
    {
        var connection = AmqpConnectionInfo.Parse(value);

        Assert.Equal(scheme, connection.Scheme);
        Assert.Equal(host, connection.Host);
        Assert.Equal(port, connection.Port);
        Assert.Equal(userName, connection.UserName);
        Assert.Equal(password, connection.Password);
        Assert.Equal(virtualHost, connection.VirtualHost);
        Assert.Equal(usesTls, connection.UsesTls);
    }

    [Theory]
    [InlineData("http://rabbit.example")]
    [InlineData("amqp:///ledger")]
    [InlineData("not-a-uri")]
    public void ParseAmqpConnectionStringRejectsUnsupportedValues(string value)
    {
        Assert.Throws<DistributedApplicationException>(() =>
            AmqpConnectionInfo.Parse(value));
    }

    [Fact]
    public async Task ChangeDataCaptureCommandShellQuotesConfiguredValues()
    {
        var builder = DistributedApplication.CreateBuilder();
        var cdc = builder.AddTigerBeetle("tigerbeetle")
            .AddChangeDataCapture(
                "tigerbeetle-cdc",
                builder.AddRabbitMQ("rabbitmq"),
                "ledger's-events")
            .WithVirtualHost("ledger's-vhost")
            .WithPublishRoutingKey("ledger's-routing-key")
            .WithCdcArgs("--custom=ledger's-value");

        var arguments = await cdc.Resource.GetArgumentValuesAsync();
        var script = arguments[1];

        Assert.Contains("amqp_vhost='ledger'\"'\"'s-vhost'", script);
        Assert.Contains("--publish-exchange='ledger'\"'\"'s-events'", script);
        Assert.Contains("--publish-routing-key='ledger'\"'\"'s-routing-key'", script);
        Assert.EndsWith("'--custom=ledger'\"'\"'s-value'", script, StringComparison.Ordinal);
        Assert.Contains("WARNING: TigerBeetle CDC does not support native AMQP TLS", script);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("not-a-timestamp")]
    public void WithTimestampLastRejectsInvalidValues(string timestamp)
    {
        var builder = DistributedApplication.CreateBuilder();
        var cdc = builder.AddTigerBeetle("tigerbeetle").AddChangeDataCapture(
            "tigerbeetle-cdc",
            builder.AddRabbitMQ("rabbitmq"),
            "tigerbeetle");

        Assert.Throws<ArgumentException>(() => cdc.WithTimestampLast(timestamp));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CdcStringConfigurationRejectsBlankValues(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var cdc = builder.AddTigerBeetle("tigerbeetle").AddChangeDataCapture(
            "tigerbeetle-cdc",
            builder.AddRabbitMQ("rabbitmq"),
            "tigerbeetle");

        Assert.Throws<ArgumentException>(() => cdc.WithVirtualHost(value));
        Assert.Throws<ArgumentException>(() => cdc.WithPublishRoutingKey(value));
        Assert.Throws<ArgumentException>(() => cdc.WithCdcArgs(value));
    }

    [Fact]
    public void WithCdcArgsRejectsANullArray()
    {
        var builder = DistributedApplication.CreateBuilder();
        var cdc = builder.AddTigerBeetle("tigerbeetle").AddChangeDataCapture(
            "tigerbeetle-cdc",
            builder.AddRabbitMQ("rabbitmq"),
            "tigerbeetle");

        Assert.Throws<ArgumentNullException>(() => cdc.WithCdcArgs(null!));
    }

    [Fact]
    public async Task AddTigerBeetleConfiguresAWorkingDevelopmentContainer()
    {
        var builder = DistributedApplication.CreateBuilder();

        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        var image = Assert.Single(tigerBeetle.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("ghcr.io", image.Registry);
        Assert.Equal("tigerbeetle/tigerbeetle", image.Image);
        Assert.Equal("0.17.9", image.Tag);

        var endpoint = Assert.Single(tigerBeetle.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(TigerBeetleResource.PrimaryEndpointName, endpoint.Name);
        Assert.Equal(TigerBeetleResource.DefaultPort, endpoint.TargetPort);
        Assert.Equal("tcp", endpoint.UriScheme);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Null(endpoint.Port);

        Assert.Equal("/bin/sh", tigerBeetle.Resource.Entrypoint);
        var arguments = await tigerBeetle.Resource.GetArgumentValuesAsync();
        Assert.Equal("-ec", arguments[0]);
        Assert.Contains("/tigerbeetle format --cluster='0' --replica=0 --replica-count=1 --development", arguments[1]);
        Assert.Contains("exec /sbin/tini -- /tigerbeetle start --addresses='0.0.0.0:3000' --development", arguments[1]);
        Assert.Contains("/data/0_0.tigerbeetle", arguments[1]);

        var runtimeArgs = Assert.Single(
            tigerBeetle.Resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>());
        var values = new List<object>();
        await runtimeArgs.Callback(new ContainerRuntimeArgsCallbackContext(values));
        Assert.Equal(["--security-opt", "seccomp=unconfined"], values);

        Assert.Single(tigerBeetle.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public async Task StructuredPropertiesUseTheAllocatedEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));

        var properties = tigerBeetle.Resource.GetConnectionProperties()
            .ToDictionary(property => property.Key, property => property.Value);

        Assert.DoesNotContain(typeof(IResourceWithConnectionString), typeof(TigerBeetleResource).GetInterfaces());
        Assert.Equal("127.0.0.1", await properties["Host"].GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("4567", await properties["Port"].GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("0", await properties["ClusterId"].GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("127.0.0.1:4567", await properties["Addresses"].GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithReferenceInjectsStandardAndTigerBeetleConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle);

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.DoesNotContain("ConnectionStrings__tigerbeetle", environment.Keys);
        Assert.Equal("127.0.0.1", environment["TIGERBEETLE_HOST"]);
        Assert.Equal("4567", environment["TIGERBEETLE_PORT"]);
        Assert.Equal("0", environment["TIGERBEETLE_CLUSTERID"]);
        Assert.Equal("127.0.0.1:4567", environment["TIGERBEETLE_ADDRESSES"]);
        Assert.Equal("127.0.0.1:4567", environment["TIGERBEETLE_ADDRESSES__0"]);

        Assert.Equal(
            ["Host", "Port", "ClusterId", "Addresses", "Addresses__0"],
            tigerBeetle.Resource.GetConnectionProperties().Select(property => property.Key));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TIGERBEETLE_ADDRESSES"] = environment["TIGERBEETLE_ADDRESSES"],
                ["TIGERBEETLE_ADDRESSES:0"] = environment["TIGERBEETLE_ADDRESSES__0"],
            })
            .Build();
        var boundAddresses = configuration.GetSection("TIGERBEETLE_ADDRESSES").Get<string[]>();
        Assert.NotNull(boundAddresses);
        Assert.Equal(["127.0.0.1:4567"], boundAddresses);
    }

    [Fact]
    public async Task WithReferenceSupportsAnEncodedPropertyPrefix()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle, "ledger-east");

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.Equal("0", environment["LEDGER_EAST_CLUSTERID"]);
        Assert.Equal("127.0.0.1:4567", environment["LEDGER_EAST_ADDRESSES"]);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReferenceInjectsOrderedIndexedClientAddresses()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithClientAddresses("10.0.0.10:3000, [2001:db8::1]:3000, 3002")
            .WithEndpoint(TigerBeetleResource.PrimaryEndpointName, endpoint =>
                endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 4567));
        var consumer = builder.AddExecutable("consumer", "echo", ".")
            .WithReference(tigerBeetle);

        var environment = await consumer.Resource.GetEnvironmentVariableValuesAsync();

        Assert.Equal(
            "10.0.0.10:3000, [2001:db8::1]:3000, 3002",
            environment["TIGERBEETLE_ADDRESSES"]);
        Assert.Equal("10.0.0.10:3000", environment["TIGERBEETLE_ADDRESSES__0"]);
        Assert.Equal("[2001:db8::1]:3000", environment["TIGERBEETLE_ADDRESSES__1"]);
        Assert.Equal("3002", environment["TIGERBEETLE_ADDRESSES__2"]);
        Assert.DoesNotContain("TIGERBEETLE_ADDRESSES__3", environment.Keys);
    }

    [Fact]
    public async Task ConfigurationMethodsAffectFormatAndStart()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithClusterId("340282366920938463463374607431768211455")
            .WithReplica(replicaIndex: 2, replicaCount: 3)
            .WithAddresses("10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000")
            .WithCacheGrid("12GiB")
            .WithDevelopmentMode(false)
            .WithDataFile("/var/lib/tigerbeetle/data.tigerbeetle")
            .WithStatsD("10.0.0.20", 9125)
            .WithFormatArgument("--verbose")
            .WithStartArgument("--trace");

        var arguments = await tigerBeetle.Resource.GetArgumentValuesAsync();
        var script = arguments[1];

        Assert.Contains("--cluster='340282366920938463463374607431768211455'", script);
        Assert.Contains("--replica=2 --replica-count=3", script);
        Assert.Contains("--addresses='10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000'", script);
        Assert.Contains("--cache-grid='12GiB'", script);
        Assert.Contains("--experimental --statsd='10.0.0.20:9125'", script);
        Assert.Contains("'--verbose'", script);
        Assert.Contains("'--trace'", script);
        Assert.DoesNotContain("--development", script);
        Assert.EndsWith("'/var/lib/tigerbeetle/data.tigerbeetle'", script, StringComparison.Ordinal);
        Assert.Equal(
            "340282366920938463463374607431768211455",
            await tigerBeetle.Resource.ClusterIdExpression.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "10.0.0.10:3000,10.0.0.11:3000,10.0.0.12:3000",
            await tigerBeetle.Resource.ClientAddressesExpression.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithClusterIdAcceptsUInt128Values()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithClusterId(UInt128.MaxValue);

        var arguments = await tigerBeetle.Resource.GetArgumentValuesAsync();

        Assert.Equal(UInt128.MaxValue.ToString(), tigerBeetle.Resource.ClusterId);
        Assert.Contains($"--cluster='{UInt128.MaxValue}'", arguments[1]);
    }

    [Fact]
    public void PersistenceMethodsUseTheTigerBeetleDataDirectory()
    {
        var builder = DistributedApplication.CreateBuilder();

        var volume = builder.AddTigerBeetle("volume").WithDataVolume("tigerbeetle-data");
        var volumeMount = Assert.Single(volume.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("tigerbeetle-data", volumeMount.Source);
        Assert.Equal(TigerBeetleResource.DataDirectory, volumeMount.Target);
        Assert.False(volumeMount.IsReadOnly);

        var bind = builder.AddTigerBeetle("bind").WithDataBindMount("./data");
        var bindMount = Assert.Single(bind.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.EndsWith(Path.Combine("data"), bindMount.Source, StringComparison.Ordinal);
        Assert.Equal(TigerBeetleResource.DataDirectory, bindMount.Target);
        Assert.False(bindMount.IsReadOnly);
    }

    [Fact]
    public void PersistenceMethodsRejectReadOnlyMounts()
    {
        var builder = DistributedApplication.CreateBuilder();

        var volume = builder.AddTigerBeetle("volume");
        var volumeException = Assert.Throws<ArgumentException>(() => volume.WithDataVolume(isReadOnly: true));
        Assert.Equal("isReadOnly", volumeException.ParamName);
        Assert.Contains("must be writable", volumeException.Message, StringComparison.Ordinal);

        var bind = builder.AddTigerBeetle("bind");
        var bindException = Assert.Throws<ArgumentException>(() => bind.WithDataBindMount("./data", isReadOnly: true));
        Assert.Equal("isReadOnly", bindException.ParamName);
        Assert.Contains("must be writable", bindException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void WithDataVolumeRejectsBlankExplicitNames(string name)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        var exception = Assert.Throws<ArgumentException>(() => tigerBeetle.WithDataVolume(name));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("340282366920938463463374607431768211456")]
    public void WithClusterIdRejectsInvalidValues(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        Assert.Throws<ArgumentException>(() => tigerBeetle.WithClusterId(value));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 7)]
    public void WithReplicaRejectsInvalidValues(int replicaIndex, int replicaCount)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        Assert.Throws<ArgumentOutOfRangeException>(() => tigerBeetle.WithReplica(replicaIndex, replicaCount));
    }

    [Theory]
    [InlineData("tigerbeetle:3000")]
    [InlineData("10.0.0.1:0")]
    [InlineData("10.0.0.1:65536")]
    [InlineData("3000,")]
    [InlineData(",3000")]
    [InlineData("3000,,3001")]
    public void WithAddressesRejectsDnsNamesAndInvalidPorts(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle");

        Assert.Throws<ArgumentException>(() => tigerBeetle.WithAddresses(value));
    }

    [Theory]
    [InlineData("3000")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:3000")]
    [InlineData("2001:db8::1")]
    [InlineData("[2001:db8::1]:3000")]
    public async Task WithAddressesAcceptsTigerBeetleNumericAddressForms(string value)
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithAddresses(value);

        var arguments = await tigerBeetle.Resource.GetArgumentValuesAsync();

        Assert.Contains($"--addresses='{value}'", arguments[1]);
    }

    [Fact]
    public async Task StartupRejectsAnAddressCountThatDoesNotMatchReplicaCount()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithReplica(replicaIndex: 0, replicaCount: 3)
            .WithAddresses("3000,3001");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
        {
            _ = await tigerBeetle.Resource.GetArgumentValuesAsync();
        });

        Assert.Contains("has 2 server address(es), but its replica count is 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TigerBeetleResourceBuilderExtensions.WithAddresses), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAddressesAcceptsBracketedIpv6Endpoints()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
            .WithReplica(replicaIndex: 0, replicaCount: 2)
            .WithAddresses("[2001:db8::1]:3000,[2001:db8::2]:3000");

        Assert.Equal(
            "[2001:db8::1]:3000,[2001:db8::2]:3000",
            await tigerBeetle.Resource.ClientAddressesExpression.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ["[2001:db8::1]:3000", "[2001:db8::2]:3000"],
            await Task.WhenAll(tigerBeetle.Resource.ClientAddressExpressions.Select(
                expression => expression.GetValueAsync(TestContext.Current.CancellationToken).AsTask())));
    }

    [Fact]
    public async Task TcpHealthCheckReportsThePrimaryEndpointItReached()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var healthCheck = new TigerBeetleTcpHealthCheck(() => ("127.0.0.1", port));

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains($"'127.0.0.1:{port}'", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TcpHealthCheckReportsTheUnavailablePrimaryEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var healthCheck = new TigerBeetleTcpHealthCheck(() => ("127.0.0.1", port));

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains($"'127.0.0.1:{port}'", result.Description, StringComparison.Ordinal);
        Assert.NotNull(result.Exception);
    }
}
