using System.Text.Json.Nodes;

namespace Aspire.Hosting.Testing;

internal static class CliTraceJson
{
    internal static JsonArray Convert(JsonNode? response, TelemetryResourceNames resources, string dashboardUrl) =>
        new(TelemetryJson.Entries(TelemetryJson.Data(response, "resourceSpans"), "scopeSpans", "spans")
            .GroupBy(entry => TelemetryJson.TextOrDefault(entry.Item["traceId"]), StringComparer.Ordinal)
            .Select(trace => (JsonNode)ConvertTrace(trace.Key, trace.ToArray(), resources, dashboardUrl)).ToArray());

    private static JsonObject ConvertTrace(string traceId, TelemetryEntry[] spans, TelemetryResourceNames resources, string dashboardUrl)
    {
        var root = spans.FirstOrDefault(entry => string.IsNullOrEmpty(TelemetryJson.Text(entry.Item["parentSpanId"]))) ?? spans[0];
        return TelemetryJson.WithoutNulls(new JsonObject
        {
            ["traceId"] = traceId,
            ["durationMs"] = TelemetryJson.Duration(
                spans.Min(entry => TelemetryJson.Nanoseconds(entry.Item["startTimeUnixNano"])),
                spans.Max(entry => TelemetryJson.Nanoseconds(entry.Item["endTimeUnixNano"]))),
            ["title"] = TelemetryJson.Text(root.Item["name"]),
            ["spans"] = new JsonArray(spans.Select(entry => (JsonNode)CliSpanJson.Convert(entry, resources)).ToArray()),
            ["hasError"] = spans.Any(CliSpanJson.IsError),
            ["timestamp"] = TelemetryJson.Timestamp(TelemetryJson.Nanoseconds(root.Item["startTimeUnixNano"])),
            ["dashboardUrl"] = $"{dashboardUrl.TrimEnd('/')}/traces/detail/{Uri.EscapeDataString(traceId)}",
        });
    }
}
