using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class DashboardImportTests
{
    [Fact]
    public async Task ExportedTelemetryImportsIntoThePinnedAspireDashboard()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("aspire-import-").FullName;
        try
        {
            TestDiagnosticsExport export;
            var marker = Guid.NewGuid().ToString("N");
            await using (var builder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(cancellationToken: token))
            await using (var app = await builder.BuildAsync(token))
            {
                await app.StartAsync(token);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("api", token);
                using var client = app.CreateHttpClient("api", "http");
                await client.GetStringAsync($"/sample?marker={marker}", token);
                export = await DistributedDiagnosticsTests.WaitForTelemetryAsync(app, directory, marker, token);
            }

            // Import after the source application is gone, into the same service used by the dashboard UI.
            await using var services = new ServiceCollection().AddLogging().AddOptions()
                .AddSingleton<PauseManager>().AddSingleton<TelemetryRepository>()
                .AddSingleton<TelemetryImportService>().BuildServiceProvider();
            var repository = services.GetRequiredService<TelemetryRepository>();
            var importer = services.GetRequiredService<TelemetryImportService>();
            Assert.Empty(repository.GetResources());
            foreach (var signal in new[] { "logs", "traces" })
            {
                await using var input = File.OpenRead(Path.Combine(export.DirectoryPath, "otlp", signal + ".json"));
                await importer.ImportAsync(signal + ".json", input, token);
            }

            var resource = Assert.Single(repository.GetResources(), resource => resource.ResourceName == "api");
            var logs = repository.GetLogs(GetLogsContext.ForResourceKey(resource.ResourceKey));
            var log = Assert.Single(logs.Items, log => log.Message == $"Sample diagnostic message {marker}");
            var traces = repository.GetTraces(GetTracesRequest.ForResourceKey(resource.ResourceKey));
            var span = Assert.Single(traces.PagedResult.Items.SelectMany(trace => trace.Spans), span => span.Name == "sample-request");
            Assert.False(string.IsNullOrEmpty(log.TraceId));
            Assert.False(string.IsNullOrEmpty(log.SpanId));
            Assert.Equal(log.TraceId, span.TraceId);
            Assert.Equal(log.SpanId, span.SpanId);
            Assert.Contains(span.Attributes, attribute => attribute.Key == "sample.marker" && attribute.Value.ToString() == marker);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
