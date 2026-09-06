namespace Aspire.Hosting.Testing;

internal static class DiagnosticsOperation
{
    internal static async Task<TestDiagnosticsExport?> RunAsync(
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task<TestDiagnosticsExport>> export,
        TestDiagnosticsOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await AttachExportAsync(exception, export, options.ExportTimeout).ConfigureAwait(false);
            throw;
        }

        return options.ExportMode == TestDiagnosticsExportMode.Always
            ? await ExportAsync(export, options.ExportTimeout).ConfigureAwait(false)
            : null;
    }

    private static async Task AttachExportAsync(Exception exception,
        Func<CancellationToken, Task<TestDiagnosticsExport>> export, TimeSpan timeout)
    {
        try
        {
            exception.Data["Aspire.TestDiagnostics"] = await ExportAsync(export, timeout).ConfigureAwait(false);
        }
        catch (Exception exportException)
        {
            exception.Data["Aspire.TestDiagnostics.ExportError"] = exportException;
        }
    }

    private static async Task<TestDiagnosticsExport> ExportAsync(
        Func<CancellationToken, Task<TestDiagnosticsExport>> export, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await export(cancellation.Token).ConfigureAwait(false);
    }
}
