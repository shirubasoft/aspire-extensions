using System.Globalization;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Testing;

internal static class CliLogJson
{
    private static readonly HashSet<string> ExcludedAttributes =
        ["exception.stacktrace", "exception.message", "exception.type", "aspire.log_id"];

    internal static JsonArray Convert(JsonNode? response, TelemetryResourceNames resources, string dashboardUrl) =>
        new(TelemetryJson.Entries(TelemetryJson.Data(response, "resourceLogs"), "scopeLogs", "logRecords")
            .Select(entry => (JsonNode)ConvertEntry(entry, resources, dashboardUrl)).ToArray());

    private static JsonObject ConvertEntry(TelemetryEntry entry, TelemetryResourceNames resources, string dashboardUrl)
    {
        var log = entry.Item;
        var idText = TelemetryJson.Attribute(log["attributes"], "aspire.log_id");
        var logId = long.TryParse(idText, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;
        return TelemetryJson.WithoutNulls(new JsonObject
        {
            ["logId"] = logId,
            ["spanId"] = TelemetryJson.Text(log["spanId"]),
            ["traceId"] = TelemetryJson.Text(log["traceId"]),
            ["message"] = TelemetryJson.TextOrDefault(TelemetryJson.Property(log["body"], "stringValue")),
            ["severity"] = TelemetryJson.TextOrDefault(log["severityText"], "Unknown"),
            ["resourceName"] = resources.Resolve(entry.Resource),
            ["attributes"] = Attributes(log["attributes"]),
            ["exception"] = ExceptionText(log["attributes"]),
            ["source"] = entry.Scope,
            ["dashboardUrl"] = LogUrl(dashboardUrl, logId),
        });
    }

    private static string? LogUrl(string dashboardUrl, long? id) => id is null
        ? null : $"{dashboardUrl.TrimEnd('/')}/structuredlogs?logEntryId={id.Value.ToString(CultureInfo.InvariantCulture)}";

    private static JsonObject Attributes(JsonNode? attributes)
    {
        var result = new JsonObject();
        foreach (var attribute in TelemetryJson.Objects(attributes).Where(item => !ExcludedAttributes.Contains(TelemetryJson.Text(item["key"])!)))
        {
            result[TelemetryJson.Text(attribute["key"])!] = TelemetryJson.AttributeValue(attribute["value"]);
        }
        return result;
    }

    private static string? ExceptionText(JsonNode? attributes) =>
        TelemetryJson.Attribute(attributes, "exception.stacktrace") ?? ExceptionMessage(attributes);

    private static string? ExceptionMessage(JsonNode? attributes)
    {
        var message = TelemetryJson.Attribute(attributes, "exception.message");
        if (message is null)
        {
            return null;
        }
        var type = TelemetryJson.Attribute(attributes, "exception.type");
        return type is null ? message : $"{type}: {message}";
    }
}
