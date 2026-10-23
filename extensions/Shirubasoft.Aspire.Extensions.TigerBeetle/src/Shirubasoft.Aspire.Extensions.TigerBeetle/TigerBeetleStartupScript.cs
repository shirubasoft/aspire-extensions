using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.TigerBeetle;

// Formats the replica's data file on first start, then starts the replica with the same development mode.
internal static class TigerBeetleStartupScript
{
    public static string Build(TigerBeetleResource resource)
    {
        ValidateReplicaAddressCount(resource);

        var dataFile = ShellQuoting.Quote(resource.DataFile);
        var replica = resource.ReplicaIndex.ToString(CultureInfo.InvariantCulture);
        var replicaCount = resource.ReplicaCount.ToString(CultureInfo.InvariantCulture);
        var development = DevelopmentFlag(resource);

        // Join with LF so a Windows AppHost still produces a script that a Linux shell can run.
        return string.Join(
            '\n',
            "set -eu",
            $"if [ ! -e {dataFile} ]; then",
            $"  /tigerbeetle format --cluster={ShellQuoting.Quote(resource.ClusterId)} --replica={replica} " +
                $"--replica-count={replicaCount}{development}" +
                $"{ShellQuoting.QuoteEach(resource.AdditionalFormatArguments)} {dataFile}",
            "fi",
            $"exec /sbin/tini -- /tigerbeetle start --addresses={ShellQuoting.Quote(resource.Addresses)}{development}" +
                $"{ShellQuoting.Option("--cache-grid", resource.CacheGrid)}{StatsDOptions(resource)}" +
                $"{ShellQuoting.QuoteEach(resource.AdditionalStartArguments)} {dataFile}");
    }

    private static string DevelopmentFlag(TigerBeetleResource resource) =>
        resource.DevelopmentMode ? " --development" : string.Empty;

    private static string StatsDOptions(TigerBeetleResource resource) =>
        resource.StatsDEndpoint is null
            ? string.Empty
            : $" --experimental{ShellQuoting.Option("--statsd", resource.StatsDEndpoint)}";

    private static void ValidateReplicaAddressCount(TigerBeetleResource resource)
    {
        var addressCount = TigerBeetleAddresses.Count(resource.Addresses);
        if (addressCount != resource.ReplicaCount)
        {
            throw new DistributedApplicationException(
                $"The TigerBeetle resource '{resource.Name}' has {addressCount.ToString(CultureInfo.InvariantCulture)} server " +
                $"address(es), but its replica count is {resource.ReplicaCount.ToString(CultureInfo.InvariantCulture)}. " +
                $"Configure exactly one ordered address per replica with {nameof(TigerBeetleResourceBuilderExtensions.WithAddresses)}.");
        }
    }
}
