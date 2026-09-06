namespace Aspire.Hosting;

internal enum TestOutcome { Passed, Failed, Skipped, Error }

internal sealed record TestCaseResult(string Name, TestOutcome Outcome, string Duration, string Details);

internal sealed record TestReport(IReadOnlyList<TestCaseResult> Tests, bool Completed, bool HasRunErrors = false)
{
    internal int Passed => Tests.Count(test => test.Outcome == TestOutcome.Passed);
    internal int Failed => Tests.Count(test => test.Outcome is TestOutcome.Failed or TestOutcome.Error);
    internal int Skipped => Tests.Count(test => test.Outcome == TestOutcome.Skipped);
    internal bool IsComplete => Completed && !HasRunErrors;
    internal bool IsSuccessful => IsComplete && Tests.Count > 0 && Failed == 0;
}
