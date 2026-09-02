using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CloudflareApiClientTests
{
    [Fact]
    public async Task ClientUsesCloudflareEndpointsAndDeserializesResponses()
    {
        var handler = new RecordingHttpMessageHandler(
            JsonResponse(
                """
                {"success":true,"result":[{"id":"tunnel-id","name":"public","status":"healthy","created_at":null,"deleted_at":null}]}
                """),
            JsonResponse(
                """
                {"success":true,"result":{"id":"created-id","name":"created","status":"inactive","created_at":null,"deleted_at":null}}
                """),
            JsonResponse("""{"success":true,"result":"secret-token"}"""),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            JsonResponse(
                """
                {"success":true,"result":{"config":{"ingress":[{"hostname":"app.example.com","service":"http://web:80"}]}}}
                """),
            JsonResponse("""{"success":true,"result":{}}"""),
            JsonResponse(
                """
                {"success":true,"result":[{"id":"zone-id","name":"example.com","status":"active"}]}
                """),
            JsonResponse("""{"success":true,"result":[]}"""),
            JsonResponse(
                """
                {"success":true,"result":{"id":"record-id","type":"CNAME","name":"app.example.com","content":"tunnel-id.cfargotunnel.com","proxied":true,"ttl":1}}
                """));
        using var client = CreateClient(handler);

        var tunnel = await client.FindTunnelByNameAsync(
            "public tunnel",
            TestContext.Current.CancellationToken);
        var created = await client.CreateTunnelAsync(
            "created",
            TestContext.Current.CancellationToken);
        var token = await client.GetTunnelTokenAsync(
            "tunnel/id",
            TestContext.Current.CancellationToken);
        var missingConfiguration = await client.GetTunnelConfigurationAsync(
            "missing",
            TestContext.Current.CancellationToken);
        var configuration = await client.GetTunnelConfigurationAsync(
            "tunnel-id",
            TestContext.Current.CancellationToken);
        await client.UpdateTunnelConfigurationAsync(
            "tunnel-id",
            configuration!,
            TestContext.Current.CancellationToken);
        var zone = await client.FindZoneByNameAsync(
            "example.com",
            TestContext.Current.CancellationToken);
        var record = await client.UpsertTunnelDnsRecordAsync(
            "zone-id",
            "app.example.com",
            "tunnel-id",
            TestContext.Current.CancellationToken);

        Assert.Equal("tunnel-id", tunnel?.Id);
        Assert.Equal("created-id", created.Id);
        Assert.Equal("secret-token", token);
        Assert.Null(missingConfiguration);
        Assert.Equal("http://web:80", Assert.Single(configuration!.Ingress).Service);
        Assert.Equal("zone-id", zone?.Id);
        Assert.Equal("record-id", record.Id);
        Assert.All(
            handler.Requests,
            request => Assert.Equal("Bearer test-token", request.Authorization));
        Assert.Contains(
            handler.Requests,
            request => request.Uri.Contains(
                "name=public tunnel",
                StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Uri.Contains(
                "tunnel%2Fid/token",
                StringComparison.Ordinal));
        var createTunnel = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Post
                && request.Uri.EndsWith("/cfd_tunnel", StringComparison.Ordinal));
        Assert.Contains("tunnel_secret", createTunnel.Body, StringComparison.Ordinal);
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Post
                && request.Uri.EndsWith("/dns_records", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertUpdatesAnExistingDnsRecord()
    {
        var handler = new RecordingHttpMessageHandler(
            JsonResponse(
                """
                {"success":true,"result":[{"id":"record/id","type":"CNAME","name":"app.example.com","content":"old.example.com","proxied":true,"ttl":1}]}
                """),
            JsonResponse(
                """
                {"success":true,"result":{"id":"record/id","type":"CNAME","name":"app.example.com","content":"tunnel-id.cfargotunnel.com","proxied":true,"ttl":1}}
                """));
        using var client = CreateClient(handler);

        var record = await client.UpsertTunnelDnsRecordAsync(
            "zone-id",
            "app.example.com",
            "tunnel-id",
            TestContext.Current.CancellationToken);

        Assert.Equal("tunnel-id.cfargotunnel.com", record.Content);
        var update = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.EndsWith("/dns_records/record%2Fid", update.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"success":false,"result":null,"errors":[{"code":1000,"message":"denied"}]}""",
        "[1000] denied")]
    [InlineData(
        """{"success":false,"result":null,"errors":[]}""",
        "Unknown error")]
    public async Task ClientReportsCloudflareApiErrors(
        string responseBody,
        string expectedMessage)
    {
        var handler = new RecordingHttpMessageHandler(JsonResponse(responseBody));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudflareApiException>(() =>
            client.GetTunnelTokenAsync(
                "tunnel-id",
                TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRejectsASuccessResponseWithoutAResult()
    {
        var handler = new RecordingHttpMessageHandler(
            JsonResponse("""{"success":true,"result":null}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CloudflareApiException>(() =>
            client.GetTunnelTokenAsync(
                "tunnel-id",
                TestContext.Current.CancellationToken));

        Assert.Contains("no result", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CloudflareApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.cloudflare.com"),
            },
            "test-token",
            "account-id");

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class RecordingHttpMessageHandler(
        params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                FormatAuthorization(request.Headers.Authorization)));
            return _responses.Dequeue();
        }

        private static string? FormatAuthorization(
            AuthenticationHeaderValue? authorization) =>
            authorization is null
                ? null
                : $"{authorization.Scheme} {authorization.Parameter}";
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string Body,
        string? Authorization);
}
