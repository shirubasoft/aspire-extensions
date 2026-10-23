#pragma warning disable ASPIREPIPELINES001

using CliWrap;
using CliWrap.Buffered;
using Aspire.Hosting.Pipelines;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal static class RepositoryInspector
{
    private static readonly Type[] GitExecutionFailureTypes =
        [typeof(InvalidOperationException), typeof(System.ComponentModel.Win32Exception), typeof(IOException)];

    public static async Task<string> FindRepositoryRootAsync(
        string projectPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var root = await TryFindRepositoryRootAsync(
            projectPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (root is not null)
        {
            return root;
        }

        return ResolveFallbackRepositoryRoot(projectPath);
    }

    private static string ResolveFallbackRepositoryRoot(string projectPath)
    {
        var startDirectory = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Unable to determine the directory for '{projectPath}'.");
        return Path.GetFullPath(startDirectory);
    }

    public static async Task<string?> TryFindRepositoryRootAsync(
        string path,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var startDirectory = GetWorkingDirectory(path);
        var result = await TryRunGitAsync(
            startDirectory,
            ["rev-parse", "--show-toplevel"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            return Path.GetFullPath(result.Output.Trim());
        }

        return null;
    }

    public static async Task<string?> TryGetRemoteAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["config", "--get", "remote.origin.url"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return GetSuccessfulOutput(result);
    }

    private static string? GetSuccessfulOutput((bool Success, string Output) result)
    {
        if (!result.Success)
        {
            return null;
        }

        var output = result.Output.Trim();
        return output.Length == 0 ? null : output;
    }

    public static async Task<bool> IsGitRepositoryAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        bool requireSuccessfulInspection = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(repositoryPath))
        {
            return false;
        }

        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", "--is-inside-work-tree"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return string.Equals(result.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        ThrowIfInspectionWasRequired(
            repositoryPath,
            gitExecutablePath,
            requireSuccessfulInspection);
        return false;
    }

    private static void ThrowIfInspectionWasRequired(
        string repositoryPath,
        string gitExecutablePath,
        bool requireSuccessfulInspection)
    {
        if (!requireSuccessfulInspection)
        {
            return;
        }

        if (ContainsGitMetadata(repositoryPath))
        {
            throw CreateInspectionException(repositoryPath, gitExecutablePath);
        }
    }

    public static async Task<bool> IsDirtyAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        bool requireSuccessfulInspection = false,
        CancellationToken cancellationToken = default)
    {
        if (!await IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                requireSuccessfulInspection,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await InspectDirtyStateAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            requireSuccessfulInspection,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> InspectDirtyStateAsync(
        string repositoryPath,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        bool requireSuccessfulInspection,
        CancellationToken cancellationToken)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["status", "--porcelain", "--untracked-files=normal"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return !string.IsNullOrWhiteSpace(result.Output);
        }

        ThrowIfDirtyInspectionWasRequired(repositoryPath, gitExecutablePath, requireSuccessfulInspection);
        return false;
    }

    private static void ThrowIfDirtyInspectionWasRequired(
        string repositoryPath,
        string gitExecutablePath,
        bool requireSuccessfulInspection)
    {
        Action[] handlers =
        [
            static () => { },
            () => throw CreateInspectionException(repositoryPath, gitExecutablePath)
        ];
        handlers[Convert.ToInt32(requireSuccessfulInspection)]();
    }

    public static async Task<string?> TryGetBranchAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["branch", "--show-current"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return GetSuccessfulOutput(result);
    }

    public static async Task<bool> HasUpstreamAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output);
    }

    public static async Task<string?> TryGetCommitAsync(
        string repositoryPath,
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", "--short=12", "HEAD"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return GetSuccessfulOutput(result);
    }

    public static async Task<string?> TryResolveCommitAsync(
        string repositoryPath,
        string revision = "HEAD",
        string gitExecutablePath = "git",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            repositoryPath,
            ["rev-parse", $"{revision}^{{commit}}"],
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return GetSuccessfulOutput(result);
    }

    private static string GetWorkingDirectory(string path)
    {
        var candidate = Path.GetFullPath(path);
        while (!Directory.Exists(candidate))
        {
            candidate = GetParentDirectory(candidate, path);
        }

        return candidate;
    }

    private static string GetParentDirectory(string candidate, string originalPath)
    {
        var parent = Path.GetDirectoryName(candidate);
        if (parent is null)
        {
            throw new InvalidOperationException(
                $"Unable to determine an existing directory for '{originalPath}'.");
        }

        return string.Equals(parent, candidate, StringComparison.Ordinal)
            ? throw new InvalidOperationException(
                $"Unable to determine an existing directory for '{originalPath}'.")
            : parent;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The handler converts expected Git failures and rethrows every other exception.")]
    private static async Task<(bool Success, string Output)> TryRunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var timeoutDuration = commandTimeout.GetValueOrDefault(TimeSpan.FromSeconds(5));
        var commandWorkingDirectory = ResolveCommandWorkingDirectory(workingDirectory);
        try
        {
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var result = await CliCommand.Wrap(gitExecutablePath)
                .WithArguments(arguments)
                .WithWorkingDirectory(commandWorkingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(linked.Token)
                .ConfigureAwait(false);

            return (result.IsSuccess, result.StandardOutput);
        }
        catch (Exception exception)
        {
            return HandleGitExecutionException(exception, cancellationToken);
        }
    }

    private static string ResolveCommandWorkingDirectory(string workingDirectory)
    {
        if (Directory.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        return GetCommandParentDirectory(workingDirectory);
    }

    private static string GetCommandParentDirectory(string workingDirectory) =>
        new[] { Path.GetDirectoryName(workingDirectory), workingDirectory }
            .OfType<string>()
            .First();

    private static (bool Success, string Output) HandleGitExecutionException(
        Exception exception,
        CancellationToken cancellationToken)
    {
        var timedOut = exception is OperationCanceledException & !cancellationToken.IsCancellationRequested;
        var expectedFailure = timedOut | IsGitExecutionFailure(exception);
        Func<Exception, (bool Success, string Output)>[] handlers =
        [
            RethrowGitExecutionException,
            static _ => (false, string.Empty)
        ];
        return handlers[Convert.ToInt32(expectedFailure)](exception);
    }

    private static (bool Success, string Output) RethrowGitExecutionException(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        return default;
    }

    private static bool IsGitExecutionFailure(Exception exception) =>
        GitExecutionFailureTypes.Any(type => type.IsInstanceOfType(exception));

    private static bool ContainsGitMetadata(string repositoryPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(repositoryPath));
        while (current is not null)
        {
            if (ContainsGitMetadata(current))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool ContainsGitMetadata(DirectoryInfo directory)
    {
        var metadataPath = Path.Combine(directory.FullName, ".git");
        return Directory.Exists(metadataPath) || File.Exists(metadataPath);
    }

    private static InvalidOperationException CreateInspectionException(
        string repositoryPath,
        string gitExecutablePath)
    {
        return new InvalidOperationException(
            $"Unable to inspect Git repository '{repositoryPath}' with executable '{gitExecutablePath}'. " +
            $"Verify {nameof(ModularAppHostsOptions.GitExecutablePath)} and " +
            $"{nameof(ModularAppHostsOptions.RepositoryCommandTimeout)} before materializing modules.");
    }
}

internal sealed record RepositorySyncCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Operation);

internal sealed record RepositorySyncLifecycleEvent(
    string Operation,
    string State,
    string? Reason = null,
    double ElapsedMilliseconds = 0,
    bool IsWarning = false);

internal static class RepositorySynchronizer
{
    private static readonly Dictionary<string, string> OperationTitles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["clone"] = "Clone",
            ["fetch"] = "Fetch",
            ["checkout"] = "Checkout",
            ["fast-forward"] = "Fast-forward",
            ["submodule-update"] = "Update submodules"
        };

    private static readonly string[] MissingRemoteMessages =
    [
        "repository not found",
        "does not appear to be a git repository",
        "project you were looking for could not be found"
    ];

    public static async Task<RepositorySyncCommand?> CreateCommandAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git",
        string githubCliPath = "gh",
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var commands = await CreateCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return commands.Count == 0 ? null : commands[0];
    }

    public static async Task<IReadOnlyList<RepositorySyncCommand>> CreateCommandsAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision = null,
        string gitExecutablePath = "git",
        string githubCliPath = "gh",
        TimeSpan? commandTimeout = null,
        Action<RepositorySyncLifecycleEvent>? lifecycle = null,
        CancellationToken cancellationToken = default)
    {
        var isGitRepository = await RepositoryInspector.IsGitRepositoryAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            requireSuccessfulInspection: true,
            cancellationToken).ConfigureAwait(false);
        if (!isGitRepository)
        {
            return CreateMissingCheckoutCommands(
                repositoryPath,
                repository,
                revision,
                gitExecutablePath,
                githubCliPath,
                lifecycle);
        }

        return await CreateExistingCheckoutCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            lifecycle,
            cancellationToken).ConfigureAwait(false);
    }

    private static List<RepositorySyncCommand> CreateMissingCheckoutCommands(
        string repositoryPath,
        string? repository,
        string? revision,
        string gitExecutablePath,
        string githubCliPath,
        Action<RepositorySyncLifecycleEvent>? lifecycle)
    {
        if (Directory.Exists(repositoryPath))
        {
            ValidateExistingNonRepositoryPath(repositoryPath, repository);
            ReportSkipped(lifecycle, "not-git");
            return [];
        }

        return CreateCloneCommands(
            repositoryPath,
            repository,
            revision,
            gitExecutablePath,
            githubCliPath);
    }

    private static void ValidateExistingNonRepositoryPath(string repositoryPath, string? repository)
    {
        var baseDirectory = GetParentDirectoryOrSelf(repositoryPath);
        if (string.IsNullOrWhiteSpace(repository) ||
            !RepositoryIdentity.IsRemoteRepository(repository, baseDirectory))
        {
            return;
        }

        var normalizedRepository = GetDiagnosticIdentity(repository, baseDirectory, "(unavailable)");
        throw new InvalidOperationException(
            $"Repository path '{repositoryPath}' already exists, but it is not a Git checkout of " +
            $"configured normalized repository identity '{normalizedRepository}'. " +
            "Move that directory or correct the module configuration.");
    }

    private static List<RepositorySyncCommand> CreateCloneCommands(
        string repositoryPath,
        string? repository,
        string? revision,
        string gitExecutablePath,
        string githubCliPath)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new InvalidOperationException(
                $"Repository '{repositoryPath}' does not exist and the module does not define a Git remote.");
        }

        Directory.CreateDirectory(GetRequiredParentDirectory(repositoryPath));
        var commands = new List<RepositorySyncCommand>
        {
            new(
                gitExecutablePath,
                GitHubGitAuthentication.ConfigureCredentialHelper(
                    ["clone", "--recurse-submodules", "--", repository, repositoryPath],
                    repository,
                    githubCliPath),
                "clone")
        };
        AddRevisionCommands(
            commands,
            repositoryPath,
            revision,
            gitExecutablePath,
            githubCliPath,
            repository);
        return commands;
    }

    private static async Task<IReadOnlyList<RepositorySyncCommand>> CreateExistingCheckoutCommandsAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        string? revision,
        string gitExecutablePath,
        string githubCliPath,
        TimeSpan? commandTimeout,
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        CancellationToken cancellationToken)
    {
        var actualRepository = await EnsureExpectedOriginAsync(
            repositoryPath,
            repository,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        var isDirty = await RepositoryInspector.IsDirtyAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            requireSuccessfulInspection: true,
            cancellationToken).ConfigureAwait(false);
        if (isDirty)
        {
            return await CreateDirtyCheckoutCommandsAsync(
                repositoryPath,
                revision,
                gitExecutablePath,
                commandTimeout,
                lifecycle,
                cancellationToken).ConfigureAwait(false);
        }

        return await CreateCleanCheckoutCommandsAsync(
            repositoryPath,
            repository,
            actualRepository,
            updateRepository,
            revision,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            lifecycle,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RepositorySyncCommand>> CreateDirtyCheckoutCommandsAsync(
        string repositoryPath,
        string? revision,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        CancellationToken cancellationToken)
    {
        await EnsureDirtyCheckoutMatchesRevisionAsync(
            repositoryPath,
            revision,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        ReportSkipped(lifecycle, "dirty");
        return [];
    }

    private static async Task<IReadOnlyList<RepositorySyncCommand>> CreateCleanCheckoutCommandsAsync(
        string repositoryPath,
        string? configuredRepository,
        string? actualRepository,
        bool updateRepository,
        string? revision,
        string gitExecutablePath,
        string githubCliPath,
        TimeSpan? commandTimeout,
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(revision))
        {
            return CreateRevisionCommands(
                repositoryPath,
                revision,
                gitExecutablePath,
                githubCliPath,
                SelectRepository(actualRepository, configuredRepository));
        }

        return await CreateUnpinnedCheckoutCommandsAsync(
            repositoryPath,
            configuredRepository,
            actualRepository,
            updateRepository,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            lifecycle,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RepositorySyncCommand>> CreateUnpinnedCheckoutCommandsAsync(
        string repositoryPath,
        string? configuredRepository,
        string? actualRepository,
        bool updateRepository,
        string gitExecutablePath,
        string githubCliPath,
        TimeSpan? commandTimeout,
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        CancellationToken cancellationToken)
    {
        if (!updateRepository)
        {
            ReportSkipped(lifecycle, "disabled");
            return CreateSubmoduleOnlyCommands(
                repositoryPath,
                gitExecutablePath,
                githubCliPath,
                SelectRepository(actualRepository, configuredRepository));
        }

        return await CreateUpdateCommandsAsync(
            repositoryPath,
            configuredRepository,
            actualRepository,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            lifecycle,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RepositorySyncCommand>> CreateUpdateCommandsAsync(
        string repositoryPath,
        string? configuredRepository,
        string? actualRepository,
        string gitExecutablePath,
        string githubCliPath,
        TimeSpan? commandTimeout,
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        CancellationToken cancellationToken)
    {
        var hasUpstream = await RepositoryInspector.HasUpstreamAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        var selectedRepository = SelectRepository(actualRepository, configuredRepository);
        if (!hasUpstream)
        {
            ReportSkipped(lifecycle, "no-upstream");
            return CreateSubmoduleOnlyCommands(
                repositoryPath,
                gitExecutablePath,
                githubCliPath,
                selectedRepository);
        }

        return CreateFastForwardCommands(
            repositoryPath,
            gitExecutablePath,
            githubCliPath,
            selectedRepository);
    }

    private static List<RepositorySyncCommand> CreateRevisionCommands(
        string repositoryPath,
        string revision,
        string gitExecutablePath,
        string githubCliPath,
        string? repository)
    {
        var commands = new List<RepositorySyncCommand>();
        AddRevisionCommands(
            commands,
            repositoryPath,
            revision,
            gitExecutablePath,
            githubCliPath,
            repository);
        return commands;
    }

    private static IReadOnlyList<RepositorySyncCommand> CreateSubmoduleOnlyCommands(
        string repositoryPath,
        string gitExecutablePath,
        string githubCliPath,
        string? repository) =>
        [
            CreateSubmoduleUpdateCommand(repositoryPath, gitExecutablePath, githubCliPath, repository)
        ];

    private static IReadOnlyList<RepositorySyncCommand> CreateFastForwardCommands(
        string repositoryPath,
        string gitExecutablePath,
        string githubCliPath,
        string? repository) =>
        [
            new RepositorySyncCommand(
                gitExecutablePath,
                GitHubGitAuthentication.ConfigureCredentialHelper(
                    ["-C", repositoryPath, "pull", "--ff-only", "--recurse-submodules"],
                    repository,
                    githubCliPath),
                "fast-forward"),
            CreateSubmoduleUpdateCommand(
                repositoryPath,
                gitExecutablePath,
                githubCliPath,
                repository)
        ];

    public static async Task SynchronizeAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        CancellationToken cancellationToken,
        string? revision = null,
        string gitExecutablePath = "git",
        string githubCliPath = "gh",
        TimeSpan? commandTimeout = null,
        Action<string>? progress = null,
        Action<RepositorySyncLifecycleEvent>? lifecycle = null,
        IReportingStep? reportingStep = null)
    {
        var commands = await CreateCommandsAsync(
            repositoryPath,
            repository,
            updateRepository,
            revision,
            gitExecutablePath,
            githubCliPath,
            commandTimeout,
            lifecycle,
            cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, $"Synchronizing repository '{repositoryPath}'.");
        foreach (var command in commands)
        {
            await ExecuteCommandAsync(
                command,
                repositoryPath,
                repository,
                commandTimeout,
                progress,
                lifecycle,
                reportingStep,
                cancellationToken).ConfigureAwait(false);
        }

        ReportProgress(progress, $"Repository '{repositoryPath}' is synchronized.");
    }

    private static async Task ExecuteCommandAsync(
        RepositorySyncCommand command,
        string repositoryPath,
        string? repository,
        TimeSpan? commandTimeout,
        Action<string>? progress,
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        IReportingStep? reportingStep,
        CancellationToken cancellationToken)
    {
        ReportLifecycle(lifecycle, new RepositorySyncLifecycleEvent(command.Operation, "started"));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var reportingTask = await CreateReportingTaskAsync(
            reportingStep,
            command.Operation,
            repositoryPath,
            cancellationToken).ConfigureAwait(false);
        var skippedMissingRemote = await ExecuteCommandWithReportingAsync(
            command,
            repositoryPath,
            repository,
            commandTimeout,
            progress,
            reportingTask,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        ReportLifecycle(
            lifecycle,
            CreateCompletionEvent(command.Operation, skippedMissingRemote, stopwatch.Elapsed.TotalMilliseconds));
    }

    private static async Task<IReportingTask?> CreateReportingTaskAsync(
        IReportingStep? reportingStep,
        string operation,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (reportingStep is null)
        {
            return null;
        }

        return await reportingStep.CreateTaskAsync(
            $"{GetOperationTitle(operation)} {Path.GetFileName(repositoryPath)}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExecuteCommandWithReportingAsync(
        RepositorySyncCommand command,
        string repositoryPath,
        string? repository,
        TimeSpan? commandTimeout,
        Action<string>? progress,
        IReportingTask? reportingTask,
        CancellationToken cancellationToken)
    {
        try
        {
            var skippedMissingRemote = await ExecuteRepositoryCommandAsync(
                command,
                repositoryPath,
                repository,
                commandTimeout,
                progress,
                cancellationToken).ConfigureAwait(false);
            await ReportSuccessAsync(
                reportingTask,
                command.Operation,
                skippedMissingRemote,
                cancellationToken).ConfigureAwait(false);
            return skippedMissingRemote;
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(reportingTask, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await DisposeReportingTaskAsync(reportingTask).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ExecuteRepositoryCommandAsync(
        RepositorySyncCommand command,
        string repositoryPath,
        string? repository,
        TimeSpan? commandTimeout,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await ModuleCliRunner.RunAsync(
            command.Executable,
            command.Arguments,
            GetRequiredParentDirectory(repositoryPath),
            commandTimeout ?? TimeSpan.FromMinutes(2),
            $"prepare {Path.GetFileName(repositoryPath)}",
            cancellationToken,
            progress).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return false;
        }

        return HandleCommandFailure(command, repositoryPath, repository, progress, result);
    }

    private static bool HandleCommandFailure(
        RepositorySyncCommand command,
        string repositoryPath,
        string? repository,
        Action<string>? progress,
        ModuleCliResult result)
    {
        var error = SelectCommandError(result);
        var diagnostic = CreateCredentialFreeCommandDiagnostic(
            error.Trim(),
            repository,
            GetParentDirectoryOrSelf(repositoryPath));
        if (IsMissingRemoteFailure(command.Operation, error))
        {
            ReportProgress(
                progress,
                $"Warning: Automatic fast-forward was skipped for repository '{repositoryPath}' " +
                $"because its remote no longer exists. {diagnostic}");
            return true;
        }

        throw new InvalidOperationException(
            $"Repository synchronization failed for '{repositoryPath}' with exit code " +
            $"{result.ExitCode}: {diagnostic}");
    }

    private static string SelectCommandError(ModuleCliResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

    private static bool IsMissingRemoteFailure(string operation, string error)
    {
        if (!string.Equals(operation, "fast-forward", StringComparison.Ordinal))
        {
            return false;
        }

        return IndicatesMissingRemote(error);
    }

    private static async Task ReportSuccessAsync(
        IReportingTask? reportingTask,
        string operation,
        bool skippedMissingRemote,
        CancellationToken cancellationToken)
    {
        if (reportingTask is null)
        {
            return;
        }

        await reportingTask.SucceedAsync(
            GetReportingSuccessMessage(operation, skippedMissingRemote),
            cancellationToken).ConfigureAwait(false);
    }

    private static string GetReportingSuccessMessage(string operation, bool skippedMissingRemote) =>
        new[]
        {
            $"{GetOperationTitle(operation)} completed",
            $"{GetOperationTitle(operation)} skipped: remote no longer exists"
        }[Convert.ToInt32(skippedMissingRemote)];

    private static async Task ReportFailureAsync(IReportingTask? reportingTask, Exception exception)
    {
        if (reportingTask is null)
        {
            return;
        }

        await reportingTask.FailAsync(exception.Message, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DisposeReportingTaskAsync(IReportingTask? reportingTask)
    {
        if (reportingTask is null)
        {
            return;
        }

        await reportingTask.DisposeAsync().ConfigureAwait(false);
    }

    private static RepositorySyncLifecycleEvent CreateCompletionEvent(
        string operation,
        bool skippedMissingRemote,
        double elapsedMilliseconds) =>
        skippedMissingRemote
            ? new RepositorySyncLifecycleEvent(
                operation,
                "skipped",
                "remote-missing",
                elapsedMilliseconds,
                IsWarning: true)
            : new RepositorySyncLifecycleEvent(
                operation,
                "completed",
                ElapsedMilliseconds: elapsedMilliseconds);

    private static void ReportSkipped(
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        string reason) =>
        ReportLifecycle(lifecycle, new RepositorySyncLifecycleEvent("update", "skipped", reason));

    private static void ReportLifecycle(
        Action<RepositorySyncLifecycleEvent>? lifecycle,
        RepositorySyncLifecycleEvent lifecycleEvent)
    {
        if (lifecycle is not null)
        {
            lifecycle(lifecycleEvent);
        }
    }

    private static void ReportProgress(Action<string>? progress, string message)
    {
        if (progress is not null)
        {
            progress(message);
        }
    }

    private static string? SelectRepository(string? actualRepository, string? configuredRepository) =>
        actualRepository ?? configuredRepository;

    private static string GetParentDirectoryOrSelf(string repositoryPath) =>
        Path.GetDirectoryName(repositoryPath) ?? repositoryPath;

    private static string GetRequiredParentDirectory(string repositoryPath) =>
        Path.GetDirectoryName(repositoryPath)
        ?? throw new InvalidOperationException($"Unable to determine the parent of '{repositoryPath}'.");

    private static string GetOperationTitle(string operation) =>
        OperationTitles.GetValueOrDefault(operation, operation);

    private static void AddRevisionCommands(
        List<RepositorySyncCommand> commands,
        string repositoryPath,
        string? revision,
        string gitExecutablePath,
        string githubCliPath,
        string? repository)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            GitHubGitAuthentication.ConfigureCredentialHelper(
                ["-C", repositoryPath, "fetch", "--tags", "origin", revision],
                repository,
                githubCliPath),
            "fetch"));
        commands.Add(new RepositorySyncCommand(
            gitExecutablePath,
            ["-C", repositoryPath, "checkout", "--detach", "FETCH_HEAD"],
            "checkout"));
        commands.Add(CreateSubmoduleUpdateCommand(
            repositoryPath,
            gitExecutablePath,
            githubCliPath,
            repository));
    }

    private static RepositorySyncCommand CreateSubmoduleUpdateCommand(
        string repositoryPath,
        string gitExecutablePath,
        string githubCliPath,
        string? repository) =>
        new(
            gitExecutablePath,
            GitHubGitAuthentication.ConfigureCredentialHelper(
                ["-C", repositoryPath, "submodule", "update", "--init", "--recursive"],
                repository,
                githubCliPath),
            "submodule-update");

    private static async Task<string?> EnsureExpectedOriginAsync(
        string repositoryPath,
        string? expectedRepository,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var actualRepository = await RepositoryInspector.TryGetRemoteAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedRepository))
        {
            return actualRepository;
        }

        return await ValidateExpectedOriginAsync(
            repositoryPath,
            expectedRepository,
            actualRepository,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ValidateExpectedOriginAsync(
        string repositoryPath,
        string expectedRepository,
        string? actualRepository,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var baseDirectory = Path.GetDirectoryName(repositoryPath) ?? repositoryPath;
        if (await IsExpectedLocalCheckoutAsync(
            expectedRepository,
            repositoryPath,
            baseDirectory,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false))
        {
            return actualRepository;
        }

        ValidateExpectedOrigin(repositoryPath, expectedRepository, actualRepository, baseDirectory);
        return actualRepository;
    }

    private static async Task<bool> IsExpectedLocalCheckoutAsync(
        string expectedRepository,
        string repositoryPath,
        string baseDirectory,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        if (RepositoryIdentity.IsRemoteRepository(expectedRepository, baseDirectory))
        {
            return false;
        }

        return await LocalRepositoryRootsMatchAsync(
            expectedRepository,
            repositoryPath,
            baseDirectory,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateExpectedOrigin(
        string repositoryPath,
        string expectedRepository,
        string? actualRepository,
        string baseDirectory)
    {
        if (OriginsMatch(expectedRepository, actualRepository, baseDirectory))
        {
            return;
        }

        var expectedIdentity = GetDiagnosticIdentity(expectedRepository, baseDirectory, "(unavailable)");
        var actualIdentity = GetDiagnosticIdentity(actualRepository, baseDirectory, "(missing or unavailable)");
        throw new InvalidOperationException(
            $"Repository '{repositoryPath}' has normalized origin '{actualIdentity}', which does not match " +
            $"configured normalized repository identity '{expectedIdentity}'. " +
            "Move the checkout or correct the module configuration.");
    }

    private static bool OriginsMatch(
        string expectedRepository,
        string? actualRepository,
        string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(actualRepository))
        {
            return false;
        }

        if (RepositoryIdentity.RefersToSameRepository(expectedRepository, actualRepository, baseDirectory))
        {
            return true;
        }

        return LocalRepositoriesMatch(expectedRepository, actualRepository, baseDirectory);
    }

    private static string? TryNormalizeDiagnosticIdentity(string? repository, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        try
        {
            return RepositoryIdentity.NormalizeRepositoryIdentity(repository, baseDirectory);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static string CreateCredentialFreeCommandDiagnostic(
        string output,
        string? repository,
        string baseDirectory)
    {
        if (!IsRemoteDiagnosticRepository(repository, baseDirectory))
        {
            return output;
        }

        var normalizedRepository = GetDiagnosticIdentity(
            repository,
            baseDirectory,
            "the configured repository");
        return $"Git could not synchronize normalized repository identity '{normalizedRepository}'. " +
            "Verify repository access and configured credentials.";
    }

    private static bool IsRemoteDiagnosticRepository(string? repository, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        return RepositoryIdentity.IsRemoteRepository(repository, baseDirectory);
    }

    private static string GetDiagnosticIdentity(
        string? repository,
        string baseDirectory,
        string fallback) =>
        TryNormalizeDiagnosticIdentity(repository, baseDirectory) ?? fallback;

    private static bool IndicatesMissingRemote(string output)
    {
        foreach (var message in MissingRemoteMessages)
        {
            if (output.Contains(message, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LocalRepositoriesMatch(string first, string second, string baseDirectory)
    {
        if (RepositoryIdentity.IsRemoteRepository(first, baseDirectory) ||
            RepositoryIdentity.IsRemoteRepository(second, baseDirectory))
        {
            return false;
        }

        return PathSafety.AreEqual(
            Path.GetFullPath(first, baseDirectory),
            Path.GetFullPath(second, baseDirectory));
    }

    private static async Task<bool> LocalRepositoryRootsMatchAsync(
        string expectedRepository,
        string repositoryPath,
        string baseDirectory,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(expectedRepository, baseDirectory);
        var expectedRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            expectedPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        var actualRoot = await RepositoryInspector.TryFindRepositoryRootAsync(
            repositoryPath,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        return expectedRoot is not null && actualRoot is not null &&
            PathSafety.AreEqual(expectedRoot, actualRoot);
    }

    private static async Task EnsureDirtyCheckoutMatchesRevisionAsync(
        string repositoryPath,
        string? revision,
        string gitExecutablePath,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return;
        }

        var currentCommit = await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            gitExecutablePath: gitExecutablePath,
            commandTimeout: commandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var expectedCommit = await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            revision,
            gitExecutablePath,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);
        ValidateDirtyCheckoutRevision(repositoryPath, revision, currentCommit, expectedCommit);
    }

    private static void ValidateDirtyCheckoutRevision(
        string repositoryPath,
        string revision,
        string? currentCommit,
        string? expectedCommit)
    {
        ValidateResolvedDirtyCommit(repositoryPath, revision, currentCommit);
        ValidateResolvedDirtyCommit(repositoryPath, revision, expectedCommit);
        if (!string.Equals(currentCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateDirtyCheckoutRevisionException(repositoryPath, revision);
        }
    }

    private static void ValidateResolvedDirtyCommit(
        string repositoryPath,
        string revision,
        string? commit)
    {
        if (commit is null)
        {
            throw CreateDirtyCheckoutRevisionException(repositoryPath, revision);
        }
    }

    private static InvalidOperationException CreateDirtyCheckoutRevisionException(
        string repositoryPath,
        string revision) =>
        new(
            $"Repository '{repositoryPath}' has local changes and is not at configured revision '{revision}'. " +
            "Commit or stash the changes before switching revisions.");
}
