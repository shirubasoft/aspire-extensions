namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Exception thrown when a container image build operation fails.
/// </summary>
/// <remarks>
/// This exception extends <see cref="ContainerOperationException"/> with additional
/// build-specific information such as the working directory and build output.
/// The <see cref="GetDetailedMessage"/> method provides troubleshooting information.
/// </remarks>
[Serializable]
public class ContainerBuildException : ContainerOperationException
{
    /// <summary>
    /// Gets the working directory where the build was executed.
    /// </summary>
    /// <remarks>
    /// This is typically the repository root or the directory containing the Dockerfile.
    /// </remarks>
    public string WorkingDirectory { get; }

    /// <summary>
    /// Gets the build output (stdout) from the container build process.
    /// </summary>
    /// <remarks>
    /// This contains the full build output, which can be helpful for diagnosing
    /// build failures that may not be apparent from the error message alone.
    /// </remarks>
    public string BuildOutput { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerBuildException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="command">The container build command that was executed.</param>
    /// <param name="exitCode">The exit code returned by the build process.</param>
    /// <param name="standardError">The standard error output from the build process.</param>
    /// <param name="workingDirectory">The working directory where the build was executed.</param>
    /// <param name="buildOutput">The build output (stdout) from the build process.</param>
    public ContainerBuildException(
        string message,
        string command,
        int exitCode,
        string standardError,
        string workingDirectory,
        string buildOutput)
        : base(message, command, exitCode, standardError)
    {
        WorkingDirectory = workingDirectory;
        BuildOutput = buildOutput;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerBuildException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="command">The container build command that was executed.</param>
    /// <param name="exitCode">The exit code returned by the build process.</param>
    /// <param name="standardError">The standard error output from the build process.</param>
    /// <param name="workingDirectory">The working directory where the build was executed.</param>
    /// <param name="buildOutput">The build output (stdout) from the build process.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public ContainerBuildException(
        string message,
        string command,
        int exitCode,
        string standardError,
        string workingDirectory,
        string buildOutput,
        Exception innerException)
        : base(message, command, exitCode, standardError, innerException)
    {
        WorkingDirectory = workingDirectory;
        BuildOutput = buildOutput;
    }

    /// <summary>
    /// Gets a detailed message with build output and troubleshooting information.
    /// </summary>
    /// <returns>A detailed message including build output and troubleshooting steps.</returns>
    /// <remarks>
    /// The detailed message includes:
    /// <list type="bullet">
    ///   <item>The original error message</item>
    ///   <item>The command that was executed</item>
    ///   <item>The working directory</item>
    ///   <item>The exit code</item>
    ///   <item>Standard error output</item>
    ///   <item>Build output (truncated if very long)</item>
    ///   <item>Troubleshooting suggestions</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// try
    /// {
    ///     // Build container
    /// }
    /// catch (ContainerBuildException ex)
    /// {
    ///     Console.WriteLine(ex.GetDetailedMessage());
    /// }
    /// </code>
    /// </example>
    public string GetDetailedMessage()
    {
        var truncatedBuildOutput = BuildOutput.Length > 2000
            ? $"[Truncated, showing last 2000 characters]\n...{BuildOutput[^2000..]}"
            : BuildOutput;

        return $"""
            {Message}

            Command: {Command}
            Working Directory: {WorkingDirectory}
            Exit Code: {ExitCode}

            Standard Error:
            {StandardError}

            Build Output:
            {truncatedBuildOutput}

            Troubleshooting:
            1. Verify the repository is at the correct path: {WorkingDirectory}
            2. Check that all required files (Dockerfile, project files) exist
            3. Ensure Docker daemon is running: docker info
            4. Try running the build command manually:
               cd "{WorkingDirectory}" && {Command}
            5. Check for sufficient disk space and memory
            6. Review the build output above for specific error messages
            """;
    }
}
