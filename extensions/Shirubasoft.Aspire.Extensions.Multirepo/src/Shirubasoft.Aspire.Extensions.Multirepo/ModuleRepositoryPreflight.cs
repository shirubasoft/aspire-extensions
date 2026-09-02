using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal enum ModuleRequiredPathKind
{
    Directory,
    File
}

internal sealed record ModuleRequiredPath(
    string ModuleName,
    string Description,
    string Path,
    ModuleRequiredPathKind Kind,
    bool RequiredOnRun = true);

internal static class ModuleRepositoryPreflight
{
    private static readonly Action<ILogger, int, string, Exception?> LogPreflightFailed =
        LoggerMessage.Define<int, string>(
            LogLevel.Error,
            new EventId(10, nameof(LogPreflightFailed)),
            "Repository preflight failed with {FailureCount} problem(s). Run '{InitializeCommand}'.");

    public static string CreateInitializeCommand(string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        return $"aspire do initialize --apphost \"{Path.GetFullPath(appHostPath)}\" --non-interactive";
    }

    public static async Task ValidateAsync(
        IEnumerable<ModuleRepositoryRequirement> repositories,
        IEnumerable<ModuleRequiredPath> requiredPaths,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        string appHostPath,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(requiredPaths);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();
        await AddRepositoryFailuresAsync(
            failures,
            repositories,
            stateStore,
            settings,
            cancellationToken).ConfigureAwait(false);
        AddRequiredPathFailures(failures, requiredPaths);
        ThrowIfPreflightFailed(failures, appHostPath, logger);
    }

