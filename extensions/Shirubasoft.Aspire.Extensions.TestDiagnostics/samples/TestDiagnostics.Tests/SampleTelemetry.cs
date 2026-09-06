using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Xunit;

namespace TestDiagnostics.Sample.Tests;

internal static class SampleTelemetry
{
    internal static async Task WaitForDeliveryAsync(DistributedApplication app, string marker, CancellationToken cancellationToken)
    {
        // OTLP delivery is asynchronous. Use temporary snapshots to wait for the exact sample message and span.
        var scratch = Directory.CreateTempSubdirectory("aspire-sample-readiness-").FullName;
        try
        {
            while (true)
            {
                var snapshot = await app.ExportDiagnosticsAsync(scratch, cancellationToken);
                Assert.Empty(snapshot.Errors);
                var logs = await File.ReadAllTextAsync(Path.Combine(snapshot.DirectoryPath, "logs.json"), cancellationToken);
                var traces = await File.ReadAllTextAsync(Path.Combine(snapshot.DirectoryPath, "traces.json"), cancellationToken);
                Directory.Delete(snapshot.DirectoryPath, recursive: true);
                if (logs.Contains($"Sample diagnostic message {marker}", StringComparison.Ordinal)
                    && traces.Contains(marker, StringComparison.Ordinal))
                {
                    return;
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    internal static async Task AssertExportAsync(TestDiagnosticsExport export, string marker, CancellationToken cancellationToken)
    {
        Assert.Empty(export.Errors);
        foreach (var file in new[] { "logs.json", "traces.json", "otlp/logs.json", "otlp/traces.json", "consolelogs/api.txt" })
        {
            var content = await File.ReadAllTextAsync(Path.Combine(export.DirectoryPath, file), cancellationToken);
            Assert.Contains(marker, content);
        }
        Assert.True(File.Exists(Path.Combine(export.DirectoryPath, "manifest.json")));
    }
}
