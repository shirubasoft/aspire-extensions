using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

// An in-memory Cloudflare API for one account, one tunnel, and its zones. It implements
// the endpoints the integration calls, including DNS list filters and pagination, so
// tests exercise the real CloudflareApiClient against stateful API behavior.
internal sealed partial class FakeCloudflare : HttpMessageHandler
{
    public const string AccountId = "account-id";
    public const string TunnelId = "tunnel-id";
    public const string TunnelName = "public";
    public const string TunnelTarget = $"{TunnelId}.cfargotunnel.com";

    private int _nextRecordId;

    public List<(string Id, string Name)> Zones { get; } = [("zone-id", "example.com")];

    public List<FakeDnsRecord> DnsRecords { get; } = [];

    public List<(string? Hostname, string Service)> Ingress { get; set; } = [];

    public int ConfigurationVersion { get; private set; }

    // Cloudflare caps per_page on the server. A small cap forces multi-page lists.
    public int MaxPageSize { get; set; } = 100;

    // Fails the next request whose "{METHOD} {resource}" matches, such as
    // "PUT configurations" or "DELETE dns_records".
    public string? FailNext { get; set; }

    public List<string> Writes { get; } = [];

    public FakeDnsRecord AddDnsRecord(
        string name,
        string content,
        string? comment = null,
        string type = "CNAME") =>
        AddRecord("zone-id", type, name, content, comment);

    public ICloudflareApiClientFactory CreateClientFactory() => new ClientFactory(this);

    public CloudflareTunnelResource CreateTunnel() =>
        new(TunnelName)
        {
            TunnelId = TunnelId,
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = ParseQuery(uri.Query);
        var body = request.Content is null
            ? null
            : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        var match = Route().Match(uri.AbsolutePath);
        var resource = match.Groups["resource"].Value;
        var key = $"{request.Method.Method} {resource}";

        if (FailNext == key)
        {
            FailNext = null;
            return Failure("injected failure");
        }

        return key switch
        {
            "GET zones" => Page(Zones
                .Where(zone => Matches(zone.Name, query, "name"))
                .Select(zone => new JsonObject
                {
                    ["id"] = zone.Id,
                    ["name"] = zone.Name,
                    ["status"] = "active",
                }), query),
            "GET dns_records" => Page(
                DnsRecords
                    .Where(record => record.ZoneId == match.Groups["zone"].Value)
                    .Where(record => Matches(record.Name, query, "name"))
                    .Where(record => Matches(record.Type, query, "type"))
                    .Where(record => Matches(record.Comment, query, "comment"))
                    .Select(record => record.ToJson()),
                query),
            "POST dns_records" => Write(key, body!, () => AddRecord(
                match.Groups["zone"].Value,
                (string)body!["type"]!,
                (string)body["name"]!,
                (string)body["content"]!,
                (string?)body["comment"]).ToJson()),
            "PUT dns_records" => Write(key, body!, () => UpdateRecord(match.Groups["id"].Value, body!)),
            "DELETE dns_records" => Write(key, body, () => DeleteRecord(match.Groups["id"].Value)),
            "GET configurations" => Ok(new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["ingress"] = new JsonArray([.. Ingress.Select(rule => (JsonNode)RuleToJson(rule))]),
                },
                ["version"] = ConfigurationVersion,
            }),
            "PUT configurations" => Write(key, body!, () => UpdateConfiguration(body!)),
            "GET cfd_tunnel" => Ok(new JsonArray(new JsonObject
            {
                ["id"] = TunnelId,
                ["name"] = TunnelName,
                ["status"] = "healthy",
            })),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private FakeDnsRecord AddRecord(
        string zoneId,
        string type,
        string name,
        string content,
        string? comment)
    {
        var record = new FakeDnsRecord
        {
            Id = $"record-{++_nextRecordId}",
            ZoneId = zoneId,
            Type = type,
            Name = name.ToLowerInvariant(),
            Content = content,
            Comment = comment,
        };
        DnsRecords.Add(record);
        return record;
    }

    private JsonObject UpdateRecord(string id, JsonNode body)
    {
        var record = DnsRecords.Single(candidate => candidate.Id == id);
        record.Type = (string)body["type"]!;
        record.Name = ((string)body["name"]!).ToLowerInvariant();
        record.Content = (string)body["content"]!;
        record.Proxied = (bool)body["proxied"]!;
        record.Comment = (string?)body["comment"];
        return record.ToJson();
    }

