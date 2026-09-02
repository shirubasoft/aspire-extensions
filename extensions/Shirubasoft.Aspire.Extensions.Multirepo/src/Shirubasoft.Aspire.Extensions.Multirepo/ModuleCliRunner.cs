using System.Text;
using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal sealed record ModuleCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

internal static class ModuleCliRunner
{
    public static async Task<ModuleCliResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The command timeout must be positive.");
        }

        progress ??= line => Console.WriteLine($"[{operation}] {line}");
        var progressLock = new object();
        void ReportProgress(string line)
        {
            lock (progressLock)
            {
                progress(line);
            }
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            var result = await CliCommand.Wrap(executable)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(standardOutput),
                    PipeTarget.ToDelegate(ReportProgress)))
                .WithStandardErrorPipe(PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(standardError),
                    PipeTarget.ToDelegate(ReportProgress)))
                .ExecuteAsync(linkedSource.Token);

            return new ModuleCliResult(
                result.ExitCode,
                standardOutput.ToString(),
                standardError.ToString());
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{operation} exceeded the configured timeout of {timeout}.",
                exception);
        }
    }
}
