namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Exception thrown when a Git operation fails.
/// </summary>
/// <remarks>
/// This exception captures details about the failed Git command, including the exit code
/// and standard error output, to aid in diagnosing the issue.
/// </remarks>
[Serializable]
public class GitOperationException : Exception
{
    /// <summary>
    /// Gets the Git command that was executed.
    /// </summary>
    /// <example>
    /// <code>
    /// Command = "git rev-parse HEAD"
    /// </code>
    /// </example>
    public string Command { get; }

    /// <summary>
    /// Gets the path to the repository where the command was executed.
    /// </summary>
    /// <remarks>
    /// This may be empty if the command was not executed in a specific repository context.
    /// </remarks>
    public string RepositoryPath { get; }

    /// <summary>
    /// Gets the exit code returned by the Git process.
    /// </summary>
    /// <remarks>
    /// A non-zero exit code indicates that the Git command failed.
    /// Common exit codes:
    /// <list type="bullet">
    ///   <item>1 - Generic error</item>
    ///   <item>128 - Fatal error (e.g., not a git repository)</item>
    /// </list>
    /// </remarks>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the standard error output from the Git process.
    /// </summary>
    /// <remarks>
    /// Contains the error message output by Git, which often includes
    /// details about why the command failed.
    /// </remarks>
    public string StandardError { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GitOperationException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="command">The Git command that was executed.</param>
    /// <param name="repositoryPath">The path to the repository where the command was executed.</param>
    /// <param name="exitCode">The exit code returned by the Git process.</param>
    /// <param name="standardError">The standard error output from the Git process.</param>
    public GitOperationException(
        string message,
        string command,
        string repositoryPath,
        int exitCode,
        string standardError)
        : base(message)
    {
        Command = command;
        RepositoryPath = repositoryPath;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GitOperationException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="command">The Git command that was executed.</param>
    /// <param name="repositoryPath">The path to the repository where the command was executed.</param>
    /// <param name="exitCode">The exit code returned by the Git process.</param>
    /// <param name="standardError">The standard error output from the Git process.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public GitOperationException(
        string message,
        string command,
        string repositoryPath,
        int exitCode,
        string standardError,
        Exception innerException)
        : base(message, innerException)
    {
        Command = command;
        RepositoryPath = repositoryPath;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Creates a <see cref="GitOperationException"/> indicating that Git is not installed or not in PATH.
    /// </summary>
    /// <returns>A new <see cref="GitOperationException"/> with appropriate message and properties.</returns>
    /// <example>
    /// <code>
    /// throw GitOperationException.GitNotInstalled();
    /// </code>
    /// </example>
    public static GitOperationException GitNotInstalled()
    {
        return new GitOperationException(
            message: "Git is not installed or not found in the system PATH. Please install Git and ensure it is available in your PATH.",
            command: "git",
            repositoryPath: string.Empty,
            exitCode: -1,
            standardError: "Git executable not found");
    }

    /// <summary>
    /// Creates a <see cref="GitOperationException"/> indicating that the specified path is not a Git repository.
    /// </summary>
    /// <param name="repositoryPath">The path that was expected to be a Git repository.</param>
    /// <returns>A new <see cref="GitOperationException"/> with appropriate message and properties.</returns>
    /// <example>
    /// <code>
    /// throw GitOperationException.NotARepository("/path/to/directory");
    /// </code>
    /// </example>
    public static GitOperationException NotARepository(string repositoryPath)
    {
        return new GitOperationException(
            message: $"The path '{repositoryPath}' is not a Git repository.",
            command: "git rev-parse --git-dir",
            repositoryPath: repositoryPath,
            exitCode: 128,
            standardError: "fatal: not a git repository (or any of the parent directories): .git");
    }
}