    private JsonObject DeleteRecord(string id)
    {
        DnsRecords.RemoveAll(record => record.Id == id);
        return new JsonObject { ["id"] = id };
    }

    private JsonObject UpdateConfiguration(JsonNode body)
    {
        Ingress =
        [
            .. body["config"]!["ingress"]!.AsArray().Select(rule => (
                (string?)rule!["hostname"],
                (string)rule["service"]!)),
        ];
        ConfigurationVersion++;
        return new JsonObject { ["version"] = ConfigurationVersion };
    }

    private HttpResponseMessage Write(string key, JsonNode? body, Func<JsonNode> apply)
    {
        var target = (string?)body?["name"] ?? string.Empty;
        Writes.Add($"{key} {target}".TrimEnd());
        return Ok(apply());
    }

    private static JsonObject RuleToJson((string? Hostname, string Service) rule)
    {
        var json = new JsonObject { ["service"] = rule.Service };
        if (rule.Hostname is not null)
        {
            json["hostname"] = rule.Hostname;
        }

        return json;
    }

    // Cloudflare list filters match exactly and ignore case.
    private static bool Matches(string? value, Dictionary<string, string> query, string field) =>
        (query.GetValueOrDefault(field) ?? query.GetValueOrDefault($"{field}.exact")) is not { } expected
        || string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private HttpResponseMessage Page(IEnumerable<JsonNode> items, Dictionary<string, string> query)
    {
        var all = items.ToArray();
        var perPage = Math.Min(
            int.Parse(query.GetValueOrDefault("per_page") ?? "100", System.Globalization.CultureInfo.InvariantCulture),
            MaxPageSize);
        var page = int.Parse(query.GetValueOrDefault("page") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var totalPages = Math.Max(1, (all.Length + perPage - 1) / perPage);

        return Ok(
            new JsonArray([.. all.Skip((page - 1) * perPage).Take(perPage).Select(item => item.DeepClone())]),
            new JsonObject
            {
                ["page"] = page,
                ["per_page"] = perPage,
                ["count"] = Math.Min(perPage, Math.Max(0, all.Length - ((page - 1) * perPage))),
                ["total_count"] = all.Length,
                ["total_pages"] = totalPages,
            });
    }

    private static HttpResponseMessage Ok(JsonNode result, JsonObject? resultInfo = null) =>
        Json(new JsonObject
        {
            ["success"] = true,
            ["errors"] = new JsonArray(),
            ["messages"] = new JsonArray(),
            ["result"] = result,
            ["result_info"] = resultInfo,
        });

    private static HttpResponseMessage Failure(string message) =>
        Json(new JsonObject
        {
            ["success"] = false,
            ["errors"] = new JsonArray(new JsonObject { ["code"] = 1000, ["message"] = message }),
            ["result"] = null,
        });

    private static HttpResponseMessage Json(JsonObject content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    private static Dictionary<string, string> ParseQuery(string query) =>
        query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? string.Empty));

    [GeneratedRegex(
        @"^/client/v4/(?:zones/(?<zone>[^/]+)/(?<resource>dns_records)(?:/(?<id>[^/]+))?|(?<resource>zones)|accounts/[^/]+/cfd_tunnel/[^/]+/(?<resource>configurations)|accounts/[^/]+/(?<resource>cfd_tunnel))$")]
    private static partial Regex Route();

    private sealed class ClientFactory(FakeCloudflare cloudflare) : ICloudflareApiClientFactory
    {
        public ValueTask<ICloudflareApiClient> CreateAsync(
            CloudflareTunnelResource tunnel,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ICloudflareApiClient>(new CloudflareApiClient(
                new HttpClient(cloudflare, disposeHandler: false)
                {
                    BaseAddress = new Uri("https://api.cloudflare.com"),
                },
                "api-token",
                AccountId));
    }
}

internal sealed class FakeDnsRecord
{
    public required string Id { get; init; }

    public required string ZoneId { get; init; }

    public required string Type { get; set; }

    public required string Name { get; set; }

    public required string Content { get; set; }

    public bool Proxied { get; set; } = true;

    public string? Comment { get; set; }

    public JsonObject ToJson() =>
        new()
        {
            ["id"] = Id,
            ["type"] = Type,
            ["name"] = Name,
            ["content"] = Content,
            ["proxied"] = Proxied,
            ["ttl"] = 1,
            ["comment"] = Comment,
        };
}

internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
