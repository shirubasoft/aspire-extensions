using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class DiagnosticsOperationTests
{
    [Theory]
    [InlineData(TestDiagnosticsExportMode.OnFailure, false)]
    [InlineData(TestDiagnosticsExportMode.Always, true)]
    public async Task SuccessfulOperationsHonorExportMode(TestDiagnosticsExportMode mode, bool shouldExport)
    {
        var expected = new TestDiagnosticsExport("result", []);
        var result = await DiagnosticsOperation.RunAsync(_ => Task.CompletedTask,
            _ => Task.FromResult(expected), new() { ExportMode = mode }, TestContext.Current.CancellationToken);
        Assert.Equal(shouldExport, ReferenceEquals(expected, result));
    }

    [Fact]
    public async Task FailurePreservesExceptionAndExportsWithAnIndependentToken()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var original = new OperationCanceledException(canceled.Token);
        var result = new TestDiagnosticsExport("result", ["partial telemetry"]);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => DiagnosticsOperation.RunAsync(
            token =>
            {
                Assert.Equal(canceled.Token, token);
                throw original;
            },
            token =>
            {
                Assert.False(token.IsCancellationRequested);
                return Task.FromResult(result);
            }, new(), canceled.Token));
        Assert.Same(original, actual);
        Assert.Same(result, actual.Data["Aspire.TestDiagnostics"]);
    }

    [Fact]
    public async Task ExportFailureDoesNotReplaceTheTestFailure()
    {
        var original = new InvalidOperationException("test failed");
        var exportFailure = new IOException("disk full");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticsOperation.RunAsync(
            _ => throw original, _ => throw exportFailure, new(), TestContext.Current.CancellationToken));
        Assert.Same(original, actual);
        Assert.Same(exportFailure, actual.Data["Aspire.TestDiagnostics.ExportError"]);
    }

    [Fact]
    public async Task ExportTimeoutDoesNotReplaceTheTestFailure()
    {
        var original = new InvalidOperationException("test failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticsOperation.RunAsync(
            _ => throw original, async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new TestDiagnosticsExport("unreachable", []);
            }, new() { ExportTimeout = TimeSpan.FromMilliseconds(50) }, TestContext.Current.CancellationToken));
        Assert.Same(original, actual);
        Assert.IsAssignableFrom<OperationCanceledException>(actual.Data["Aspire.TestDiagnostics.ExportError"]);
    }

    [Fact]
    public async Task AlwaysExportReportsInfrastructureFailureToSuccessfulTest()
    {
        await Assert.ThrowsAsync<IOException>(() => DiagnosticsOperation.RunAsync(
            _ => Task.CompletedTask, _ => throw new IOException("disk full"),
            new() { ExportMode = TestDiagnosticsExportMode.Always }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OptionsValidateDefaultsAndRejectInvalidValues()
    {
        var options = new TestDiagnosticsOptions();
        options.Validate();
        Assert.Equal(TestDiagnosticsExportMode.OnFailure, options.ExportMode);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ExportTimeout);
        Assert.False(string.IsNullOrWhiteSpace(options.OutputDirectory));
        Assert.Throws<ArgumentException>(() => new TestDiagnosticsOptions { OutputDirectory = " " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestDiagnosticsOptions { ExportMode = (TestDiagnosticsExportMode)99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestDiagnosticsOptions { ExportTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestDiagnosticsOptions { ExportTimeout = TimeSpan.MaxValue }.Validate());
    }
}
