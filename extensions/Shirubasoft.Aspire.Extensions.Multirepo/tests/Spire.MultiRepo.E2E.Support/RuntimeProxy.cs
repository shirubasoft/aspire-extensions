using System.Diagnostics;
using System.Text.Json;

namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    private sealed record RuntimeProxyOperation(
        string Runtime,
        string RealExecutable,
        string[] Arguments);

    private static class RuntimeProxy
    {
        public const string LogDirectoryEnvironmentVariable = "MODULAR_E2E_RUNTIME_PROXY_LOG_DIRECTORY";
        public const string RealExecutableEnvironmentVariable = "MODULAR_E2E_REAL_CONTAINER_RUNTIME";
        public const string RuntimeEnvironmentVariable = "MODULAR_E2E_CONTAINER_RUNTIME";

        public static bool IsInvocation()
        {
            var runtime = Environment.GetEnvironmentVariable(RuntimeEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(runtime) &&
                string.Equals(
                    Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                    runtime,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
        {
            var runtime = Environment.GetEnvironmentVariable(RuntimeEnvironmentVariable)
                ?? throw new InvalidOperationException($"{RuntimeEnvironmentVariable} is not configured.");
            var realExecutable = Environment.GetEnvironmentVariable(RealExecutableEnvironmentVariable)
                ?? throw new InvalidOperationException($"{RealExecutableEnvironmentVariable} is not configured.");
            var logDirectory = Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable)
                ?? throw new InvalidOperationException($"{LogDirectoryEnvironmentVariable} is not configured.");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(
                logDirectory,
                $"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(
                logPath,
                JsonSerializer.Serialize(new RuntimeProxyOperation(runtime, realExecutable, args)),
                cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo(realExecutable)
            {
                UseShellExecute = false
            };
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment.Remove(LogDirectoryEnvironmentVariable);
            startInfo.Environment.Remove(RealExecutableEnvironmentVariable);
            startInfo.Environment.Remove(RuntimeEnvironmentVariable);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start '{realExecutable}'.");
            await WaitForExitAndKillAsync(process, cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
