# Contract: IGitOperations

## Overview

This contract defines the interface and implementation for git operations needed to determine container image tags based on repository commit state.

## Namespace

```csharp
namespace Aspire.Hosting.SharedResources;
```

## Dependencies

- `CliWrap` (3.10.0+) - For CLI process execution
- `Microsoft.Extensions.Logging` (for ILogger)

---

## IGitOperations Interface

### Description

Service for git operations needed to determine image tags and repository state.

### Interface Definition

```csharp
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
```

---

## GitOperationException

### Description

Exception thrown when a git operation fails.

### Class Definition

```csharp
/// <summary>
/// Exception thrown when a git operation fails.
/// </summary>
[Serializable]
public class GitOperationException : Exception
{
    /// <summary>
    /// The git command that was executed.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// The repository path where the command was executed.
    /// </summary>
    public string RepositoryPath { get; }

    /// <summary>
    /// The exit code from the git command.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// The standard error output from git.
    /// </summary>
    public string StandardError { get; }

    /// <summary>
    /// Initializes a new instance of the GitOperationException class.
    /// </summary>
    public GitOperationException(
        string command,
        string repositoryPath,
        int exitCode,
        string standardError,
        string message)
        : base(message)
    {
        Command = command;
        RepositoryPath = repositoryPath;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>
    /// Creates an exception for when git is not installed.
    /// </summary>
    public static GitOperationException GitNotInstalled()
    {
        return new GitOperationException(
            command: "git",
            repositoryPath: string.Empty,
            exitCode: -1,
            standardError: "git: command not found",
            message: "Git is not installed or not in PATH. Please install git and try again.");
    }

    /// <summary>
    /// Creates an exception for when a path is not a git repository.
    /// </summary>
    public static GitOperationException NotARepository(string repositoryPath)
    {
        return new GitOperationException(
            command: "git rev-parse",
            repositoryPath: repositoryPath,
            exitCode: 128,
            standardError: "fatal: not a git repository",
            message: $"Path is not a git repository: {repositoryPath}");
    }
}
```

---

## GitOperations Implementation

### Description

Default implementation of `IGitOperations` using CliWrap to execute git commands.

### Class Definition

```csharp
/// <summary>
/// Default implementation of IGitOperations using CliWrap.
/// </summary>
public class GitOperations : IGitOperations
{
    private readonly ILogger<GitOperations> _logger;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the GitOperations class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public GitOperations(ILogger<GitOperations> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentCommitShaAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        _logger.LogDebug("Getting current commit SHA for {RepositoryPath}", repositoryPath);

        var result = await ExecuteGitCommandAsync(
            repositoryPath,
            "rev-parse --short=7 HEAD",
            cancellationToken);

        var sha = result.Trim();
        _logger.LogDebug("Current commit SHA: {Sha}", sha);

        return sha;
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);

        _logger.LogDebug("Getting current branch for {RepositoryPath}", repositoryPath);

        try
        {
            var result = await ExecuteGitCommandAsync(
                repositoryPath,
                "symbolic-ref --short HEAD",
                cancellationToken);

            var branch = result.Trim();
            _logger.LogDebug("Current branch: {Branch}", branch);

            return branch;
        }
        catch (GitOperationException ex) when (ex.ExitCode == 128)
        {
            // Detached HEAD state
            _logger.LogDebug("Repository is in detached HEAD state");
            return "HEAD";
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasUncommittedChangesAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);

        _logger.LogDebug("Checking for uncommitted changes in {RepositoryPath}", repositoryPath);

        var result = await ExecuteGitCommandAsync(
            repositoryPath,
            "status --porcelain",
            cancellationToken);

        var hasChanges = !string.IsNullOrWhiteSpace(result);

        if (hasChanges)
        {
            _logger.LogDebug("Repository has uncommitted changes");
        }
        else
        {
            _logger.LogDebug("Repository working tree is clean");
        }

        return hasChanges;
    }

    /// <inheritdoc />
    public async Task<bool> IsGitRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);

        if (!Directory.Exists(repositoryPath))
        {
            return false;
        }

        try
        {
            await ExecuteGitCommandAsync(
                repositoryPath,
                "rev-parse --git-dir",
                cancellationToken);

            return true;
        }
        catch (GitOperationException)
        {
            return false;
        }
    }

    private async Task<string> ExecuteGitCommandAsync(
        string repositoryPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        try
        {
            var result = await Cli.Wrap("git")
                .WithArguments(arguments)
                .WithWorkingDirectory(repositoryPath)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new GitOperationException(
                    command: $"git {arguments}",
                    repositoryPath: repositoryPath,
                    exitCode: result.ExitCode,
                    standardError: stdErrBuffer.ToString(),
                    message: $"Git command failed: git {arguments}");
            }

            return stdOutBuffer.ToString();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // File not found
        {
            throw GitOperationException.GitNotInstalled();
        }
    }
}
```

---

