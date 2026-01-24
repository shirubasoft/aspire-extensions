using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using CliWrapLib = CliWrap;

namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Default implementation of <see cref="IGitOperations"/> using CliWrap.
/// </summary>
/// <remarks>
/// This implementation executes git CLI commands via CliWrap for consistent behavior
/// across platforms. It is thread-safe and stateless, allowing multiple calls to
/// execute concurrently.
/// </remarks>
public class GitOperations : IGitOperations
{
    private readonly ILogger<GitOperations> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitOperations"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when logger is null.</exception>
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
            cancellationToken).ConfigureAwait(false);

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
                cancellationToken).ConfigureAwait(false);

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
            cancellationToken).ConfigureAwait(false);

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
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (GitOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Executes a git command in the specified repository directory.
    /// </summary>
    /// <param name="repositoryPath">The repository directory to run the command in.</param>
    /// <param name="arguments">The git command arguments (without 'git' prefix).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The standard output from the git command.</returns>
    /// <exception cref="GitOperationException">
    /// Thrown when the git command fails or git is not installed.
    /// </exception>
    private async Task<string> ExecuteGitCommandAsync(
        string repositoryPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        // Check if the directory exists first to provide a better error message
        if (!Directory.Exists(repositoryPath))
        {
            throw GitOperationException.NotARepository(repositoryPath);
        }

        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        try
        {
            var result = await CliWrapLib.Cli.Wrap("git")
                .WithArguments(arguments)
                .WithWorkingDirectory(repositoryPath)
                .WithStandardOutputPipe(CliWrapLib.PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithStandardErrorPipe(CliWrapLib.PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CliWrapLib.CommandResultValidation.None)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new GitOperationException(
                    message: $"Git command failed: git {arguments}",
                    command: $"git {arguments}",
                    repositoryPath: repositoryPath,
                    exitCode: result.ExitCode,
                    standardError: stdErrBuffer.ToString());
            }

            return stdOutBuffer.ToString();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // File not found - git not installed
        {
            throw GitOperationException.GitNotInstalled();
        }
    }
}
