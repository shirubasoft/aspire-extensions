using System.Text;
using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool;

internal enum ProcessOutputMode
{
    Stream,
    Capture
}

internal sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    ProcessOutputMode OutputMode = ProcessOutputMode.Stream,
    string? StandardInput = null);

internal sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

internal interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken);
}

internal sealed class CliWrapProcessRunner(
    Stream input,
    TextWriter output,
    TextWriter error) : IProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var command = CliCommand.Wrap(invocation.FileName)
            .WithArguments(invocation.Arguments)
            .WithWorkingDirectory(invocation.WorkingDirectory)
            .WithValidation(CommandResultValidation.None);
        command = ConfigureOutput(command, invocation.OutputMode, standardOutput, standardError);
        command = ConfigureEnvironment(command, invocation.EnvironmentVariables);
        command = command.WithStandardInputPipe(CreateStandardInput(invocation.StandardInput));

        var result = await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessExecutionResult(
            result.ExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private Command ConfigureOutput(
        Command command,
        ProcessOutputMode outputMode,
        StringBuilder standardOutput,
        StringBuilder standardError) =>
        outputMode switch
        {
            ProcessOutputMode.Capture => command
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(standardOutput))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError)),
            ProcessOutputMode.Stream => command
                .WithStandardOutputPipe(PipeTarget.ToDelegate(output.WriteLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(error.WriteLine)),
            _ => throw new ArgumentOutOfRangeException(nameof(outputMode))
        };

    private static Command ConfigureEnvironment(
        Command command,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (environmentVariables is not null)
        {
            return command.WithEnvironmentVariables(environmentVariables);
        }

        return command;
    }

    private PipeSource CreateStandardInput(string? standardInput) =>
        standardInput is null ? PipeSource.FromStream(input) : PipeSource.FromString(standardInput);
}
