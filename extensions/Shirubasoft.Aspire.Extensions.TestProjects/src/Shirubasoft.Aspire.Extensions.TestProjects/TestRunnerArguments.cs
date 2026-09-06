namespace Aspire.Hosting;

internal static class TestRunnerArguments
{
    internal static string[] Create(string project, TestProjectOptions options, string directory, string? filter, IEnumerable<string> additional)
    {
        if (options.Runner == TestProjectRunner.MicrosoftTestingPlatform)
        {
            return ["run", "--project", project, "--configuration", options.Configuration, "--no-launch-profile", "--",
                .. additional, "--report-trx", "--report-trx-filename", "results.trx", "--results-directory", directory];
        }
        return ["test", project, "--configuration", options.Configuration,
            .. additional, "--logger", "trx;LogFilePrefix=results", "--results-directory", directory, .. Filter(filter)];
    }

    private static string[] Filter(string? filter) => string.IsNullOrWhiteSpace(filter) ? [] : ["--filter", filter];
}
