namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service for git operations needed to determine image tags.
/// </summary>
/// <remarks>
/// Implementations use the git CLI via CliWrap for consistent behavior
/// across platforms and no native dependency requirements.
/// </remarks>
public interface IGitOperations
{
    /// <summary>
    /// Gets the current commit SHA (short form, 7 characters) for a repository.
    /// </summary>
    /// <param name="repositoryPath">Absolute path to the repository root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The 7-character short SHA of the current HEAD commit.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when repositoryPath is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when repositoryPath is empty or whitespace.
    /// </exception>
    /// <exception cref="GitOperationException">
    /// Thrown when the git command fails or the path is not a git repository.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled.
    /// </exception>
    /// <example>
    /// <code>
    /// var sha = await gitOps.GetCurrentCommitShaAsync("/home/user/repos/my-repo");
    /// // Returns: "abc1234"
    /// </code>
    /// </example>
    Task<string> GetCurrentCommitShaAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current branch name.
    /// </summary>
    /// <param name="repositoryPath">Absolute path to the repository root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The name of the current branch, or "HEAD" if in detached HEAD state.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when repositoryPath is null.
    /// </exception>
    /// <exception cref="GitOperationException">
    /// Thrown when the git command fails.
    /// </exception>
    /// <example>
    /// <code>
    /// var branch = await gitOps.GetCurrentBranchAsync("/home/user/repos/my-repo");
    /// // Returns: "main" or "feature/my-branch" or "HEAD"
    /// </code>
    /// </example>
    Task<string> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the working tree has uncommitted changes.
    /// </summary>
    /// <param name="repositoryPath">Absolute path to the repository root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// True if there are staged or unstaged changes; otherwise, false.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when repositoryPath is null.
    /// </exception>
    /// <exception cref="GitOperationException">
    /// Thrown when the git command fails.
    /// </exception>
    /// <remarks>
    /// This method checks for both staged and unstaged changes using
    /// <c>git status --porcelain</c>. An empty output indicates a clean
    /// working tree.
    /// </remarks>
    Task<bool> HasUncommittedChangesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a path is a git repository.
    /// </summary>
    /// <param name="repositoryPath">Path to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the path is a valid git repository; otherwise, false.</returns>
    Task<bool> IsGitRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
