using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using CliWrapLib = CliWrap;

namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Default implementation of <see cref="IContainerImageService"/> using CliWrap to execute Docker commands.
/// </summary>
/// <remarks>
/// <para>
/// This service provides container image operations by executing Docker CLI commands.
/// It uses CliWrap for process management, ensuring consistent behavior across platforms.
/// </para>
/// <para>
/// Timeouts are configured as follows:
/// <list type="bullet">
///   <item>Quick operations (inspect, tag, info): 30 seconds</item>
///   <item>Build operations: 10 minutes</item>
/// </list>
/// </para>
/// </remarks>
public class ContainerImageService : IContainerImageService
{
    private readonly ILogger<ContainerImageService> _logger;

    /// <summary>
    /// Default timeout for build operations (10 minutes).
    /// </summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Default timeout for quick operations like inspect and tag (30 seconds).
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerImageService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    public ContainerImageService(ILogger<ContainerImageService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> ImageExistsLocallyAsync(
        string imageName,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageName);
        ArgumentNullException.ThrowIfNull(tag);

        var fullImageName = $"{imageName}:{tag}";
        _logger.LogDebug("Checking if image exists locally: {ImageName}", fullImageName);

        try
        {
            var result = await ExecuteDockerCommandAsync(
                $"image inspect {fullImageName}",
                timeout: CommandTimeout,
                cancellationToken: cancellationToken);

            var exists = result.ExitCode == 0;
            _logger.LogDebug("Image {ImageName} exists: {Exists}", fullImageName, exists);

            return exists;
        }
        catch (ContainerOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task BuildImageAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        _logger.LogInformation("Building container image...");
        _logger.LogDebug("Build command: {Command}", command);
        _logger.LogDebug("Working directory: {WorkingDirectory}", workingDirectory);

        var outputBuilder = new StringBuilder();

        try
        {
            // Parse command into executable and arguments
            var (executable, arguments) = ParseCommand(command);

            var result = await CliWrapLib.Cli.Wrap(executable)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithStandardOutputPipe(CliWrapLib.PipeTarget.ToDelegate(line =>
                {
                    _logger.LogDebug("[build] {Line}", line);
                    outputBuilder.AppendLine(line);
                }))
                .WithStandardErrorPipe(CliWrapLib.PipeTarget.ToDelegate(line =>
                {
                    _logger.LogDebug("[build:err] {Line}", line);
                    outputBuilder.AppendLine(line);
                }))
                .WithValidation(CliWrapLib.CommandResultValidation.None)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new ContainerBuildException(
                    message: $"Container build failed with exit code {result.ExitCode}",
                    command: command,
                    exitCode: result.ExitCode,
                    standardError: string.Empty,
                    workingDirectory: workingDirectory,
                    buildOutput: outputBuilder.ToString());
            }

            _logger.LogInformation("Container image built successfully");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogError("Build command executable not found: {Command}", command);
            throw ContainerOperationException.DockerNotAvailable();
        }
    }

    /// <inheritdoc />
    public async Task TagImageAsync(
        string sourceImage,
        string targetImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentNullException.ThrowIfNull(targetImage);

        _logger.LogDebug("Tagging image {Source} as {Target}", sourceImage, targetImage);

        var result = await ExecuteDockerCommandAsync(
            $"tag {sourceImage} {targetImage}",
            timeout: CommandTimeout,
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new ContainerOperationException(
                message: $"Failed to tag image: {result.StandardError}",
                command: $"docker tag {sourceImage} {targetImage}",
                exitCode: result.ExitCode,
                standardError: result.StandardError);
        }

        _logger.LogDebug("Successfully tagged {Source} as {Target}", sourceImage, targetImage);
    }

    /// <inheritdoc />
    public async Task<bool> IsDockerAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExecuteDockerCommandAsync(
                "info",
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: cancellationToken);

            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ContainerImageInfo?> GetImageInfoAsync(
        string imageName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageName);

        try
        {
            var format = "{{.Id}}\t{{.Created}}\t{{.Size}}";
            var result = await ExecuteDockerCommandAsync(
                $"image inspect --format \"{format}\" {imageName}",
                timeout: CommandTimeout,
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0)
            {
                return null;
            }

            var parts = result.StandardOutput.Trim().Split('\t');
            if (parts.Length < 3)
            {
                return null;
            }

            return new ContainerImageInfo
            {
                Id = parts[0],
                FullName = imageName,
                Created = DateTimeOffset.Parse(parts[1]),
                Size = long.Parse(parts[2])
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executes a Docker command with the specified arguments.
    /// </summary>
    /// <param name="arguments">The arguments to pass to the docker command.</param>
    /// <param name="timeout">The timeout for the command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the command execution.</returns>
    /// <exception cref="ContainerOperationException">
    /// Thrown when Docker is not available (executable not found).
    /// </exception>
    private async Task<DockerCommandResult> ExecuteDockerCommandAsync(
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            var result = await CliWrapLib.Cli.Wrap("docker")
                .WithArguments(arguments)
                .WithStandardOutputPipe(CliWrapLib.PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithStandardErrorPipe(CliWrapLib.PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CliWrapLib.CommandResultValidation.None)
                .ExecuteAsync(linkedCts.Token)
                .ConfigureAwait(false);

            return new DockerCommandResult
            {
                ExitCode = result.ExitCode,
                StandardOutput = stdOutBuffer.ToString(),
                StandardError = stdErrBuffer.ToString()
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogError("Docker executable not found");
            throw ContainerOperationException.DockerNotAvailable();
        }
    }

    /// <summary>
    /// Parses a command string into executable and arguments components.
    /// </summary>
    /// <param name="command">The full command string to parse.</param>
    /// <returns>A tuple containing the executable name and the remaining arguments.</returns>
    /// <remarks>
    /// The command is split on the first space character. If no space is found,
    /// the entire command is treated as the executable with no arguments.
    /// </remarks>
    /// <example>
    /// <code>
    /// var (exe, args) = ParseCommand("dotnet publish --os linux");
    /// // exe = "dotnet", args = "publish --os linux"
    /// </code>
    /// </example>
    internal static (string executable, string arguments) ParseCommand(string command)
    {
        var trimmed = command.Trim();
        var spaceIndex = trimmed.IndexOf(' ');

        if (spaceIndex == -1)
        {
            return (trimmed, string.Empty);
        }

        return (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..]);
    }

    /// <summary>
    /// Represents the result of a Docker command execution.
    /// </summary>
    private sealed record DockerCommandResult
    {
        /// <summary>
        /// Gets the exit code from the Docker command.
        /// </summary>
        public int ExitCode { get; init; }

        /// <summary>
        /// Gets the standard output from the Docker command.
        /// </summary>
        public string StandardOutput { get; init; } = string.Empty;

        /// <summary>
        /// Gets the standard error from the Docker command.
        /// </summary>
        public string StandardError { get; init; } = string.Empty;
    }
}
