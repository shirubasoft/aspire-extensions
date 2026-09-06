#pragma warning disable ASPIREPROCESSCOMMAND001 // Results from the pinned Aspire process runner.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class TestRunArtifacts
{
    internal static async Task<ExecuteCommandResult> ReadResultAsync(ProcessCommandResultContext context, string directory)
    {
        var output = context.GetFormattedOutput(200);
        await File.WriteAllTextAsync(Path.Combine(directory, "runner-output.txt"), output, context.CancellationToken);
        try
        {
            var report = await TrxReportReader.ReadAsync(directory, context.CancellationToken);
            var result = TestResultMarkdown.Report(context.ResourceName, report, context.ExitCode);
            return WithArtifacts(result, directory);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return WithArtifacts(TestResultMarkdown.Error("Test runner error", $"{exception.Message}\n\nRunner exit code: {context.ExitCode}\n\n{output}"), directory);
        }
    }

    private static ExecuteCommandResult WithArtifacts(ExecuteCommandResult result, string directory) =>
        TestResultMarkdown.Create(result.Success, result.Message!, result.Data!.Value +
            $"\n\n## Artifacts\n\nTRX reports and the runner output are saved in:\n\n{TestResultMarkdown.Code(directory)}\n");
}
