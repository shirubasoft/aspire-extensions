using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class DiagnosticsExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aspire-diagnostics-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CollectionErrorsSurviveMissingServicesAndExportsHaveUniqueDirectories()
    {
        using var loggers = new ResourceLoggerService();
        var resource = new ExecutableResource("worker", "dotnet", ".");
        using var services = new ServiceCollection()
            .AddSingleton(new DistributedApplicationModel([resource]))
            .AddSingleton(loggers).BuildServiceProvider();
        var first = await DiagnosticsExporter.ExportAsync(services, _directory, TestContext.Current.CancellationToken);
        var second = await DiagnosticsExporter.ExportAsync(services, _directory, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.Contains(first.Errors, error => error.Contains("consolelogs/worker", StringComparison.Ordinal));
        Assert.Contains(first.Errors, error => error.Contains("dashboard is unavailable", StringComparison.Ordinal));
        Assert.Contains("telemetry", await File.ReadAllTextAsync(Path.Combine(first.DirectoryPath, "manifest.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndividualCollectionFailuresAreRecordedAndCancellationPropagates()
    {
        var errors = new List<string>();
        await DiagnosticsExporter.CollectAsync("source", errors, () => throw new IOException("failed"), TestContext.Current.CancellationToken);
        Assert.Contains("IOException: failed", Assert.Single(errors));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticsExporter.CollectAsync("source", errors,
            () => Task.FromCanceled(cancellation.Token), cancellation.Token));
        using var services = new ServiceCollection().BuildServiceProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticsExporter.ExportAsync(services, _directory, cancellation.Token));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task TelemetryFailureDoesNotPreventTheOtherSignalFromExporting()
    {
        Directory.CreateDirectory(_directory);
        using var client = new HttpClient(new TelemetryHandler()) { BaseAddress = new Uri("http://localhost") };
        var errors = new List<string>();
        await DashboardTelemetry.ExportResponsesAsync(client, _directory, errors, TestContext.Current.CancellationToken);
        Assert.Contains("logs:", Assert.Single(errors));
        Assert.False(File.Exists(Path.Combine(_directory, "logs.json")));
        Assert.Equal("{\"data\":{\"resourceSpans\":[]}}", await File.ReadAllTextAsync(Path.Combine(_directory, "traces.json"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test-key")]
    public void TelemetryClientUsesTheApplicationApiKey(string? apiKey)
    {
        using var client = DashboardTelemetry.CreateClient(new Uri("http://localhost:12345"), apiKey);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(apiKey is not null, client.DefaultRequestHeaders.Contains("x-api-key"));
        if (apiKey is not null)
        {
            Assert.Equal(apiKey, Assert.Single(client.DefaultRequestHeaders.GetValues("x-api-key")));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TelemetryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("?limit=2147483647", request.RequestUri!.Query);
            var response = request.RequestUri.AbsolutePath.EndsWith("logs", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"resourceSpans\":[]}}") };
            return Task.FromResult(response);
        }
    }
}
