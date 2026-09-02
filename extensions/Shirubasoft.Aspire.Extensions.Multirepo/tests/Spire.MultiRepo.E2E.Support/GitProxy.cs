using System.Diagnostics;
using System.Text.Json;

namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    internal enum GitProxyPolicy
    {
        Initialize,
        ReadOnly,
        Refresh
    }

    internal sealed record GitProxyOperation(string Operation, string[] Arguments);

    internal static class GitProxy
    {
        public const string LogEnvironmentVariable = "MODULAR_E2E_GIT_PROXY_LOG";
        public const string PolicyEnvironmentVariable = "MODULAR_E2E_GIT_PROXY_POLICY";
        public const string RealGitEnvironmentVariable = "MODULAR_E2E_REAL_GIT";
        public const string RemoteRepositoryEnvironmentVariable = "MODULAR_E2E_REMOTE_REPOSITORY";
        public const string SourceRepositoryEnvironmentVariable = "MODULAR_E2E_SOURCE_REPOSITORY";

        public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
        {
            var operation = ReadOnlyGitCommandPolicy.FindOperation(args);
            await AppendOperationAsync(operation, args, cancellationToken).ConfigureAwait(false);
            var policy = Enum.TryParse<GitProxyPolicy>(
                Environment.GetEnvironmentVariable(PolicyEnvironmentVariable),
                ignoreCase: true,
                out var configuredPolicy)
                ? configuredPolicy
                : GitProxyPolicy.ReadOnly;
            if (policy == GitProxyPolicy.ReadOnly && !ReadOnlyGitCommandPolicy.IsAllowed(args))
            {
                await Console.Error.WriteLineAsync(
                    $"Git proxy denied unrecognized invocation: {string.Join(' ', args)}")
                    .ConfigureAwait(false);
                return 97;
            }

            var realGit = Environment.GetEnvironmentVariable(RealGitEnvironmentVariable) ?? "git";
            var remoteRepository = Environment.GetEnvironmentVariable(RemoteRepositoryEnvironmentVariable);
            var sourceRepository = Environment.GetEnvironmentVariable(SourceRepositoryEnvironmentVariable);
            if (operation == "clone" &&
                !string.IsNullOrWhiteSpace(remoteRepository) &&
                !string.IsNullOrWhiteSpace(sourceRepository) &&
                args.Contains(remoteRepository, StringComparer.Ordinal))
            {
                await Console.Out.WriteLineAsync($"Cloning {remoteRepository}").ConfigureAwait(false);
                var rewritten = args
                    .Select(argument => string.Equals(argument, remoteRepository, StringComparison.Ordinal)
                        ? sourceRepository
                        : argument)
                    .ToArray();
                var exitCode = await ForwardAsync(realGit, rewritten, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    return exitCode;
                }

                var destination = args[^1];
                return await RunSilentAsync(
                    realGit,
                    ["-C", destination, "remote", "set-url", "origin", remoteRepository],
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation is "fetch" or "pull" &&
                !string.IsNullOrWhiteSpace(remoteRepository) &&
                !string.IsNullOrWhiteSpace(sourceRepository) &&
                FindWorkingDirectory(args) is { } repositoryPath)
            {
                var configuredOrigin = await CaptureAsync(
                    realGit,
                    ["-C", repositoryPath, "config", "--get", "remote.origin.url"],
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(
                    configuredOrigin.Output.Trim(),
                    remoteRepository,
                    StringComparison.Ordinal))
                {
                    var setLocalExitCode = await RunSilentAsync(
                        realGit,
                        ["-C", repositoryPath, "remote", "set-url", "origin", sourceRepository],
                        cancellationToken).ConfigureAwait(false);
                    if (setLocalExitCode != 0)
                    {
                        return setLocalExitCode;
                    }

                    try
                    {
                        return await ForwardAsync(realGit, args, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await RunSilentAsync(
                            realGit,
                            ["-C", repositoryPath, "remote", "set-url", "origin", remoteRepository],
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            return await ForwardAsync(realGit, args, cancellationToken).ConfigureAwait(false);
        }

        private static string? FindWorkingDirectory(IReadOnlyList<string> args)
        {
            for (var index = 0; index + 1 < args.Count; index++)
            {
                if (string.Equals(args[index], "-C", StringComparison.Ordinal))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static async Task AppendOperationAsync(
            string operation,
            string[] args,
            CancellationToken cancellationToken)
        {
            var logPath = Environment.GetEnvironmentVariable(LogEnvironmentVariable)
                ?? throw new InvalidOperationException($"{LogEnvironmentVariable} is not configured.");
            var line = JsonSerializer.Serialize(new GitProxyOperation(operation, args)) +
                Environment.NewLine;
            await File.AppendAllTextAsync(logPath, line, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> ForwardAsync(
            string executable,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(executable, args, redirectOutput: false);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start '{executable}'.");
            await WaitForExitAndKillAsync(process, cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        private static async Task<int> RunSilentAsync(
            string executable,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            var result = await CaptureAsync(executable, args, cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }

        private static async Task<(int ExitCode, string Output)> CaptureAsync(
            string executable,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(executable, args, redirectOutput: true);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start '{executable}'.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await WaitForExitAndKillAsync(process, cancellationToken).ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }

        private static ProcessStartInfo CreateStartInfo(
            string executable,
            IReadOnlyList<string> args,
            bool redirectOutput)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = redirectOutput
            };
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment.Remove(LogEnvironmentVariable);
            startInfo.Environment.Remove(PolicyEnvironmentVariable);
            startInfo.Environment.Remove(RemoteRepositoryEnvironmentVariable);
            startInfo.Environment.Remove(SourceRepositoryEnvironmentVariable);
            return startInfo;
        }
    }
}
