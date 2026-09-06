using System.Globalization;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Testing;

internal static class TelemetryJson
{
    private static readonly IReadOnlyDictionary<string, Func<JsonNode, string>> Scalars =
        new Dictionary<string, Func<JsonNode, string>>
        {
            ["intValue"] = value => long.Parse(value.ToString(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ["doubleValue"] = value => value.GetValue<double>().ToString(CultureInfo.InvariantCulture),
            ["boolValue"] = value => value.GetValue<bool>().ToString(CultureInfo.InvariantCulture),
        };

    internal static JsonNode? Property(JsonNode? node, string name) => node?[name];

    internal static JsonNode? Data(JsonNode? response, string name) => response?["data"]?[name];

    internal static string TextOrDefault(JsonNode? node, string fallback = "") => Text(node) ?? fallback;

    internal static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        (node as JsonArray ?? []).OfType<JsonObject>();

    internal static string? Text(JsonNode? node) => node?.GetValue<string>();

    internal static string AttributeValue(JsonNode? value)
    {
        foreach (var key in new[] { "stringValue", "intValue", "doubleValue", "boolValue" })
        {
            if (Property(value, key) is { } item)
            {
                return Scalar(key, item);
            }
        }
        return string.Empty;
    }

    private static string Scalar(string key, JsonNode value) =>
        Scalars.TryGetValue(key, out var convert) ? convert(value) : value.ToString();

    internal static string? Attribute(JsonNode? attributes, string key)
    {
        var value = AttributeValue(Objects(attributes).FirstOrDefault(item => Text(item["key"]) == key)?["value"]);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static JsonObject WithoutNulls(JsonObject value)
    {
        foreach (var key in value.Where(item => item.Value is null).Select(item => item.Key).ToArray())
        {
            value.Remove(key);
        }
        return value;
    }

    internal static ulong? Nanoseconds(JsonNode? value) =>
        value is null ? null : ulong.Parse(value.ToString(), CultureInfo.InvariantCulture);

    internal static DateTime? Timestamp(ulong? nanoseconds) =>
        nanoseconds is null ? null : DateTime.UnixEpoch.AddTicks((long)(nanoseconds.Value / 100));

    internal static int? Duration(ulong? start, ulong? end) =>
        start is null || end is null ? null : (int)Math.Round((end.Value - start.Value) / 1_000_000.0, MidpointRounding.AwayFromZero);

    internal static IEnumerable<TelemetryEntry> Entries(JsonNode? groups, string scopesKey, string itemsKey) =>
        Objects(groups).SelectMany(group => Objects(group[scopesKey])
            .SelectMany(scope => Objects(scope[itemsKey])
                .Select(item => new TelemetryEntry(item, group["resource"], Text(Property(scope["scope"], "name"))))));
}

internal sealed record TelemetryEntry(JsonObject Item, JsonNode? Resource, string? Scope);
