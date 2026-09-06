using System.Net;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class TestResultMarkdown
{
    internal static ExecuteCommandResult Report(string name, TestReport report, int exitCode)
    {
        var success = exitCode == 0 && report.IsSuccessful;
        var title = success ? "Tests passed" : "Test run failed";
        var text = new StringBuilder($"# {Icon(success)} {title}\n\n{Escape(name)}\n\n");
        text.AppendLine("| 🟢 Passed | 🔴 Failed / errors | 🟡 Skipped | Total |");
        text.AppendLine("| ---: | ---: | ---: | ---: |");
        text.AppendLine($"| {report.Passed} | {report.Failed} | {report.Skipped} | {report.Tests.Count} |\n");
        AppendRunStatus(text, report, exitCode);
        AppendSummaryErrors(text, report);
        AppendTests(text, report.Tests);
        AppendFailures(text, report.Tests);
        return Create(success, title, text.ToString());
    }

    private static void AppendSummaryErrors(StringBuilder text, TestReport report)
    {
        if (report.HasRunErrors)
        {
            text.AppendLine("🔴 The runner marked the run summary as failed.\n");
        }
    }

    private static void AppendRunStatus(StringBuilder text, TestReport report, int exitCode)
    {
        text.AppendLine($"Runner exit code: **{exitCode}**\n");
        if (!report.Completed)
        {
            text.AppendLine("🔴 The runner did not complete the test run. Results may be partial.\n");
        }
        if (report.Tests.Count == 0)
        {
            text.AppendLine("🟡 No tests were reported. This run is not considered successful.\n");
        }
    }

    private static void AppendTests(StringBuilder text, IReadOnlyList<TestCaseResult> tests)
    {
        text.AppendLine("## Tests\n\n| Result | Test | Duration |\n| --- | --- | ---: |");
        foreach (var test in tests.OrderBy(test => Order(test.Outcome)).Take(200))
        {
            text.AppendLine($"| {Label(test.Outcome)} | {Escape(test.Name)} | {Escape(test.Duration)} |");
        }
        if (tests.Count > 200)
        {
            text.AppendLine("\nShowing 200 tests. The TRX artifacts contain the complete results.");
        }
    }

    private static void AppendFailures(StringBuilder text, IReadOnlyList<TestCaseResult> tests)
    {
        foreach (var test in tests.Where(test => test.Outcome is TestOutcome.Failed or TestOutcome.Error).Take(20))
        {
            text.AppendLine($"\n### 🔴 {Escape(test.Name)}\n");
            text.AppendLine(Code(test.Details));
        }
    }

    internal static ExecuteCommandResult Error(string title, string details, bool canceled = false) =>
        Create(false, title, $"# {(canceled ? "🟡" : "🔴")} {Escape(title)}\n\n{Code(details)}", canceled);

    internal static ExecuteCommandResult Create(bool success, string title, string markdown, bool canceled = false) => new()
    {
        Success = success,
        Canceled = canceled,
        Message = title,
        Data = new() { Value = markdown, Format = CommandResultFormat.Markdown, DisplayImmediately = true },
    };

    internal static string Escape(string value)
    {
        var encoded = WebUtility.HtmlEncode(value).Replace("\r", "").Replace("\n", " ");
        foreach (var character in new[] { "\\", "`", "*", "_", "[", "]", "|", "#" })
        {
            encoded = encoded.Replace(character, "\\" + character, StringComparison.Ordinal);
        }
        return encoded;
    }

    internal static string Code(string value) => string.Join('\n', Limit(value).Replace("\r", "").Split('\n').Select(line => "    " + line));
    private static string Limit(string value) => value.Length > 8000 ? value[..8000] + "\n… truncated; see the TRX report." : value;
    private static string Icon(bool success) => success ? "🟢" : "🔴";
    private static int Order(TestOutcome outcome) => outcome switch { TestOutcome.Passed => 2, TestOutcome.Skipped => 1, _ => 0 };
    private static string Label(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "🟢 Passed",
        TestOutcome.Skipped => "🟡 Skipped",
        TestOutcome.Failed => "🔴 Failed",
        _ => "🔴 Error",
    };
}
