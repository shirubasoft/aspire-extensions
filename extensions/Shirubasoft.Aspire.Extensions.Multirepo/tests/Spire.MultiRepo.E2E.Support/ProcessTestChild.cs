using System.Diagnostics;

namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    internal static class ProcessTestChild
    {
        private const string ChildOption = "--process-test-child";
        private const string GrandchildOption = "--process-test-grandchild";

        public static bool IsInvocation(IReadOnlyList<string> args) =>
            args.Count > 0 && args[0] is ChildOption or GrandchildOption;

        public static async Task<int> RunAsync(IReadOnlyList<string> args)
        {
            if (args.Count != 2)
            {
                return 2;
            }

            if (args[0] == GrandchildOption)
            {
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString()).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                return 0;
            }

            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The process-test child executable is unavailable.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(GrandchildOption);
            startInfo.ArgumentList.Add(args[1]);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start the process-test grandchild.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
