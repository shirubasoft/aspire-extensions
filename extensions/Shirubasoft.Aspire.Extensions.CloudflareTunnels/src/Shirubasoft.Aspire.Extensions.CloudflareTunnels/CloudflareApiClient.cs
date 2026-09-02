using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal interface ICloudflareApiClientFactory
{
    ValueTask<ICloudflareApiClient> CreateAsync(
        CloudflareTunnelResource tunnel,
        CancellationToken cancellationToken);
}

internal sealed class CloudflareApiClientFactory : ICloudflareApiClientFactory
{
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";

    public async ValueTask<ICloudflareApiClient> CreateAsync(
        CloudflareTunnelResource tunnel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tunnel);

        var credentials = RequireCredentialsAnnotation(tunnel);

        var apiToken = await credentials.ApiToken.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var accountId = await credentials.AccountId.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var values = RequireCredentials(apiToken, accountId, tunnel.Name);

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
        };

        return new CloudflareApiClient(httpClient, values.ApiToken, values.AccountId);
    }

    internal static CloudflareCredentials RequireCredentials(
        string? apiToken,
        string? accountId,
        string tunnelName)
    {
        if (string.IsNullOrWhiteSpace(apiToken) || string.IsNullOrWhiteSpace(accountId))
        {
            throw new InvalidOperationException(
                $"Cloudflare API token and account ID are required for tunnel '{tunnelName}'.");
        }

        return new(apiToken, accountId);
    }

    internal static CloudflareTunnelCredentialsAnnotation RequireCredentialsAnnotation(
        CloudflareTunnelResource tunnel)
    {
        if (!tunnel.TryGetLastAnnotation<CloudflareTunnelCredentialsAnnotation>(out var credentials))
        {
            throw new InvalidOperationException(
                $"Cloudflare API credentials are not configured on tunnel '{tunnel.Name}'.");
        }

        return credentials;
    }
}

internal interface ICloudflareApiClient : IDisposable
{
    Task<CloudflareTunnelInfo?> FindTunnelByNameAsync(
        string name,
        CancellationToken cancellationToken);

    Task<CloudflareTunnelInfo> CreateTunnelAsync(
        string name,
        CancellationToken cancellationToken);

    Task<string> GetTunnelTokenAsync(
        string tunnelId,
        CancellationToken cancellationToken);

    Task<TunnelConfiguration?> GetTunnelConfigurationAsync(
        string tunnelId,
        CancellationToken cancellationToken);

    Task UpdateTunnelConfigurationAsync(
        string tunnelId,
        TunnelConfiguration configuration,
        CancellationToken cancellationToken);

    Task<CloudflareZoneInfo?> FindZoneByNameAsync(
        string domainName,
        CancellationToken cancellationToken);

    Task<CloudflareDnsRecord> UpsertTunnelDnsRecordAsync(
        string zoneId,
        string hostname,
        string tunnelId,
        CancellationToken cancellationToken);
}

