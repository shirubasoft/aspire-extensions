using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class DistributedDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentApplicationsExportTheirOwnLogsAndTracesBeforeTeardown()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var directory = Path.Combine(Path.GetTempPath(), "aspire-distributed-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            await using var firstBuilder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(
                cancellationToken: token);
            var configured = false;
            await using var secondBuilder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(
                args: ["--Sample:Configured=true"],
                configureBuilder: (options, settings) =>
                {
                    options.DashboardApplicationName = "Second test application";
                    configured = true;
                }, cancellationToken: token);
            Assert.True(configured);
            Assert.Equal("true", secondBuilder.Configuration["Sample:Configured"]);
            await using var first = await firstBuilder.BuildAsync(token);
            await using var second = await secondBuilder.BuildAsync(token);
            await Task.WhenAll(first.StartAsync(token), second.StartAsync(token));
            await Task.WhenAll(
                first.ResourceNotifications.WaitForResourceHealthyAsync("api", token),
                second.ResourceNotifications.WaitForResourceHealthyAsync("api", token));
            output.WriteLine($"Dashboard: {first.GetEndpoint("aspire-dashboard", "http")}");
            Assert.NotEqual(first.GetEndpoint("aspire-dashboard", "http"), second.GetEndpoint("aspire-dashboard", "http"));
            var firstMarker = Guid.NewGuid().ToString("N");
            var secondMarker = Guid.NewGuid().ToString("N");
            using var firstClient = first.CreateHttpClient("api", "http");
            using var secondClient = second.CreateHttpClient("api", "http");
            await firstClient.GetStringAsync($"/sample?marker={firstMarker}", token);
            await secondClient.GetStringAsync($"/sample?marker={secondMarker}", token);
            await WaitForTelemetryAsync(first, directory, firstMarker, token);
            await WaitForTelemetryAsync(second, directory, secondMarker, token);

            var firstExport = await first.RunWithDiagnosticsAsync(_ => Task.CompletedTask,
                new() { OutputDirectory = directory, ExportMode = TestDiagnosticsExportMode.Always }, token);
            Assert.NotNull(firstExport);
            Assert.Empty(firstExport.Errors);
            await AssertExportAsync(firstExport, firstMarker, secondMarker, token);

            var original = new InvalidOperationException("Simulated test assertion failure");
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => second.RunWithDiagnosticsAsync(
                _ => throw original, new() { OutputDirectory = directory }, token));
            Assert.Same(original, thrown);
            var secondExport = Assert.IsType<TestDiagnosticsExport>(thrown.Data["Aspire.TestDiagnostics"]);
            Assert.Empty(secondExport.Errors);
            await AssertExportAsync(secondExport, secondMarker, firstMarker, token);
            Assert.Null(await first.RunWithDiagnosticsAsync(_ => Task.CompletedTask, cancellationToken: token));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartupFailureStillProducesAnExportWithCollectionErrors()
    {
        await using var builder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(
            cancellationToken: TestContext.Current.CancellationToken);
        await using var app = await builder.BuildAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), "aspire-startup-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            var original = new InvalidOperationException("Startup failed before resources started");
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => app.RunWithDiagnosticsAsync(
                _ => throw original, new() { OutputDirectory = directory }, TestContext.Current.CancellationToken));
            var result = Assert.IsType<TestDiagnosticsExport>(thrown.Data["Aspire.TestDiagnostics"]);
            Assert.True(File.Exists(Path.Combine(result.DirectoryPath, "manifest.json")));
            Assert.NotEmpty(result.Errors);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static async Task<TestDiagnosticsExport> WaitForTelemetryAsync(DistributedApplication app, string directory,
        string marker, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await app.ExportDiagnosticsAsync(directory, cancellationToken);
            Assert.Empty(result.Errors);
            var logs = await File.ReadAllTextAsync(Path.Combine(result.DirectoryPath, "logs.json"), cancellationToken);
            var traces = await File.ReadAllTextAsync(Path.Combine(result.DirectoryPath, "traces.json"), cancellationToken);
            if (logs.Contains($"Sample diagnostic message {marker}", StringComparison.Ordinal) && traces.Contains(marker, StringComparison.Ordinal))
            {
                return result;
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task AssertExportAsync(TestDiagnosticsExport result, string expectedMarker,
        string unexpectedMarker, CancellationToken cancellationToken)
    {
        foreach (var file in new[] { "logs.json", "traces.json", "consolelogs/api.txt" })
        {
            var content = await File.ReadAllTextAsync(Path.Combine(result.DirectoryPath, file), cancellationToken);
            Assert.Contains(expectedMarker, content);
            Assert.DoesNotContain(unexpectedMarker, content);
        }
        Assert.True(File.Exists(Path.Combine(result.DirectoryPath, "manifest.json")));
    }
}
