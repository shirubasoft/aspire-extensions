namespace Aspire.Hosting.Testing;

/// <summary>Exports diagnostics from the exact distributed application under test.</summary>
public static class TestDiagnosticsExtensions
{
    /// <summary>Exports console logs and dashboard logs and traces before application teardown.</summary>
    /// <param name="application">The application whose diagnostics should be exported.</param>
    /// <param name="outputDirectory">The parent directory for a unique export directory.</param>
    /// <param name="cancellationToken">Cancels collection and file writes.</param>
    /// <returns>The export directory and any individual collection errors.</returns>
    public static Task<TestDiagnosticsExport> ExportDiagnosticsAsync(
        this DistributedApplication application,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return DiagnosticsExporter.ExportAsync(application.Services, outputDirectory, cancellationToken);
    }

    /// <summary>Runs an operation and exports on failure or always, before the caller disposes the application.</summary>
    /// <remarks>
    /// The original operation exception is rethrown. Its Data contains Aspire.TestDiagnostics with the export
    /// result, or Aspire.TestDiagnostics.ExportError if exporting itself fails. Export uses an independent
    /// timeout so a canceled test can still collect diagnostics. Successful operations return their export.
    /// </remarks>
    /// <param name="application">The caller-owned application.</param>
    /// <param name="operation">The test operation, optionally including application startup.</param>
    /// <param name="options">Export settings. Defaults to exporting on failure.</param>
    /// <param name="cancellationToken">Cancellation passed to the operation.</param>
    /// <returns>The export, or null when a successful operation did not request one.</returns>
    public static async Task<TestDiagnosticsExport?> RunWithDiagnosticsAsync(
        this DistributedApplication application,
        Func<CancellationToken, Task> operation,
        TestDiagnosticsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(operation);
        options ??= new TestDiagnosticsOptions();
        options.Validate();
        return await DiagnosticsOperation.RunAsync(operation,
            token => application.ExportDiagnosticsAsync(options.OutputDirectory, token),
            options, cancellationToken).ConfigureAwait(false);
    }
}