internal sealed class CloudflareApiClient : ICloudflareApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly string _accountId;

    public CloudflareApiClient(
        HttpClient httpClient,
        string apiToken,
        string accountId)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        _httpClient = httpClient;
        _accountId = accountId;
        _httpClient.DefaultRequestHeaders.Authorization =
            new("Bearer", apiToken);
    }

    public async Task<CloudflareTunnelInfo?> FindTunnelByNameAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var tunnels = await GetRequiredResultAsync<CloudflareTunnelInfo[]>(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false",
            "Find tunnel",
            cancellationToken).ConfigureAwait(false);

        return tunnels.FirstOrDefault();
    }

    public Task<CloudflareTunnelInfo> CreateTunnelAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var tunnelSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return SendRequiredResultAsync<CloudflareTunnelInfo>(
            HttpMethod.Post,
            $"/client/v4/accounts/{_accountId}/cfd_tunnel",
            new CreateTunnelRequest(name, tunnelSecret),
            "Create tunnel",
            cancellationToken);
    }

    public Task<string> GetTunnelTokenAsync(
        string tunnelId,
        CancellationToken cancellationToken) =>
        GetRequiredResultAsync<string>(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{Uri.EscapeDataString(tunnelId)}/token",
            "Get tunnel token",
            cancellationToken);

    public async Task<TunnelConfiguration?> GetTunnelConfigurationAsync(
        string tunnelId,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{Uri.EscapeDataString(tunnelId)}/configurations",
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var wrapper = await ReadRequiredResultAsync<TunnelConfigurationWrapper>(
            response,
            "Get tunnel configuration",
            cancellationToken).ConfigureAwait(false);
        return wrapper.Config;
    }

    public async Task UpdateTunnelConfigurationAsync(
        string tunnelId,
        TunnelConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{Uri.EscapeDataString(tunnelId)}/configurations",
            new TunnelConfigurationWrapper(configuration),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(
            response,
            "Update tunnel configuration",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudflareZoneInfo?> FindZoneByNameAsync(
        string domainName,
        CancellationToken cancellationToken)
    {
        var zones = await GetRequiredResultAsync<CloudflareZoneInfo[]>(
            $"/client/v4/zones?name={Uri.EscapeDataString(domainName)}&account.id={Uri.EscapeDataString(_accountId)}",
            "Find zone",
            cancellationToken).ConfigureAwait(false);

        return zones.FirstOrDefault();
    }

    public async Task<CloudflareDnsRecord> UpsertTunnelDnsRecordAsync(
        string zoneId,
        string hostname,
        string tunnelId,
        CancellationToken cancellationToken)
    {
        var request = new CreateDnsRecordRequest(
            "CNAME",
            hostname,
            $"{tunnelId}.cfargotunnel.com",
            Proxied: true,
            Ttl: 1);

        var existing = await FindDnsRecordAsync(
            zoneId,
            hostname,
            cancellationToken).ConfigureAwait(false);

        return existing is null
            ? await SendRequiredResultAsync<CloudflareDnsRecord>(
                HttpMethod.Post,
                $"/client/v4/zones/{Uri.EscapeDataString(zoneId)}/dns_records",
                request,
                "Create DNS record",
                cancellationToken).ConfigureAwait(false)
            : await SendRequiredResultAsync<CloudflareDnsRecord>(
                HttpMethod.Put,
                $"/client/v4/zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(existing.Id)}",
                request,
                "Update DNS record",
                cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<CloudflareDnsRecord?> FindDnsRecordAsync(
        string zoneId,
        string hostname,
        CancellationToken cancellationToken)
    {
        var records = await GetRequiredResultAsync<CloudflareDnsRecord[]>(
            $"/client/v4/zones/{Uri.EscapeDataString(zoneId)}/dns_records?name={Uri.EscapeDataString(hostname)}&type=CNAME",
            "Find DNS record",
            cancellationToken).ConfigureAwait(false);

        return records.FirstOrDefault();
    }

    private async Task<T> GetRequiredResultAsync<T>(
        string requestUri,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            requestUri,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredResultAsync<T>(
            response,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendRequiredResultAsync<T>(
        HttpMethod method,
        string requestUri,
        object request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        using var response = await _httpClient.SendAsync(
            message,
            cancellationToken).ConfigureAwait(false);

        return await ReadRequiredResultAsync<T>(
            response,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadRequiredResultAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var apiResponse = await ReadResponseAsync<T>(
            response,
            cancellationToken).ConfigureAwait(false);
        EnsureCloudflareSuccess(apiResponse, operation);

        return apiResponse.Result
            ?? throw new CloudflareApiException($"{operation} failed because Cloudflare returned no result.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var apiResponse = await ReadResponseAsync<JsonElement>(
            response,
            cancellationToken).ConfigureAwait(false);
        EnsureCloudflareSuccess(apiResponse, operation);
    }

    private static async Task<CloudflareApiResponse<T>> ReadResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<CloudflareApiResponse<T>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CloudflareApiException("Cloudflare returned an empty response.");
    }

    private static void EnsureCloudflareSuccess<T>(
        CloudflareApiResponse<T> response,
        string operation)
    {
        if (response.Success)
        {
            return;
        }

        throw new CloudflareApiException(
            $"{operation} failed: {FormatErrors(response.Errors)}");
    }

    private static string FormatErrors(CloudflareApiError[]? errors)
    {
        if (errors is null || errors.Length == 0)
        {
            return "Unknown error";
        }

        return string.Join(
            "; ",
            errors.Select(error => $"[{error.Code}] {error.Message}"));
    }
}

internal sealed class CloudflareApiException(string message) : Exception(message);

internal sealed record CloudflareCredentials(string ApiToken, string AccountId);

internal sealed record CloudflareApiResponse<T>(
    bool Success,
    T? Result,
    CloudflareApiError[]? Errors,
    CloudflareApiMessage[]? Messages);

internal sealed record CloudflareApiError(int Code, string Message);

internal sealed record CloudflareApiMessage(int Code, string Message);

internal sealed record CreateTunnelRequest(string Name, string TunnelSecret);

internal sealed record TunnelConfigurationWrapper(TunnelConfiguration Config);

internal sealed record CreateDnsRecordRequest(
    string Type,
    string Name,
    string Content,
    bool Proxied,
    int Ttl);

internal sealed record CloudflareTunnelInfo(
    string Id,
    string Name,
    string Status,
    string? CreatedAt,
    string? DeletedAt);

internal sealed record CloudflareZoneInfo(
    string Id,
    string Name,
    string Status);

internal sealed record CloudflareDnsRecord(
    string Id,
    string Type,
    string Name,
    string Content,
    bool Proxied,
    int Ttl);

internal sealed class TunnelConfiguration
{
    [JsonPropertyName("ingress")]
    public List<IngressRule> Ingress { get; init; } = [];
}

internal sealed class IngressRule
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}
