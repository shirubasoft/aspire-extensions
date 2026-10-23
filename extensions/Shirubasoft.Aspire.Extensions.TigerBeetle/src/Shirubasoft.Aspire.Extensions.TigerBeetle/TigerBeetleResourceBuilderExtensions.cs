using System.Globalization;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.TigerBeetle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Adds and configures TigerBeetle container resources.
/// </summary>
public static class TigerBeetleResourceBuilderExtensions
{
    /// <summary>Adds a single TigerBeetle replica in development mode.</summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="port">The optional host port. Aspire assigns one when this is <see langword="null" />.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>
    /// The resource formats its data file when it does not exist, then starts TigerBeetle. Development mode is on by default.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> AddTigerBeetle(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = new TigerBeetleResource(name);
        var primaryEndpoint = new TigerBeetlePrimaryEndpoint(resource);

        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            (_, _) => primaryEndpoint.CaptureAsync());

        var healthCheckKey = $"{name}_tcp_check";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new TigerBeetleTcpHealthCheck(() => primaryEndpoint.Value),
            failureStatus: default,
            tags: default,
            timeout: TimeSpan.FromSeconds(5)));

        return builder.AddResource(resource)
            .WithImage(TigerBeetleContainerImageTags.Image)
            .WithImageRegistry(TigerBeetleContainerImageTags.Registry)
            .WithImageTag(TigerBeetleContainerImageTags.Tag)
            .WithEndpoint(
                targetPort: TigerBeetleResource.DefaultPort,
                port: port,
                scheme: "tcp",
                name: TigerBeetleResource.PrimaryEndpointName)
            .WithIconName("Database")
            .WithEntrypoint("/bin/sh")
            .WithArgs(context =>
            {
                context.Args.Add("-ec");
                context.Args.Add(TigerBeetleStartupScript.Build(resource));
            })
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--security-opt");
                context.Args.Add("seccomp=unconfined");
            })
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>Adds a structured TigerBeetle reference to a consuming resource.</summary>
    /// <param name="builder">The consuming resource builder.</param>
    /// <param name="tigerBeetleResource">The TigerBeetle resource.</param>
    /// <returns>The consuming resource builder.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the custom withReference dispatcher.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TigerBeetleResource> tigerBeetleResource)
        where TDestination : IResourceWithEnvironment =>
        WithReference(builder, tigerBeetleResource, connectionName: null);

    /// <summary>Adds a structured TigerBeetle reference to a consuming resource.</summary>
    /// <param name="builder">The consuming resource builder.</param>
    /// <param name="tigerBeetleResource">The TigerBeetle resource.</param>
    /// <param name="connectionName">An optional name used as the injected property prefix.</param>
    /// <returns>The consuming resource builder.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the custom withReference dispatcher.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TigerBeetleResource> tigerBeetleResource,
        string? connectionName)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tigerBeetleResource);

        var resource = tigerBeetleResource.Resource;
        builder.WithReferenceRelationship(resource);

        return TigerBeetleReferenceEnvironment.IncludesConnectionProperties(builder.Resource)
            ? builder.WithEnvironment(context =>
                TigerBeetleReferenceEnvironment.Apply(context, resource, connectionName ?? resource.Name))
            : builder;
    }

    /// <summary>Sets the unsigned 128-bit TigerBeetle cluster ID.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="clusterId">The cluster ID in decimal form.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>Cluster ID 0 is reserved for tests. Changing this setting with an existing data volume creates a different data file.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="clusterId" /> is not an unsigned 128-bit decimal value.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithClusterId(
        this IResourceBuilder<TigerBeetleResource> builder,
        string clusterId)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!UInt128.TryParse(clusterId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("The cluster ID must be an unsigned 128-bit decimal value.", nameof(clusterId));
        }

        builder.Resource.ClusterId = clusterId;
        return builder;
    }

    /// <summary>Sets the unsigned 128-bit TigerBeetle cluster ID.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="clusterId">The cluster ID.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>Cluster ID 0 is reserved for tests. Changing this setting with an existing data volume creates a different data file.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    [AspireExportIgnore(Reason = "System.UInt128 has no portable TypeScript representation. Use the decimal string overload in polyglot AppHosts.")]
    public static IResourceBuilder<TigerBeetleResource> WithClusterId(
        this IResourceBuilder<TigerBeetleResource> builder,
        UInt128 clusterId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithClusterId(clusterId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sets this replica's index and the cluster replica count.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="replicaIndex">The zero-based replica index.</param>
    /// <param name="replicaCount">The cluster replica count, from 1 through 6.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="replicaCount" /> is outside the range from 1 through 6, or <paramref name="replicaIndex" /> is outside the configured replica range.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithReplica(
        this IResourceBuilder<TigerBeetleResource> builder,
        int replicaIndex,
        int replicaCount)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ArgumentValidation.ThrowIfOutside(replicaCount, 1, 6, "TigerBeetle supports from 1 through 6 replicas.");
        ArgumentValidation.ThrowIfOutside(
            replicaIndex,
            0,
            replicaCount - 1,
            "The replica index must be from zero through one less than the replica count.");

        builder.Resource.ReplicaIndex = replicaIndex;
        builder.Resource.ReplicaCount = replicaCount;
        return builder;
    }

    /// <summary>Sets the ordered numeric addresses passed to <c>tigerbeetle start</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="addresses">Comma-separated ports, numeric IP addresses, or numeric IP endpoints.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>TigerBeetle does not accept DNS names. Configure one ordered address per replica. This value is also exposed through the <c>Addresses</c> connection property.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="addresses" /> is empty or contains an invalid address.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithAddresses(
        this IResourceBuilder<TigerBeetleResource> builder,
        string addresses)
    {
        ArgumentNullException.ThrowIfNull(builder);
        TigerBeetleAddresses.Validate(addresses, nameof(addresses));

        builder.Resource.Addresses = addresses;
        builder.Resource.ClientAddresses = addresses;
        return builder;
    }

    /// <summary>Sets client addresses without changing the replica listen addresses.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="addresses">Comma-separated ports, numeric IP addresses, or numeric IP endpoints.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="addresses" /> is empty or contains an invalid address.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithClientAddresses(
        this IResourceBuilder<TigerBeetleResource> builder,
        string addresses)
    {
        ArgumentNullException.ThrowIfNull(builder);
        TigerBeetleAddresses.Validate(addresses, nameof(addresses));

        builder.Resource.ClientAddresses = addresses;
        return builder;
    }

    /// <summary>Sets the TigerBeetle grid-cache size, such as <c>256MiB</c> or <c>12GiB</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="size">A TigerBeetle byte-size value.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="size" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithCacheGrid(
        this IResourceBuilder<TigerBeetleResource> builder,
        string size)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(size);

        builder.Resource.CacheGrid = size;
        return builder;
    }

    /// <summary>Enables or disables TigerBeetle development mode for both format and start.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="enabled">Whether development mode is enabled.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <remarks>Production mode requires TigerBeetle's host, memory-locking, disk, and networking requirements.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDevelopmentMode(
        this IResourceBuilder<TigerBeetleResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.DevelopmentMode = enabled;
        return builder;
    }

    /// <summary>Sets the data-file path inside the container.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="path">An absolute container path.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty, contains only white-space characters, or is not an absolute container path.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDataFile(
        this IResourceBuilder<TigerBeetleResource> builder,
        string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The data-file path must be absolute inside the container.", nameof(path));
        }

        builder.Resource.ExplicitDataFile = path;
        return builder;
    }

    /// <summary>Adds a volume mounted at TigerBeetle's <c>/data</c> directory.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="name">The optional volume name. Aspire generates a stable name when omitted.</param>
    /// <param name="isReadOnly">Whether the volume is read-only.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is explicitly empty or contains only white-space characters, or <paramref name="isReadOnly" /> is <see langword="true" />.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDataVolume(
        this IResourceBuilder<TigerBeetleResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var volumeName = DataVolumeName(builder, name);
        ArgumentValidation.ThrowIfReadOnly(isReadOnly, "TigerBeetle data volumes must be writable.");

        return builder.WithVolume(
            volumeName,
            TigerBeetleResource.DataDirectory,
            isReadOnly: false);
    }

    /// <summary>Adds a bind mount at TigerBeetle's <c>/data</c> directory.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="source">The host directory.</param>
    /// <param name="isReadOnly">Whether the bind mount is read-only.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="source" /> is empty or contains only white-space characters, or <paramref name="isReadOnly" /> is <see langword="true" />.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithDataBindMount(
        this IResourceBuilder<TigerBeetleResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        ArgumentValidation.ThrowIfReadOnly(isReadOnly, "TigerBeetle data bind mounts must be writable.");

        return builder.WithBindMount(source, TigerBeetleResource.DataDirectory, isReadOnly: false);
    }

    /// <summary>Enables TigerBeetle's experimental StatsD exporter.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="ipAddress">A numeric StatsD IPv4 or IPv6 address.</param>
    /// <param name="port">The StatsD UDP port.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="ipAddress" /> is not a numeric IPv4 or IPv6 address.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port" /> is outside the range from 1 through 65535.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithStatsD(
        this IResourceBuilder<TigerBeetleResource> builder,
        string ipAddress,
        int port = 8125)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var address = IPAddress.TryParse(ipAddress, out var parsedAddress)
            ? parsedAddress
            : throw new ArgumentException("TigerBeetle StatsD requires a numeric IP address.", nameof(ipAddress));
        ArgumentValidation.ThrowIfOutside(port, 1, 65535);

        builder.Resource.StatsDEndpoint = new IPEndPoint(address, port).ToString();
        return builder;
    }

    /// <summary>Adds one raw argument to <c>tigerbeetle format</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="argument" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithFormatArgument(
        this IResourceBuilder<TigerBeetleResource> builder,
        string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        builder.Resource.AdditionalFormatArguments.Add(argument);
        return builder;
    }

    /// <summary>Adds one raw argument to <c>tigerbeetle start</c>.</summary>
    /// <param name="builder">The TigerBeetle resource builder.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The TigerBeetle resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="argument" /> is empty or contains only white-space characters.</exception>
    [AspireExport]
    public static IResourceBuilder<TigerBeetleResource> WithStartArgument(
        this IResourceBuilder<TigerBeetleResource> builder,
        string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        builder.Resource.AdditionalStartArguments.Add(argument);
        return builder;
    }

    private static string DataVolumeName(IResourceBuilder<TigerBeetleResource> builder, string? name)
    {
        if (name is null)
        {
            return VolumeNameGenerator.Generate(builder, "data");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