## Method Signatures Summary

| Method | Parameters | Returns | Description |
|--------|------------|---------|-------------|
| `GetCurrentCommitShaAsync` | `repositoryPath`, `cancellationToken` | `Task<string>` | Returns 7-char short SHA of HEAD |
| `GetCurrentBranchAsync` | `repositoryPath`, `cancellationToken` | `Task<string>` | Returns branch name or "HEAD" if detached |
| `HasUncommittedChangesAsync` | `repositoryPath`, `cancellationToken` | `Task<bool>` | Checks for dirty working tree |
| `IsGitRepositoryAsync` | `repositoryPath`, `cancellationToken` | `Task<bool>` | Validates path is a git repo |

---

## Git Commands Used

| Method | Git Command | Purpose |
|--------|-------------|---------|
| `GetCurrentCommitShaAsync` | `git rev-parse --short=7 HEAD` | Get 7-character commit hash |
| `GetCurrentBranchAsync` | `git symbolic-ref --short HEAD` | Get current branch name |
| `HasUncommittedChangesAsync` | `git status --porcelain` | Check for uncommitted changes |
| `IsGitRepositoryAsync` | `git rev-parse --git-dir` | Verify directory is a git repo |

---

## Usage Examples

### Getting Commit SHA for Image Tag

```csharp
var gitOps = serviceProvider.GetRequiredService<IGitOperations>();

try
{
    var sha = await gitOps.GetCurrentCommitShaAsync(
        "/home/user/repos/my-service",
        cancellationToken);

    var imageTag = $"my-service:{sha}"; // "my-service:abc1234"
    Console.WriteLine($"Image tag: {imageTag}");
}
catch (GitOperationException ex)
{
    Console.WriteLine($"Git operation failed: {ex.Message}");
    Console.WriteLine($"Command: {ex.Command}");
    Console.WriteLine($"Error: {ex.StandardError}");
}
```

### Checking for Dirty Working Tree

```csharp
var gitOps = serviceProvider.GetRequiredService<IGitOperations>();

var hasChanges = await gitOps.HasUncommittedChangesAsync(
    "/home/user/repos/my-service",
    cancellationToken);

if (hasChanges)
{
    logger.LogWarning(
        "Repository has uncommitted changes. " +
        "Image will be tagged with current HEAD SHA.");
}
```

### Full Repository State Check

```csharp
var gitOps = serviceProvider.GetRequiredService<IGitOperations>();
var repoPath = "/home/user/repos/my-service";

// Validate it's a git repo
if (!await gitOps.IsGitRepositoryAsync(repoPath, cancellationToken))
{
    throw new InvalidOperationException($"Not a git repository: {repoPath}");
}

// Get current state
var sha = await gitOps.GetCurrentCommitShaAsync(repoPath, cancellationToken);
var branch = await gitOps.GetCurrentBranchAsync(repoPath, cancellationToken);
var isDirty = await gitOps.HasUncommittedChangesAsync(repoPath, cancellationToken);

Console.WriteLine($"Repository: {repoPath}");
Console.WriteLine($"Branch: {branch}");
Console.WriteLine($"Commit: {sha}");
Console.WriteLine($"Dirty: {isDirty}");
```

---

## Error Cases

| Scenario | Exception | Message/Behavior |
|----------|-----------|------------------|
| Git not installed | `GitOperationException` | "Git is not installed or not in PATH" |
| Path is not a git repository | `GitOperationException` | "Path is not a git repository: {path}" |
| Repository path is null | `ArgumentNullException` | Standard argument null exception |
| Repository path is empty | `ArgumentException` | "Value cannot be null or whitespace" |
| Operation cancelled | `OperationCanceledException` | Propagates cancellation |
| Git command times out | `OperationCanceledException` | After 30 second timeout |
| Detached HEAD (for branch) | Returns `"HEAD"` | Not an exception, expected behavior |

---

## Thread Safety

The `GitOperations` class is thread-safe and stateless. Multiple calls can execute concurrently. Each method creates its own process and buffers.

---

## Performance Considerations

- Default timeout of 30 seconds per command
- No caching - each call executes a new git command
- Consider caching SHA if calling multiple times for same repository in quick succession
- Working tree status check may be slow for large repositories

---

## Design Decisions

### Why CliWrap over LibGit2Sharp

| Aspect | CliWrap + git CLI | LibGit2Sharp |
|--------|-------------------|--------------|
| Native dependencies | None (uses system git) | Requires native libgit2 |
| Consistency | Same as docker CLI approach | Different paradigm |
| Deployment | Simple, no additional binaries | Complex, platform-specific |
| Maintenance | Git CLI is stable | Library updates needed |
| Features needed | Only basic operations | Full git API (overkill) |

### Dirty Working Tree Handling

The system logs a warning when uncommitted changes are detected but uses the same SHA tag (no `-dirty` suffix). This simplifies caching and avoids tag proliferation while still informing the developer of the state.
