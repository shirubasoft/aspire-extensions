using System.Globalization;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Testing;

internal static class TelemetryStatusNames
{
    private static readonly IReadOnlyDictionary<int, string> Http = new Dictionary<int, string>
    {
        [200] = "200 OK",
        [201] = "201 Created",
        [204] = "204 No Content",
        [301] = "301 Moved Permanently",
        [302] = "302 Found",
        [304] = "304 Not Modified",
        [400] = "400 Bad Request",
        [401] = "401 Unauthorized",
        [403] = "403 Forbidden",
        [404] = "404 Not Found",
        [405] = "405 Method Not Allowed",
        [408] = "408 Request Timeout",
        [409] = "409 Conflict",
        [422] = "422 Unprocessable Entity",
        [429] = "429 Too Many Requests",
        [500] = "500 Internal Server Error",
        [501] = "501 Not Implemented",
        [502] = "502 Bad Gateway",
        [503] = "503 Service Unavailable",
        [504] = "504 Gateway Timeout",
    };
    private static readonly IReadOnlyDictionary<int, string> Grpc = new Dictionary<int, string>
    {
        [0] = "OK",
        [1] = "CANCELLED",
        [2] = "UNKNOWN",
        [3] = "INVALID_ARGUMENT",
        [4] = "DEADLINE_EXCEEDED",
        [5] = "NOT_FOUND",
        [6] = "ALREADY_EXISTS",
        [7] = "PERMISSION_DENIED",
        [8] = "RESOURCE_EXHAUSTED",
        [9] = "FAILED_PRECONDITION",
        [10] = "ABORTED",
        [11] = "OUT_OF_RANGE",
        [12] = "UNIMPLEMENTED",
        [13] = "INTERNAL",
        [14] = "UNAVAILABLE",
        [15] = "DATA_LOSS",
        [16] = "UNAUTHENTICATED",
    };
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> Names =
        new Dictionary<string, IReadOnlyDictionary<int, string>>
        {
            ["http.response.status_code"] = Http,
            ["rpc.grpc.status_code"] = Grpc,
        };

    internal static string Attribute(JsonObject attribute)
    {
        var value = TelemetryJson.AttributeValue(attribute["value"]);
        var names = Names.GetValueOrDefault(TelemetryJson.Text(attribute["key"])!);
        return names is null ? value : Status(names, value);
    }

    private static string Status(IReadOnlyDictionary<int, string> names, string value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var code)
            ? names.GetValueOrDefault(code, code.ToString(CultureInfo.InvariantCulture)) : value;
}
