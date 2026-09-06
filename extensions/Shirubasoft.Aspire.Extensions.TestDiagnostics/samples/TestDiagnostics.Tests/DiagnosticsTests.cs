using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Xunit;
using Xunit.Sdk;

namespace TestDiagnostics.Sample.Tests;

public sealed class DiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FailedAssertionExportsBeforeTeardown()
    {
        using var timeout = CreateTimeout();
        await using var builder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(
            cancellationToken: timeout.Token);
        await using var app = await builder.BuildAsync(timeout.Token);
        var marker = Guid.NewGuid().ToString("N");

        // The outer assertion keeps this demonstration green. Real tests let the failure reach their runner.
        var failure = await Assert.ThrowsAsync<FailException>(() => app.RunWithDiagnosticsAsync(async token =>
        {
            await ExerciseApplicationAsync(app, marker, token);
            Assert.Fail("Intentional assertion failure to demonstrate diagnostics capture.");
        }, new TestDiagnosticsOptions
        {
            OutputDirectory = OutputDirectory("failed-assertion")
        }, timeout.Token));

        var export = Assert.IsType<TestDiagnosticsExport>(failure.Data["Aspire.TestDiagnostics"]);
        output.WriteLine($"Failure diagnostics: {export.DirectoryPath}");
        await SampleTelemetry.AssertExportAsync(export, marker, timeout.Token);
    }

    [Fact]
    public async Task SuccessfulAssertionAlwaysExportsForCi()
    {
        using var timeout = CreateTimeout();
        await using var builder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(
            cancellationToken: timeout.Token);
        await using var app = await builder.BuildAsync(timeout.Token);
        var marker = Guid.NewGuid().ToString("N");

        var export = await app.RunWithDiagnosticsAsync(async token =>
        {
            var response = await ExerciseApplicationAsync(app, marker, token);
            Assert.Equal("Diagnostics emitted", response);
        }, new TestDiagnosticsOptions
        {
            OutputDirectory = OutputDirectory("always-export"),
            ExportMode = TestDiagnosticsExportMode.Always
        }, timeout.Token);

        Assert.NotNull(export);
        output.WriteLine($"Successful test diagnostics: {export.DirectoryPath}");
        await SampleTelemetry.AssertExportAsync(export, marker, timeout.Token);
    }

    [Fact]
    public async Task ExplicitExportInTeardown()
    {
        using var timeout = CreateTimeout();
        await using var builder = await DiagnosticsTestingBuilder.CreateAsync<Projects.TestDiagnostics_AppHost>(
            cancellationToken: timeout.Token);
        await using var app = await builder.BuildAsync(timeout.Token);
        var marker = Guid.NewGuid().ToString("N");
        TestDiagnosticsExport export;
        try
        {
            var response = await ExerciseApplicationAsync(app, marker, timeout.Token);
            Assert.Equal("Diagnostics emitted", response);
        }
        finally
        {
            // A runner's asynchronous teardown hook can make this call before disposing the AppHost.
            using var exportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            export = await app.ExportDiagnosticsAsync(OutputDirectory("explicit-export"), exportTimeout.Token);
            output.WriteLine($"Teardown diagnostics: {export.DirectoryPath}");
        }
        await SampleTelemetry.AssertExportAsync(export, marker, timeout.Token);
    }

    private async Task<string> ExerciseApplicationAsync(DistributedApplication app, string marker, CancellationToken cancellationToken)
    {
        await app.StartAsync(cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);
        output.WriteLine($"Dashboard while the test is running: {app.GetEndpoint("aspire-dashboard", "http")}");
        using var client = app.CreateHttpClient("api", "http");
        var response = await client.GetStringAsync($"/sample?marker={marker}", cancellationToken);
        await SampleTelemetry.WaitForDeliveryAsync(app, marker, cancellationToken);
        return response;
    }

    private static string OutputDirectory(string scenario) =>
        Path.Combine(AppContext.BaseDirectory, "TestResults", "aspire-diagnostics", scenario);

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        return timeout;
    }
}
