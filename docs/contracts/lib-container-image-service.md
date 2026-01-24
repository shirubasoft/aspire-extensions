# Contract: IContainerImageService

## Overview

This contract defines the interface and implementation for container image operations including existence checks, building, and tagging.

## Namespace

```csharp
namespace Aspire.Hosting.SharedResources;
```

## Dependencies

- `CliWrap` (3.10.0+) - For CLI process execution
- `Microsoft.Extensions.Logging` (for ILogger)

---

## IContainerImageService Interface

### Description

Service for container image operations using the Docker CLI.

### Interface Definition

```csharp
/// <summary>
/// Service for container image operations.
/// </summary>
/// <remarks>
/// Implementations use the Docker CLI via CliWrap for consistent behavior
/// and to avoid SDK version dependencies.
/// </remarks>
public interface IContainerImageService
{
    /// <summary>
    /// Checks if an image with the specified name and tag exists locally.
    /// </summary>
    /// <param name="imageName">Container image name (without tag).</param>
    /// <param name="tag">Image tag to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the image exists locally; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when imageName or tag is null.
    /// </exception>
    /// <exception cref="ContainerOperationException">
    /// Thrown when Docker is not available or an error occurs.
    /// </exception>
    /// <example>
    /// <code>
    /// var exists = await containerService.ImageExistsLocallyAsync("my-api", "abc1234");
    /// if (!exists)
    /// {
    ///     await containerService.BuildImageAsync(...);
    /// }
    /// </code>
    /// </example>
    Task<bool> ImageExistsLocallyAsync(
        string imageName,
        string tag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a container image using the specified command.
    /// </summary>
    /// <param name="command">Full build command to execute.</param>
    /// <param name="workingDirectory">Working directory for the command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when command or workingDirectory is null.
    /// </exception>
    /// <exception cref="ContainerBuildException">
    /// Thrown when the build command fails.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled.
    /// </exception>
    /// <remarks>
    /// The command is executed as-is using the shell. Ensure all placeholders
    /// are substituted before calling this method.
    /// Build output is streamed to the logger in real-time.
    /// </remarks>
    /// <example>
    /// <code>
    /// var command = "dotnet publish /path/to/project.csproj --os linux /t:PublishContainer -p:ContainerRepository=my-api -p:ContainerImageTag=abc1234";
    /// await containerService.BuildImageAsync(command, "/path/to/repo", cancellationToken);
    /// </code>
    /// </example>
    Task BuildImageAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tags an existing image with a new tag.
    /// </summary>
    /// <param name="sourceImage">Source image in "name:tag" format.</param>
    /// <param name="targetImage">Target image in "name:tag" format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when sourceImage or targetImage is null.
    /// </exception>
    /// <exception cref="ContainerOperationException">
    /// Thrown when the tag operation fails or source image doesn't exist.
    /// </exception>
    /// <example>
    /// <code>
    /// await containerService.TagImageAsync("my-api:abc1234", "my-api:latest", cancellationToken);
    /// </code>
    /// </example>
    Task TagImageAsync(
        string sourceImage,
        string targetImage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that Docker is available and running.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if Docker is available and responding; otherwise, false.</returns>
    Task<bool> IsDockerAvailableAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about a local image.
    /// </summary>
    /// <param name="imageName">Image name with tag (e.g., "my-api:abc1234").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image information or null if the image doesn't exist.</returns>
    Task<ContainerImageInfo?> GetImageInfoAsync(
        string imageName,
        CancellationToken cancellationToken = default);
}
```

---

## ContainerImageInfo

### Description

Information about a container image.

### Class Definition

```csharp
/// <summary>
/// Information about a container image.
/// </summary>
public class ContainerImageInfo
{
    /// <summary>
    /// Full image ID (SHA256 hash).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Image name and tag (e.g., "my-api:abc1234").
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Creation timestamp of the image.
    /// </summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>
    /// Image size in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Image size formatted for display (e.g., "125 MB").
    /// </summary>
    public string FormattedSize => FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
```

---

## ContainerOperationException

### Description

Base exception for container operation failures.

### Class Definition

