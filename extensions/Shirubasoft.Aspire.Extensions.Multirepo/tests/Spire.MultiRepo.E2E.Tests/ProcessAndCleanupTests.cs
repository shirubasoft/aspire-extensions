using System.Diagnostics;
using Spire.MultiRepo.E2E.Support;
using Xunit;
using SupportProgram = Spire.MultiRepo.E2E.Support.Program;

namespace Spire.MultiRepo.E2E.Tests;

[Collection(MultiRepositoryE2ECollection.Name)]
public sealed class ProcessAndCleanupTests
{
    [Fact]
    public async Task Process_executor_applies_its_bounded_timeout()
    {
        var executor = new SupportProgram.ProcessExecutor(TimeSpan.FromMilliseconds(100));
        var invocation = CreateLongRunningInvocation();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            executor.RunAsync(invocation, TestContext.Current.CancellationToken));

        Assert.Contains("exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_executor_propagates_caller_cancellation_independently()
    {
        var executor = new SupportProgram.ProcessExecutor(TimeSpan.FromMinutes(1));
        var invocation = CreateLongRunningInvocation();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.RunAsync(invocation, cancellation.Token));
    }

    [Fact]
    public async Task Process_executor_kills_the_process_tree_on_timeout()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"multi-repo-process-{Guid.NewGuid():N}.pid");
        var supportExecutable = GetSupportExecutable();
        var executor = new SupportProgram.ProcessExecutor(TimeSpan.FromSeconds(2));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => executor.RunAsync(
                new SupportProgram.ProcessInvocation(
                    supportExecutable,
                    ["--process-test-child", pidFile],
                    Directory.GetCurrentDirectory()),
                TestContext.Current.CancellationToken));

            Assert.True(File.Exists(pidFile), "The test grandchild did not report its process ID.");
            var processId = int.Parse(await File.ReadAllTextAsync(
                pidFile,
                TestContext.Current.CancellationToken));
            Assert.True(
                await WaitForExitAsync(processId, TestContext.Current.CancellationToken),
                $"Grandchild process {processId} survived cancellation.");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Cleanup_reports_the_failed_path()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"multi-repo-cleanup-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "not a directory", TestContext.Current.CancellationToken);
        var original = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        try
        {
            await SupportProgram.TryDeleteDirectoryAsync(path, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(original);
            File.Delete(path);
        }

        Assert.Contains("Cleanup warning", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(path, output.ToString(), StringComparison.Ordinal);
    }

    private static SupportProgram.ProcessInvocation CreateLongRunningInvocation()
    {
        if (OperatingSystem.IsWindows())
        {
            return new SupportProgram.ProcessInvocation(
                "cmd.exe",
                ["/d", "/s", "/c", "ping -n 30 127.0.0.1 > nul"],
                Directory.GetCurrentDirectory());
        }

        return new SupportProgram.ProcessInvocation(
            "/bin/sh",
            ["-c", "sleep 30"],
            Directory.GetCurrentDirectory());
    }

    private static string GetSupportExecutable() => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows()
            ? "Spire.MultiRepo.E2E.Support.exe"
            : "Spire.MultiRepo.E2E.Support");

    private static async Task<bool> WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }
}
