using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class TestRunArtifacts
{
    internal static async Task<ExecuteCommandResult> ReadAndSaveAsync(string name, TestRunSession session, CustomResourceSnapshot snapshot, CancellationToken token)
    {
        try
        {
            var result = await ReadAsync(name, session, snapshot, token);
            result = WithArtifacts(result, session.DirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(session.DirectoryPath, "results.md"), result.Data!.Value, token);
            return result;
        }
        catch (OperationCanceledException)
        {
            return TestResultMarkdown.Error("Result collection canceled", "The report could not be collected before shutdown.", canceled: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TestResultMarkdown.Error("Could not save test results", exception.Message);
        }
    }

    private static async Task<ExecuteCommandResult> ReadAsync(string name, TestRunSession session, CustomResourceSnapshot snapshot, CancellationToken token)
    {
        if (session.Canceled)
        {
            return TestResultMarkdown.Error("Test run canceled", "The test project was stopped. Partial reports remain in the run directory.", canceled: true);
        }
        try
        {
            var report = await TrxReportReader.ReadAsync(session.DirectoryPath, token);
            return TestResultMarkdown.Report(name, report, snapshot.ExitCode ?? -1);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return TestResultMarkdown.Error("Test runner error", $"{exception.Message}\n\nSee the test resource's console logs in Aspire.");
        }
    }

    private static ExecuteCommandResult WithArtifacts(ExecuteCommandResult result, string directory) =>
        TestResultMarkdown.Create(result.Success, result.Message!, result.Data!.Value +
            $"\n\n## Artifacts\n\nTRX reports are saved in:\n\n{TestResultMarkdown.Code(directory)}\n", result.Canceled);
}