    private static async Task AddRepositoryFailuresAsync(
        List<string> failures,
        IEnumerable<ModuleRepositoryRequirement> repositories,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var repository in repositories.OrderBy(
            requirement => requirement.RepositoryPath,
            PathSafety.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddRepositoryFailureAsync(
                failures,
                repository,
                stateStore,
                settings,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddRepositoryFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!repository.RequiredOnRun)
        {
            return;
        }

        await AddRequiredRepositoryFailureAsync(
            failures,
            repository,
            stateStore,
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddRequiredRepositoryFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repository.RepositoryPath))
        {
            failures.Add(
                $"modules {FormatModules(repository.ModuleNames)} require repository " +
                $"'{repository.NormalizedRepository}' at '{repository.RepositoryPath}', but the directory is missing");
            return;
        }

        await AddCheckoutFailureAsync(
            failures,
            repository,
            stateStore,
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddCheckoutFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        var isGitRepository = await RepositoryInspector.IsGitRepositoryAsync(
            repository.RepositoryPath,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            requireSuccessfulInspection: true,
            cancellationToken).ConfigureAwait(false);
        if (!isGitRepository)
        {
            failures.Add(
                $"modules {FormatModules(repository.ModuleNames)} require '{repository.RepositoryPath}' to be a Git checkout");
            return;
        }

        await AddRepositoryStateFailureAsync(
            failures,
            repository,
            stateStore,
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddRepositoryStateFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        var state = await stateStore.ReadAsync(repository, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            var stateLocation = FormatStateLocation(stateStore.StateFilePath);
            failures.Add(
                $"modules {FormatModules(repository.ModuleNames)} have no initialization state for " +
                $"'{repository.RepositoryPath}'{stateLocation}");
            return;
        }

        await AddMatchingRepositoryStateFailureAsync(
            failures,
            repository,
            state,
            stateStore.StateFilePath,
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddMatchingRepositoryStateFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        ModuleRepositoryInitializationState state,
        string? stateFilePath,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!state.Matches(repository))
        {
            var stateLocation = FormatStateLocation(stateFilePath);
            failures.Add(
                $"modules {FormatModules(repository.ModuleNames)} have initialization state that does not match " +
                $"the current repository configuration for '{repository.RepositoryPath}'{stateLocation}");
            return;
        }

        await AddOriginFailureAsync(
            failures,
            repository,
            state,
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddOriginFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        ModuleRepositoryInitializationState state,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        var origin = await RepositoryInspector.TryGetRemoteAsync(
            repository.RepositoryPath,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var normalizedOrigin = NormalizeOrigin(origin, repository.RepositoryPath);
        if (!OriginMatches(repository, state, normalizedOrigin))
        {
            failures.Add(
                $"modules {FormatModules(repository.ModuleNames)} require origin " +
                $"'{repository.NormalizedRepository}' at '{repository.RepositoryPath}', but the checkout origin differs");
            return;
        }

        await AddRevisionFailureAsync(
            failures,
            repository,
            state,
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeOrigin(string? origin, string repositoryPath) =>
        origin is null
            ? null
            : RepositoryIdentity.NormalizeRepositoryIdentity(origin, repositoryPath);

    private static bool OriginMatches(
        ModuleRepositoryRequirement repository,
        ModuleRepositoryInitializationState state,
        string? normalizedOrigin) =>
        string.Equals(normalizedOrigin, repository.NormalizedRepository, StringComparison.Ordinal) &&
        string.Equals(state.Origin, repository.NormalizedRepository, StringComparison.Ordinal);

    private static async Task AddRevisionFailureAsync(
        List<string> failures,
        ModuleRepositoryRequirement repository,
        ModuleRepositoryInitializationState state,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        if (repository.Revision is null)
        {
            return;
        }

        var head = await RepositoryInspector.TryResolveCommitAsync(
            repository.RepositoryPath,
            "HEAD",
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var expected = await RepositoryInspector.TryResolveCommitAsync(
            repository.RepositoryPath,
            repository.Revision,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!RevisionMatchesState(head, expected, state.ResolvedCommit))
        {
            failures.Add(
                $"modules {FormatModules(repository.ModuleNames)} require revision '{repository.Revision}' " +
                $"at '{repository.RepositoryPath}', but HEAD does not match the initialized commit");
        }
    }

    private static bool RevisionMatchesState(string? head, string? expected, string? initialized)
    {
        if (head is null || expected is null)
        {
            return false;
        }

        return MatchesExpectedAndInitializedCommit(head, expected, initialized);
    }

    private static bool MatchesExpectedAndInitializedCommit(
        string head,
        string expected,
        string? initialized) =>
        string.Equals(head, expected, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(head, initialized, StringComparison.OrdinalIgnoreCase);

    private static void AddRequiredPathFailures(
        List<string> failures,
        IEnumerable<ModuleRequiredPath> requiredPaths)
    {
        foreach (var requiredPath in requiredPaths
            .OrderBy(path => path.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path.Path, PathSafety.Comparer))
        {
            AddRequiredPathFailure(failures, requiredPath);
        }
    }

    private static void AddRequiredPathFailure(List<string> failures, ModuleRequiredPath requiredPath)
    {
        if (RequiredPathExists(requiredPath))
        {
            return;
        }

        AddMissingRequiredPathFailure(failures, requiredPath);
    }

    private static bool RequiredPathExists(ModuleRequiredPath requiredPath) =>
        requiredPath.Kind == ModuleRequiredPathKind.File
            ? File.Exists(requiredPath.Path)
            : Directory.Exists(requiredPath.Path);

    private static void AddMissingRequiredPathFailure(
        List<string> failures,
        ModuleRequiredPath requiredPath)
    {
        if (!requiredPath.RequiredOnRun)
        {
            return;
        }

        failures.Add(
            $"module '{requiredPath.ModuleName}' requires {requiredPath.Description} at " +
            $"'{requiredPath.Path}', but it is missing");
    }

    private static void ThrowIfPreflightFailed(
        List<string> failures,
        string appHostPath,
        ILogger? logger)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var initializeCommand = CreateInitializeCommand(appHostPath);
        var exception = new InvalidOperationException(
            "Modular AppHost initialization is incomplete:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Select(failure => $"  - {failure}.")) +
            Environment.NewLine +
            $"Run '{initializeCommand}'.");
        LogFailure(logger, failures.Count, initializeCommand, exception);
        throw exception;
    }

    private static void LogFailure(
        ILogger? logger,
        int failureCount,
        string initializeCommand,
        Exception exception)
    {
        if (logger is not null)
        {
            LogPreflightFailed(logger, failureCount, initializeCommand, exception);
        }
    }

    private static string FormatModules(IEnumerable<string> moduleNames) =>
        string.Join(
            ", ",
            moduleNames
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"'{name}'"));

    private static string FormatStateLocation(string? stateFilePath) =>
        string.IsNullOrWhiteSpace(stateFilePath)
            ? string.Empty
            : $"; expected state at '{stateFilePath}'";
}
