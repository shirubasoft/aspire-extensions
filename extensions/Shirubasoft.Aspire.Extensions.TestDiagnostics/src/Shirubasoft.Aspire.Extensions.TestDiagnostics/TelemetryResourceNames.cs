using System.Text.Json.Nodes;

namespace Aspire.Hosting.Testing;

internal sealed class TelemetryResourceNames(JsonNode? resources)
{
    internal string Resolve(JsonNode? resource)
    {
        var name = Name(resource);
        var matches = TelemetryJson.Objects(resources)
            .Where(item => string.Equals(TelemetryJson.Text(item["name"]), name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matches.Length < 2)
        {
            return name;
        }
        var instance = TelemetryJson.Attribute(TelemetryJson.Property(resource, "attributes"), "service.instance.id");
        return ReplicaName(TelemetryJson.Text(matches[1]["name"])!, instance);
    }

    private static string Name(JsonNode? resource) =>
        TelemetryJson.Attribute(TelemetryJson.Property(resource, "attributes"), "service.name") ?? "Unknown";

    private static string ReplicaName(string name, string? instance)
    {
        if (instance is null)
        {
            return name;
        }
        var suffix = Guid.TryParse(instance, out var guid) ? guid.ToString("N")[^8..] : instance;
        return $"{name}-{suffix}";
    }
}
