namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Exception thrown when a container operation fails.
/// </summary>
/// <remarks>
/// This exception captures details about the failed container command, including the exit code
/// and standard error output, to aid in diagnosing the issue. This is the base class for
/// more specific container-related exceptions like <see cref="ContainerBuildException"/>.
/// </remarks>
[Serializable]
public class ContainerOperationException : Exception
{
    /// <summary>
    /// Gets the container command that was executed.
    /// </summary>
    /// <example>
    /// <code>
    /// Command = "docker build -t my-image ."
    /// </code>
    /// </example>
    public string Command { get; }

    /// <summary>
    /// Gets the exit code returned by the container runtime process.
    /// </summary>
    /// <remarks>
    /// A non-zero exit code indicates that the container command failed.
    /// </remarks>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the standard error output from the container runtime process.
    /// </summary>
    /// <remarks>
    /// Contains the error message output by the container runtime, which often includes
    /// details about why the command failed.
    /// </remarks>
    public string StandardError { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerOperationException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="command">The container command that was executed.</param>
    /// <param name="exitCode">The exit code returned by the container runtime process.</param>
    /// <param name="standardError">The standard error output from the container runtime process.</param>
    public ContainerOperationException(
        string message,
        string command,
        int exitCode,
        string standardError)
        : base(message)
    {
        Command = command;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerOperationException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="command">The container command that was executed.</param>
    /// <param name="exitCode">The exit code returned by the container runtime process.</param>
    /// <param name="standardError">The standard error output from the container runtime process.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public ContainerOperationException(
        string message,
        string command,
        int exitCode,
        string standardError,
        Exception innerException)
        : base(message, innerException)
    {
        Command = command;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Creates a <see cref="ContainerOperationException"/> indicating that Docker is not available.
    /// </summary>
    /// <returns>A new <see cref="ContainerOperationException"/> with appropriate message and properties.</returns>
    /// <remarks>
    /// This factory method is used when Docker is not installed, not running, or the Docker daemon
    /// is not accessible to the current user.
    /// </remarks>
    /// <example>
    /// <code>
    /// throw ContainerOperationException.DockerNotAvailable();
    /// </code>
    /// </example>
    public static ContainerOperationException DockerNotAvailable()
    {
        return new ContainerOperationException(
            message: "Docker is not available. Please ensure Docker is installed and the Docker daemon is running.",
            command: "docker",
            exitCode: -1,
            standardError: "Docker daemon not accessible");
    }
}
