using System.Xml;
using System.Xml.Linq;

namespace Aspire.Hosting;

internal static class TrxReportReader
{
    private static readonly HashSet<string> CompletedOutcomes = ["Completed", "Passed", "Failed"];
    private static readonly Dictionary<string, TestOutcome> Outcomes = new(StringComparer.Ordinal)
    {
        ["Passed"] = TestOutcome.Passed,
        ["Failed"] = TestOutcome.Failed,
        ["NotExecuted"] = TestOutcome.Skipped,
    };

    internal static async Task<TestReport> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        var paths = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        if (paths.Length == 0)
        {
            throw new InvalidDataException("The runner did not produce a TRX report. Check the runner configuration and TRX reporting extension.");
        }

        var reports = new List<TestReport>();
        foreach (var path in paths)
        {
            await using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new() { Async = true, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32 * 1024 * 1024 });
            reports.Add(Parse(await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken)));
        }

        return new(reports.SelectMany(report => report.Tests).ToArray(), reports.All(report => report.Completed), reports.Any(report => report.HasRunErrors));
    }

    internal static TestReport Parse(XDocument document)
    {
        var root = RequireTestRun(document);
        var ns = root.Name.Namespace;
        var summary = Child(root, "ResultSummary");
        var completed = CompletedOutcomes.Contains(Attribute(summary, "outcome"));
        var tests = Child(root, "Results").Descendants(ns + "UnitTestResult").Select(ParseTest).ToArray();
        return new(tests, completed && CountsMatch(summary, tests.Length), Attribute(summary, "outcome") == "Failed");
    }

    private static XElement RequireTestRun(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("The TRX document is empty.");
        if (root.Name.LocalName != "TestRun")
        {
            throw new InvalidDataException("Expected a TRX TestRun document.");
        }

        return root;
    }

    private static TestCaseResult ParseTest(XElement test)
    {
        var error = Child(Child(test, "Output"), "ErrorInfo");
        return new(
            TestName(test),
            Outcomes.GetValueOrDefault(Attribute(test, "outcome"), TestOutcome.Error),
            Attribute(test, "duration"),
            string.Join('\n', error.Elements().Select(element => element.Value)));
    }

    private static XElement Child(XElement parent, string name) =>
        parent.Element(parent.Name.Namespace + name) ?? new XElement(parent.Name.Namespace + name);

    private static string Attribute(XElement element, string name) => (string?)element.Attribute(name) ?? "";
    private static string TestName(XElement element) => (string?)element.Attribute("testName") ?? "Unnamed test";

    private static bool CountsMatch(XElement summary, int count)
    {
        var total = Attribute(Child(summary, "Counters"), "total");
        if (total.Length == 0)
        {
            return true;
        }
        return int.TryParse(total, out var expected) && expected == count;
    }
}