```csharp
/// <summary>
/// Exception thrown when a container operation fails.
/// </summary>
[Serializable]
public class ContainerOperationException : Exception
{
    /// <summary>
    /// The Docker command that was executed.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// The exit code from the Docker command.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// The standard error output from Docker.
    /// </summary>
    public string StandardError { get; }

    /// <summary>
    /// Initializes a new instance of the ContainerOperationException class.
    /// </summary>
    public ContainerOperationException(
        string command,
        int exitCode,
        string standardError,
        string message)
        : base(message)
    {
        Command = command;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Creates an exception for when Docker is not installed or not running.
    /// </summary>
    public static ContainerOperationException DockerNotAvailable()
    {
        return new ContainerOperationException(
            command: "docker info",
            exitCode: -1,
            standardError: "Cannot connect to Docker daemon",
            message: "Docker is not available. Please ensure Docker is installed and the daemon is running.");
    }
}
```

---

## ContainerBuildException

### Description

Exception thrown when a container image build fails.

### Class Definition

```csharp
/// <summary>
/// Exception thrown when a container image build fails.
/// </summary>
[Serializable]
public class ContainerBuildException : ContainerOperationException
{
    /// <summary>
    /// The working directory where the build was executed.
    /// </summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// The full build output (stdout + stderr).
    /// </summary>
    public string BuildOutput { get; }

    /// <summary>
    /// Initializes a new instance of the ContainerBuildException class.
    /// </summary>
    public ContainerBuildException(
        string command,
        string workingDirectory,
        int exitCode,
        string standardError,
        string buildOutput,
        string message)
        : base(command, exitCode, standardError, message)
    {
        WorkingDirectory = workingDirectory;
        BuildOutput = buildOutput;
    }

    /// <summary>
    /// Gets a detailed message with troubleshooting information.
    /// </summary>
    public string GetDetailedMessage()
    {
        return $"""
            Container build failed for command:
            {Command}

            Working directory: {WorkingDirectory}
            Exit code: {ExitCode}

            Build output:
            {BuildOutput}

            Troubleshooting:
            1. Verify the build command works when run manually
            2. Check that all required files exist
            3. Ensure Docker has sufficient resources
            4. Review the build output above for specific errors
            """;
    }
}
```

---

## ContainerImageService Implementation

### Description

Default implementation of `IContainerImageService` using CliWrap to execute Docker commands.

### Class Definition

```csharp
/// <summary>
/// Default implementation of IContainerImageService using CliWrap.
/// </summary>
public class ContainerImageService : IContainerImageService
{
    private readonly ILogger<ContainerImageService> _logger;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the ContainerImageService class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
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

            var result = await Cli.Wrap(executable)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                {
                    _logger.LogDebug("[build] {Line}", line);
                    outputBuilder.AppendLine(line);
                }))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                {
                    _logger.LogDebug("[build:err] {Line}", line);
                    outputBuilder.AppendLine(line);
                }))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new ContainerBuildException(
                    command: command,
                    workingDirectory: workingDirectory,
                    exitCode: result.ExitCode,
                    standardError: string.Empty,
                    buildOutput: outputBuilder.ToString(),
                    message: $"Container build failed with exit code {result.ExitCode}");
            }

            _logger.LogInformation("Container image built successfully");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
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
                command: $"docker tag {sourceImage} {targetImage}",
                exitCode: result.ExitCode,
                standardError: result.StandardError,
                message: $"Failed to tag image: {result.StandardError}");
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
            var result = await Cli.Wrap("docker")
                .WithArguments(arguments)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CommandResultValidation.None)
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
            throw ContainerOperationException.DockerNotAvailable();
        }
    }

    private static (string executable, string arguments) ParseCommand(string command)
    {
        // Handle common cases: dotnet, docker, etc.
        var trimmed = command.Trim();
        var spaceIndex = trimmed.IndexOf(' ');

        if (spaceIndex == -1)
        {
            return (trimmed, string.Empty);
        }

        return (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..]);
    }

    private record DockerCommandResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
    }
}
```

---

## Method Signatures Summary

| Method | Parameters | Returns | Description |
|--------|------------|---------|-------------|
| `ImageExistsLocallyAsync` | `imageName`, `tag`, `cancellationToken` | `Task<bool>` | Checks if image:tag exists locally |
| `BuildImageAsync` | `command`, `workingDirectory`, `cancellationToken` | `Task` | Executes build command |
| `TagImageAsync` | `sourceImage`, `targetImage`, `cancellationToken` | `Task` | Tags an existing image |
| `IsDockerAvailableAsync` | `cancellationToken` | `Task<bool>` | Validates Docker is running |
| `GetImageInfoAsync` | `imageName`, `cancellationToken` | `Task<ContainerImageInfo?>` | Gets image metadata |

