using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Testing;

internal static class DiagnosticsExporter
{
    internal static async Task<TestDiagnosticsExport> ExportAsync(
        IServiceProvider services, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(Path.Combine(outputDirectory, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var errors = new List<string>();
        await ExportConsoleLogsAsync(services, directory, errors, cancellationToken).ConfigureAwait(false);
        await CollectAsync("telemetry", errors,
            () => DashboardTelemetry.ExportAsync(services, directory, errors, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var result = new TestDiagnosticsExport(directory, errors.AsReadOnly());
        await using var manifest = File.Create(Path.Combine(directory, "manifest.json"));
        await JsonSerializer.SerializeAsync(manifest, result, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task ExportConsoleLogsAsync(IServiceProvider services, string directory,
        List<string> errors, CancellationToken cancellationToken)
    {
        var model = services.GetRequiredService<DistributedApplicationModel>();
        var loggers = services.GetRequiredService<ResourceLoggerService>();
        Directory.CreateDirectory(Path.Combine(directory, "consolelogs"));
        foreach (var resource in model.Resources)
        {
            await CollectAsync($"consolelogs/{resource.Name}", errors,
                () => WriteConsoleLogsAsync(loggers, resource, directory, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteConsoleLogsAsync(ResourceLoggerService loggers, IResource resource,
        string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "consolelogs", Uri.EscapeDataString(resource.Name) + ".txt");
        await using var writer = new StreamWriter(path);
        await foreach (var batch in loggers.GetAllAsync(resource).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var line in batch)
            {
                await writer.WriteLineAsync(line.Content.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task CollectAsync(string source, List<string> errors, Func<Task> collect,
        CancellationToken cancellationToken)
    {
        try
        {
            await collect().ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            errors.Add($"{source}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
