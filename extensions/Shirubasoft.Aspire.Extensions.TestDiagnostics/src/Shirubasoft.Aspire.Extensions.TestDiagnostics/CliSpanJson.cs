using System.Text.Json.Nodes;

namespace Aspire.Hosting.Testing;

internal static class CliSpanJson
{
    private static readonly IReadOnlyDictionary<int, string> Kinds = new Dictionary<int, string>
    {
        [1] = "Internal",
        [2] = "Server",
        [3] = "Client",
        [4] = "Producer",
        [5] = "Consumer",
    };
    private static readonly IReadOnlyDictionary<int, string> Statuses = new Dictionary<int, string>
    {
        [1] = "Ok",
        [2] = "Error",
    };

    internal static JsonObject Convert(TelemetryEntry entry, TelemetryResourceNames resources)
    {
        var span = entry.Item;
        return TelemetryJson.WithoutNulls(new JsonObject
        {
            ["traceId"] = TelemetryJson.TextOrDefault(span["traceId"]),
            ["spanId"] = TelemetryJson.TextOrDefault(span["spanId"]),
            ["parentSpanId"] = TelemetryJson.Text(span["parentSpanId"]),
            ["kind"] = Lookup(Kinds, span["kind"]),
            ["name"] = TelemetryJson.Text(span["name"]),
            ["status"] = Lookup(Statuses, TelemetryJson.Property(span["status"], "code")),
            ["statusMessage"] = TelemetryJson.Text(TelemetryJson.Property(span["status"], "message")),
            ["source"] = resources.Resolve(entry.Resource),
            ["destination"] = TelemetryJson.Attribute(span["attributes"], "aspire.destination"),
            ["durationMs"] = TelemetryJson.Duration(TelemetryJson.Nanoseconds(span["startTimeUnixNano"]), TelemetryJson.Nanoseconds(span["endTimeUnixNano"])),
            ["timestamp"] = TelemetryJson.Timestamp(TelemetryJson.Nanoseconds(span["startTimeUnixNano"])),
            ["attributes"] = Attributes(span["attributes"]),
            ["links"] = Links(span["links"]),
        });
    }

    internal static bool IsError(TelemetryEntry entry) =>
        TelemetryJson.Property(entry.Item["status"], "code")?.GetValue<int>() == 2;

    private static string? Lookup(IReadOnlyDictionary<int, string> values, JsonNode? key) =>
        values.GetValueOrDefault(key?.GetValue<int>() ?? 0);

    private static JsonObject Attributes(JsonNode? attributes)
    {
        var result = new JsonObject();
        foreach (var attribute in TelemetryJson.Objects(attributes).Where(item => TelemetryJson.Text(item["key"]) != "aspire.destination"))
        {
            result[TelemetryJson.Text(attribute["key"])!] = TelemetryStatusNames.Attribute(attribute);
        }
        return result;
    }

    private static JsonArray? Links(JsonNode? links)
    {
        var result = new JsonArray(TelemetryJson.Objects(links).Select(link => (JsonNode)new JsonObject
        {
            ["traceId"] = TelemetryJson.TextOrDefault(link["traceId"]),
            ["spanId"] = TelemetryJson.TextOrDefault(link["spanId"]),
        }).ToArray());
        return result.Count == 0 ? null : result;
    }
}