---

## Docker Commands Used

| Method | Docker Command | Purpose |
|--------|----------------|---------|
| `ImageExistsLocallyAsync` | `docker image inspect {name}:{tag}` | Check if image exists |
| `TagImageAsync` | `docker tag {source} {target}` | Create new tag for image |
| `IsDockerAvailableAsync` | `docker info` | Verify Docker daemon is running |
| `GetImageInfoAsync` | `docker image inspect --format ...` | Get image metadata |

---

## Usage Examples

### Complete Image Build Workflow

```csharp
var containerService = serviceProvider.GetRequiredService<IContainerImageService>();
var gitOps = serviceProvider.GetRequiredService<IGitOperations>();

// Step 1: Get current commit SHA
var sha = await gitOps.GetCurrentCommitShaAsync(repoPath, cancellationToken);
var imageName = "my-api";
var imageTag = sha; // "abc1234"

// Step 2: Check if image already exists
var exists = await containerService.ImageExistsLocallyAsync(imageName, imageTag, cancellationToken);

if (exists)
{
    logger.LogInformation("Image {Image}:{Tag} already exists, skipping build", imageName, imageTag);
    return;
}

// Step 3: Build the image
var buildCommand = $"dotnet publish {repoPath}/src/Api/Api.csproj --os linux /t:PublishContainer -p:ContainerRepository={imageName} -p:ContainerImageTag={imageTag}";

try
{
    await containerService.BuildImageAsync(buildCommand, repoPath, cancellationToken);
    logger.LogInformation("Successfully built {Image}:{Tag}", imageName, imageTag);
}
catch (ContainerBuildException ex)
{
    logger.LogError("Build failed: {Details}", ex.GetDetailedMessage());
    throw;
}
```

### Checking Docker Availability

```csharp
var containerService = serviceProvider.GetRequiredService<IContainerImageService>();

if (!await containerService.IsDockerAvailableAsync(cancellationToken))
{
    throw new InvalidOperationException(
        "Docker is not available. Please start the Docker daemon and try again.");
}
```

### Getting Image Information

```csharp
var containerService = serviceProvider.GetRequiredService<IContainerImageService>();

var info = await containerService.GetImageInfoAsync("my-api:abc1234", cancellationToken);
if (info is not null)
{
    Console.WriteLine($"Image: {info.FullName}");
    Console.WriteLine($"Created: {info.Created}");
    Console.WriteLine($"Size: {info.FormattedSize}");
}
```

---

## Error Cases

| Scenario | Exception | Message/Behavior |
|----------|-----------|------------------|
| Docker not installed | `ContainerOperationException` | "Docker is not available..." |
| Docker daemon not running | `ContainerOperationException` | "Docker is not available..." |
| Build command fails | `ContainerBuildException` | Includes full build output |
| Image not found (inspect) | Returns `false` | Not an exception for existence check |
| Tag operation fails | `ContainerOperationException` | Includes Docker error message |
| Operation cancelled | `OperationCanceledException` | Propagates cancellation |
| Command timeout | `OperationCanceledException` | After configured timeout |

---

## Timeouts

| Operation | Default Timeout | Configurable |
|-----------|----------------|--------------|
| Image inspect | 30 seconds | No |
| Image tag | 30 seconds | No |
| Docker info | 10 seconds | No |
| Build operation | 10 minutes | No |

---

## Thread Safety

The `ContainerImageService` class is thread-safe and stateless. Multiple builds can execute concurrently (though Docker may serialize them internally).

---

## Design Decisions

### Why CliWrap over Docker.DotNet

| Aspect | CliWrap + docker CLI | Docker.DotNet |
|--------|---------------------|---------------|
| Dependency | Uses system docker | Additional NuGet package |
| Consistency | Same as git operations | Different API style |
| Build support | Any build command | Limited to Docker API |
| SDK compatibility | Uses installed Docker | SDK version coupling |
| Streaming output | Simple with PipeTarget | More complex setup |

### Build Command Flexibility

The `BuildImageAsync` method accepts any command string, supporting:
- `dotnet publish /t:PublishContainer`
- `docker build`
- `docker buildx build`
- Custom build scripts
- Any executable that produces a container image
