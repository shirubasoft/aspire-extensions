using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class ReportTests
{
    [Fact]
    public void MixedResultsRenderColoredLabelsCountsAndFailureDetails()
    {
        var report = TrxReportReader.Parse(Document("Passed", "Failed", "NotExecuted", "Timeout"));
        var result = TestResultMarkdown.Report("suite", report, 1);
        Assert.False(result.Success);
        Assert.Equal(CommandResultFormat.Markdown, result.Data!.Format);
        Assert.True(result.Data.DisplayImmediately);
        Assert.Contains("| 1 | 2 | 1 | 4 |", result.Data.Value);
        Assert.Contains("🟢 Passed", result.Data.Value);
        Assert.Contains("🔴 Failed", result.Data.Value);
        Assert.Contains("🟡 Skipped", result.Data.Value);
        Assert.Contains("🔴 Error", result.Data.Value);
        Assert.Contains("    Expected true\n    at Test()", result.Data.Value);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void RunnerExitCodeIsPartOfTheOutcome(int exitCode, bool success)
    {
        var result = TestResultMarkdown.Report("suite", TrxReportReader.Parse(Document("Passed", "NotExecuted")), exitCode);
        Assert.Equal(success, result.Success);
    }

    [Fact]
    public void ZeroTestsAndIncompleteRunsCannotPass()
    {
        Assert.False(TestResultMarkdown.Report("empty", new([], true), 0).Success);
        var report = TrxReportReader.Parse(XDocument.Parse("<TestRun><Results><UnitTestResult outcome='Passed'/></Results></TestRun>"));
        var result = TestResultMarkdown.Report("partial", report, 0);
        Assert.False(result.Success);
        Assert.Contains("partial", result.Data!.Value);
        Assert.Contains("Unnamed test", result.Data.Value);
    }

    [Theory]
    [InlineData("Failed", "1")]
    [InlineData("Completed", "2")]
    [InlineData("Completed", "invalid")]
    public void SummaryFailuresAndMissingTestResultsCannotPass(string outcome, string total)
    {
        var document = XDocument.Parse($"<TestRun><Results><UnitTestResult outcome='Passed'/></Results><ResultSummary outcome='{outcome}'><Counters total='{total}'/></ResultSummary></TestRun>");
        Assert.False(TestResultMarkdown.Report("suite", TrxReportReader.Parse(document), 0).Success);
    }

    [Fact]
    public void TestNamesAndFailureContentCannotInjectMarkdownOrHtml()
    {
        var report = new TestReport([new("a|<script>_[link](url)\n# heading`", TestOutcome.Failed, "", "```\n<script>alert(1)</script>\n# heading")], true);
        var text = TestResultMarkdown.Report("suite", report, 1).Data!.Value;
        Assert.Contains("a\\|&lt;script&gt;\\_\\[link\\]", text);
        Assert.Contains("    ```\n    <script>", text);
        Assert.DoesNotContain("\n<script>", text);
    }

    [Fact]
    public void LargeReportsAreBoundedWithFailuresFirst()
    {
        var tests = Enumerable.Range(0, 250).Select(index => new TestCaseResult($"test{index}", TestOutcome.Passed, "1", "")).ToList();
        tests.Add(new("last-failure", TestOutcome.Failed, "2", new string('x', 9000)));
        var text = TestResultMarkdown.Report("large", new(tests, true), 1).Data!.Value;
        Assert.Contains("Showing 200 tests", text);
        Assert.Contains("truncated", text);
        Assert.True(text.IndexOf("last-failure", StringComparison.Ordinal) < text.IndexOf("test0", StringComparison.Ordinal));
        Assert.DoesNotContain("test249", text);
    }

    [Fact]
    public async Task ReadsMultipleReportsAndRejectsMalformedOrMissingArtifacts()
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => TrxReportReader.ReadAsync(directory.Path, TestContext.Current.CancellationToken));
        Document("Passed").Save(System.IO.Path.Combine(directory.Path, "first.trx"));
        Document("NotExecuted").Save(System.IO.Path.Combine(directory.Path, "second.trx"));
        var report = await TrxReportReader.ReadAsync(directory.Path, TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Tests.Count);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "malformed.trx"), "<!DOCTYPE TestRun [<!ENTITY x 'bad'>]><TestRun>&x;</TestRun>", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<XmlException>(() => TrxReportReader.ReadAsync(directory.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RejectsNonTrxAndHandlesMissingFields()
    {
        Assert.Throws<InvalidDataException>(() => TrxReportReader.Parse(new()));
        Assert.Throws<InvalidDataException>(() => TrxReportReader.Parse(XDocument.Parse("<Report/>")));
        Assert.Empty(TrxReportReader.Parse(XDocument.Parse("<TestRun/>")).Tests);
        Assert.Equal(TestOutcome.Error, TrxReportReader.Parse(XDocument.Parse("<TestRun><Results><UnitTestResult/></Results></TestRun>")).Tests[0].Outcome);
    }

    internal static XDocument Document(params string[] outcomes)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return new(new XElement(ns + "TestRun",
            new XElement(ns + "Results", outcomes.Select((outcome, index) => new XElement(ns + "UnitTestResult",
                new XAttribute("testName", $"test-{index}"), new XAttribute("outcome", outcome), new XAttribute("duration", "00:00:00.010"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", "Expected true"), new XElement(ns + "StackTrace", "at Test()")))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"))));
    }
}

internal sealed class TestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspire-test-project-tests", Guid.NewGuid().ToString("N"));
    internal TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
