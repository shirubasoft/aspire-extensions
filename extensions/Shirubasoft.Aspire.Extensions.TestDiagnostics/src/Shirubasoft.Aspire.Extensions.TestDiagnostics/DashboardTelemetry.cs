using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Testing;

internal static class DashboardTelemetry
{
    internal static async Task ExportAsync(IServiceProvider services, string directory,
        List<string> errors, CancellationToken cancellationToken)
    {
        var resource = services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<IResourceWithEndpoints>().SingleOrDefault(resource => resource.Name == "aspire-dashboard")
            ?? throw new InvalidOperationException("The dashboard is unavailable. Create the builder with DiagnosticsTestingBuilder.CreateAsync and start the application before exporting telemetry.");
        await services.GetRequiredService<ResourceNotificationService>()
            .WaitForResourceHealthyAsync(resource.Name, WaitBehavior.StopOnResourceUnavailable, cancellationToken)
            .ConfigureAwait(false);
        var endpoint = resource.GetEndpoint("https").Exists ? resource.GetEndpoint("https") : resource.GetEndpoint("http");
        var url = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var configuration = services.GetRequiredService<IConfiguration>();
        using var client = CreateClient(new Uri(url!), configuration["AppHost:DashboardApiKey"]);
        await ExportResponsesAsync(client, directory, errors, cancellationToken).ConfigureAwait(false);
    }

    internal static HttpClient CreateClient(Uri address, string? apiKey)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        }
        return client;
    }

    internal static async Task ExportResponsesAsync(HttpClient client, string directory,
        List<string> errors, CancellationToken cancellationToken)
    {
        foreach (var signal in new[] { "logs", "traces" })
        {
            await DiagnosticsExporter.CollectAsync(signal, errors,
                () => WriteResponseAsync(client, signal, directory, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteResponseAsync(HttpClient client, string signal, string directory,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/api/telemetry/{signal}?limit={int.MaxValue}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(Path.Combine(directory, signal + ".json"));
        await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
