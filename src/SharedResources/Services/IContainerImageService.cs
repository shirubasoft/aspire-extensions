namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service for container image operations using the Docker CLI.
/// </summary>
/// <remarks>
/// Implementations use the Docker CLI via CliWrap for consistent behavior
/// and to avoid SDK version dependencies. All operations support cancellation
/// and have appropriate timeouts configured.
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
    /// Thrown when <paramref name="imageName"/> or <paramref name="tag"/> is null.
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
    /// <param name="command">Full build command to execute (e.g., "dotnet publish ..." or "docker build ...").</param>
    /// <param name="workingDirectory">Working directory for the command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="command"/> or <paramref name="workingDirectory"/> is null.
    /// </exception>
    /// <exception cref="ContainerBuildException">
    /// Thrown when the build command fails with a non-zero exit code.
    /// </exception>
    /// <exception cref="ContainerOperationException">
    /// Thrown when the command executable is not found (e.g., Docker not installed).
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled via the cancellation token.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The command is parsed by splitting on the first space to separate the executable
    /// from its arguments. Build output is streamed to the logger in real-time.
    /// </para>
    /// <para>
    /// This operation has a 10-minute timeout by default.
    /// </para>
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
    /// Thrown when <paramref name="sourceImage"/> or <paramref name="targetImage"/> is null.
    /// </exception>
    /// <exception cref="ContainerOperationException">
    /// Thrown when Docker is not available, the source image doesn't exist, or the tag operation fails.
    /// </exception>
    /// <remarks>
    /// This operation has a 30-second timeout.
    /// </remarks>
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
    /// <remarks>
    /// This method executes <c>docker info</c> to verify the Docker daemon is running.
    /// It does not throw exceptions for Docker unavailability; instead, it returns false.
    /// This operation has a 10-second timeout.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (!await containerService.IsDockerAvailableAsync(cancellationToken))
    /// {
    ///     throw new InvalidOperationException("Docker is not available");
    /// }
    /// </code>
    /// </example>
    Task<bool> IsDockerAvailableAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about a local image.
    /// </summary>
    /// <param name="imageName">Image name with tag (e.g., "my-api:abc1234").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image information or null if the image doesn't exist.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="imageName"/> is null.
    /// </exception>
    /// <remarks>
    /// This method uses <c>docker image inspect</c> to retrieve image metadata.
    /// If the image does not exist or an error occurs, null is returned rather than throwing.
    /// This operation has a 30-second timeout.
    /// </remarks>
    /// <example>
    /// <code>
    /// var info = await containerService.GetImageInfoAsync("my-api:abc1234", cancellationToken);
    /// if (info is not null)
    /// {
    ///     Console.WriteLine($"Image size: {info.FormattedSize}");
    /// }
    /// </code>
    /// </example>
    Task<ContainerImageInfo?> GetImageInfoAsync(
        string imageName,
        CancellationToken cancellationToken = default);
}
