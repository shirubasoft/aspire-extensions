#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class ModuleRepositoryInitializationSettings
{
    public ModuleRepositoryInitializationSettings(
        string gitExecutablePath,
        string githubCliPath,
        TimeSpan commandTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubCliPath);
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                commandTimeout,
                "The repository command timeout must be positive.");
        }

        GitExecutablePath = gitExecutablePath;
        GitHubCliPath = githubCliPath;
        CommandTimeout = commandTimeout;
    }

    public string GitExecutablePath { get; }

    public string GitHubCliPath { get; }

    public TimeSpan CommandTimeout { get; }
}

internal static class ModuleRepositoryInitializationPipeline
{
    internal const string StepName = "initialize";
    internal const string RepositoryStepTag = "initialize-module-repository";
    internal const string SkippedRepositoryStepTag = "initialize-module-repository-skipped";
    internal const string RepositoryContentNotRequiredTag = "repository-content-not-required";

    private sealed class InitializationPipelineRegistration;

    private static readonly Action<ILogger, string, string, string, Exception?> LogInitializationStarted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogInitializationStarted)),
            "Initializing repository {Repository} at {RepositoryPath} for modules {Modules}.");

    private static readonly Action<ILogger, string, string, Exception?> LogInitializationProgress =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(LogInitializationProgress)),
            "Repository {Repository}: {Output}");

    private static readonly Action<ILogger, string, string, double, Exception?> LogInitializationCompleted =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(3, nameof(LogInitializationCompleted)),
            "Initialized repository {Repository} at {RepositoryPath} in {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, string, string, string, double, Exception?> LogRepositoryOperation =
        LoggerMessage.Define<string, string, string, string, double>(
            LogLevel.Information,
            new EventId(4, nameof(LogRepositoryOperation)),
            "Repository operation {Operation} {State} for {Repository} at {RepositoryPath}. " +
            "Elapsed: {ElapsedMilliseconds} ms.");

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogRepositoryOperationSkipped =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Information,
            new EventId(5, nameof(LogRepositoryOperationSkipped)),
            "Repository operation {Operation} {State} for {Repository} at {RepositoryPath}: {Reason}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogRepositoryInitializationSkipped =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(6, nameof(LogRepositoryInitializationSkipped)),
            "Module {Module} declares repository {Repository}, but no repository initialization step was planned: " +
            "{Reason}.");

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogRepositoryOperationWarning =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(7, nameof(LogRepositoryOperationWarning)),
            "Repository operation {Operation} {State} for {Repository} at {RepositoryPath}: {Reason}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(InitializationPipelineRegistration)))
        {
            return;
        }

        builder.Services.AddSingleton<InitializationPipelineRegistration>();
        builder.Pipeline.AddStep(CreateAggregateStep());
    }

    public static void AddRepositoryStep(
        IDistributedApplicationBuilder builder,
        ModuleRepositoryRequirement requirement,
        Func<ModuleRepositoryInitializationSettings> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Pipeline.AddStep(CreateRepositoryStep(requirement, settingsFactory));
    }

    public static void AddSkippedRepositoryStep(
        IDistributedApplicationBuilder builder,
        string moduleName,
        string repository)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        Configure(builder);
        builder.Pipeline.AddStep(CreateSkippedRepositoryStep(
            moduleName.Trim(),
            repository.Trim(),
            builder.AppHostDirectory));
    }

    internal static PipelineStep CreateAggregateStep() => new()
    {
        Name = StepName,
        Description = "Initializes all prerequisites required by the AppHost.",
        Action = _ => Task.CompletedTask
    };

    internal static PipelineStep CreateRepositoryStep(
        ModuleRepositoryRequirement requirement,
        Func<ModuleRepositoryInitializationSettings> settingsFactory) =>
        CreateRepositoryStep(requirement, settingsFactory, InitializeRepositoryStepAsync);

    internal static PipelineStep CreateRepositoryStep(
        ModuleRepositoryRequirement requirement,
        Func<ModuleRepositoryInitializationSettings> settingsFactory,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            PipelineStepContext,
            Task> initializeAsync)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settingsFactory);
        ArgumentNullException.ThrowIfNull(initializeAsync);
        return new PipelineStep
        {
            Name = GetRepositoryStepName(requirement),
            Description =
                $"Initializes repository {requirement.NormalizedRepository} for " +
                $"{string.Join(", ", requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase))}.",
            Action = context => ExecuteRepositoryStepAsync(
                requirement,
                settingsFactory,
                initializeAsync,
                context),
            RequiredBySteps = [StepName],
            Tags = [RepositoryStepTag]
        };
    }

    private static async Task ExecuteRepositoryStepAsync(
        ModuleRepositoryRequirement requirement,
        Func<ModuleRepositoryInitializationSettings> settingsFactory,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            PipelineStepContext,
            Task> initializeAsync,
        PipelineStepContext context)
    {
        var task = await context.ReportingStep.CreateTaskAsync(
            $"Initialize {requirement.NormalizedRepository}",
            context.CancellationToken).ConfigureAwait(false);
        await using var configuredTask = task.ConfigureAwait(false);
        try
        {
            await initializeAsync(requirement, settingsFactory(), context).ConfigureAwait(false);
            await task.SucceedAsync(
                $"Initialized at {requirement.RepositoryPath}",
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await task.FailAsync(exception.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static Task InitializeRepositoryStepAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        PipelineStepContext context) =>
        InitializeAndRecordAsync(
            requirement,
            settings,
            context.Logger,
            context.Logger,
            context.Services.GetRequiredService<IModuleRepositoryStateStore>(),
            context.ReportingStep,
            context.CancellationToken);

    internal static PipelineStep CreateSkippedRepositoryStep(
        string moduleName,
        string repository,
        string appHostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        var normalizedRepository = RepositoryIdentity.NormalizeRepositoryIdentity(
            repository,
            appHostDirectory);
        var repositorySlug = RepositoryIdentity.GetCanonicalCheckoutSlug(
            repository,
            appHostDirectory);
        var stepKey = RepositoryIdentity.GetStepKey(
            normalizedRepository,
            revision: moduleName,
            $"{moduleName}-{repositorySlug}");
        const string reason = "the module's resources do not require repository content";
        return new PipelineStep
        {
            Name = $"skip-initialize-{stepKey}",
            Description =
                $"Module '{moduleName}' declares repository '{normalizedRepository}', but no repository " +
                $"initialization step was planned because {reason}.",
            Action = context =>
            {
                LogRepositoryInitializationSkipped(
                    context.Logger,
                    moduleName,
                    normalizedRepository,
                    reason,
                    null);
                return Task.CompletedTask;
            },
            RequiredBySteps = [StepName],
            Tags = [SkippedRepositoryStepTag, RepositoryContentNotRequiredTag]
        };
    }

    internal static string GetRepositoryStepName(ModuleRepositoryRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return $"initialize-{requirement.StepKey}";
    }

    internal static Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger logger,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            requirement,
            settings,
            logger,
            logger,
            static (plannedRepository, initializationSettings, progress, lifecycle, token) =>
                RepositorySynchronizer.SynchronizeAsync(
                    plannedRepository.RepositoryPath,
                    plannedRepository.Repository,
                    plannedRepository.UpdateOnInitialize,
                    token,
                    plannedRepository.Revision,
                    initializationSettings.GitExecutablePath,
                    initializationSettings.GitHubCliPath,
                    initializationSettings.CommandTimeout,
                    progress,
                    lifecycle),
            cancellationToken);

    internal static async Task InitializeAndRecordAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IModuleRepositoryStateStore stateStore,
        IReportingStep? reportingStep,
        CancellationToken cancellationToken,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task>? synchronizeAsync = null)
    {
        var previousState = await stateStore.ReadAsync(
            requirement,
            cancellationToken).ConfigureAwait(false);
        var checkoutExists = PathExists(requirement.RepositoryPath);
        var ownership = ClassifyOwnership(requirement, previousState, checkoutExists);
        await ValidateCanonicalCheckoutIfNeededAsync(
            requirement,
            settings,
            checkoutExists,
            cancellationToken).ConfigureAwait(false);
        await SynchronizeIfOwnedAsync(
            requirement,
            settings,
            lifecycleLogger,
            resourceLogger,
            reportingStep,
            ownership,
            synchronizeAsync,
            cancellationToken).ConfigureAwait(false);

        await RecordStateAsync(
            requirement,
            settings,
            stateStore,
            ownership,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task ValidateCanonicalCheckoutIfNeededAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        bool checkoutExists,
        CancellationToken cancellationToken)
    {
        if (!requirement.UsesCanonicalCheckout)
        {
            return Task.CompletedTask;
        }

        return checkoutExists
            ? ValidateCanonicalCheckoutAsync(requirement, settings, cancellationToken)
            : Task.CompletedTask;
    }

    private static async Task SynchronizeIfOwnedAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IReportingStep? reportingStep,
        ModuleRepositoryCheckoutOwnership ownership,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task>? synchronizeAsync,
        CancellationToken cancellationToken)
    {
        if (ownership == ModuleRepositoryCheckoutOwnership.Adopted)
        {
            LogAdoptedCheckout(lifecycleLogger, requirement);
            return;
        }

        await SynchronizeAsync(
            requirement,
            settings,
            lifecycleLogger,
            resourceLogger,
            reportingStep,
            synchronizeAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private static void LogAdoptedCheckout(
        ILogger lifecycleLogger,
        ModuleRepositoryRequirement requirement) =>
        LogRepositoryOperationSkipped(
            lifecycleLogger,
            "synchronize",
            "skipped",
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            "adopted-developer-checkout",
            null);

    private static Task SynchronizeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IReportingStep? reportingStep,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task>? synchronizeAsync,
        CancellationToken cancellationToken)
    {
        if (synchronizeAsync is null)
        {
            return InitializeAsync(
                requirement,
                settings,
                lifecycleLogger,
                resourceLogger,
                reportingStep,
                cancellationToken);
        }

        return InitializeAsync(
            requirement,
            settings,
            lifecycleLogger,
            resourceLogger,
            synchronizeAsync,
            cancellationToken);
    }

    internal static Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        IReportingStep? reportingStep,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            requirement,
            settings,
            lifecycleLogger,
            resourceLogger,
            (plannedRepository, initializationSettings, progress, lifecycle, token) =>
                RepositorySynchronizer.SynchronizeAsync(
                    plannedRepository.RepositoryPath,
                    plannedRepository.Repository,
                    plannedRepository.UpdateOnInitialize,
                    token,
                    plannedRepository.Revision,
                    initializationSettings.GitExecutablePath,
                    initializationSettings.GitHubCliPath,
                    initializationSettings.CommandTimeout,
                    progress,
                    lifecycle,
                    reportingStep),
            cancellationToken);

    internal static async Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger logger,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task> synchronizeAsync,
        CancellationToken cancellationToken)
        => await InitializeAsync(
            requirement,
            settings,
            logger,
            logger,
            synchronizeAsync,
            cancellationToken).ConfigureAwait(false);

    internal static async Task InitializeAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        ILogger lifecycleLogger,
        ILogger resourceLogger,
        Func<
            ModuleRepositoryRequirement,
            ModuleRepositoryInitializationSettings,
            Action<string>,
            Action<RepositorySyncLifecycleEvent>,
            CancellationToken,
            Task> synchronizeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(lifecycleLogger);
        ArgumentNullException.ThrowIfNull(resourceLogger);
        ArgumentNullException.ThrowIfNull(synchronizeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var operationId = Guid.NewGuid().ToString("N");
        var scopeState = new Dictionary<string, object?>
        {
            ["OperationId"] = operationId,
            ["Repository"] = requirement.NormalizedRepository,
            ["RepositoryPath"] = requirement.RepositoryPath,
            ["RepositoryKind"] = requirement.Revision is null ? "branch" : "revision",
            ["Modules"] = requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        using var lifecycleScope = lifecycleLogger.BeginScope(scopeState);
        using var resourceScope = ReferenceEquals(lifecycleLogger, resourceLogger)
            ? null
            : resourceLogger.BeginScope(scopeState);
        var modules = string.Join(", ", requirement.ModuleNames.Order(StringComparer.OrdinalIgnoreCase));
        LogInitializationStarted(
            lifecycleLogger,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            modules,
            null);
        var stopwatch = Stopwatch.StartNew();
        await synchronizeAsync(
            requirement,
            settings,
            progress => LogInitializationProgress(
                resourceLogger,
                requirement.NormalizedRepository,
                progress,
                null),
            lifecycle => LogRepositoryLifecycle(lifecycleLogger, requirement, lifecycle),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        LogInitializationCompleted(
            lifecycleLogger,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            stopwatch.Elapsed.TotalMilliseconds,
            null);
    }

    private static void LogRepositoryLifecycle(
        ILogger lifecycleLogger,
        ModuleRepositoryRequirement requirement,
        RepositorySyncLifecycleEvent lifecycle)
    {
        if (lifecycle.IsWarning)
        {
            LogRepositoryWarning(lifecycleLogger, requirement, lifecycle);
            return;
        }

        if (string.IsNullOrWhiteSpace(lifecycle.Reason))
        {
            LogRepositoryCompleted(lifecycleLogger, requirement, lifecycle);
            return;
        }

        LogRepositorySkipped(lifecycleLogger, requirement, lifecycle);
    }

    private static void LogRepositoryWarning(
        ILogger logger,
        ModuleRepositoryRequirement requirement,
        RepositorySyncLifecycleEvent lifecycle) =>
        LogRepositoryOperationWarning(
            logger,
            lifecycle.Operation,
            lifecycle.State,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            lifecycle.Reason ?? "warning",
            null);

    private static void LogRepositoryCompleted(
        ILogger logger,
        ModuleRepositoryRequirement requirement,
        RepositorySyncLifecycleEvent lifecycle) =>
        LogRepositoryOperation(
            logger,
            lifecycle.Operation,
            lifecycle.State,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            lifecycle.ElapsedMilliseconds,
            null);

    private static void LogRepositorySkipped(
        ILogger logger,
        ModuleRepositoryRequirement requirement,
        RepositorySyncLifecycleEvent lifecycle) =>
        LogRepositoryOperationSkipped(
            logger,
            lifecycle.Operation,
            lifecycle.State,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            lifecycle.Reason!,
            null);

    internal static async Task RecordStateAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryCheckoutOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(stateStore);
        var (origin, resolvedCommit) = await InspectRepositoryAsync(
            requirement,
            settings,
            cancellationToken).ConfigureAwait(false);
        var normalizedOrigin = ValidateRecordedOrigin(requirement, origin);

        var expectedState = new ModuleRepositoryInitializationState(
            ModuleRepositoryInitializationState.CurrentSchemaVersion,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            requirement.Revision,
            requirement.ConfigurationFingerprint,
            normalizedOrigin,
            resolvedCommit,
            ownership,
            DateTimeOffset.UtcNow);
        await PersistAndVerifyStateAsync(
            requirement,
            stateStore,
            expectedState,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Origin, string Commit)> InspectRepositoryAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        var origin = await RepositoryInspector.TryGetRemoteAsync(
            requirement.RepositoryPath,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var commit = await RepositoryInspector.TryResolveCommitAsync(
            requirement.RepositoryPath,
            "HEAD",
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (origin is null)
        {
            throw CreateInspectionFailure(requirement);
        }

        return commit is null
            ? throw CreateInspectionFailure(requirement)
            : (origin, commit);
    }

    private static InvalidOperationException CreateInspectionFailure(
        ModuleRepositoryRequirement requirement) =>
        new($"Repository '{requirement.RepositoryPath}' could not be inspected after initialization.");

    private static string ValidateRecordedOrigin(
        ModuleRepositoryRequirement requirement,
        string origin)
    {
        var normalizedOrigin = RepositoryIdentity.NormalizeRepositoryIdentity(
            origin,
            requirement.RepositoryPath);
        if (!string.Equals(
                normalizedOrigin,
                requirement.NormalizedRepository,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repository '{requirement.RepositoryPath}' has origin '{normalizedOrigin}' after initialization; " +
                $"expected '{requirement.NormalizedRepository}'.");
        }

        return normalizedOrigin;
    }

    private static async Task PersistAndVerifyStateAsync(
        ModuleRepositoryRequirement requirement,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationState expectedState,
        CancellationToken cancellationToken)
    {
        await stateStore.WriteAsync(requirement, expectedState, cancellationToken).ConfigureAwait(false);
        var persistedState = await stateStore.ReadAsync(requirement, cancellationToken).ConfigureAwait(false);
        VerifyPersistedState(requirement, stateStore, expectedState, persistedState);
    }

    private static void VerifyPersistedState(
        ModuleRepositoryRequirement requirement,
        IModuleRepositoryStateStore stateStore,
        ModuleRepositoryInitializationState expectedState,
        ModuleRepositoryInitializationState? persistedState)
    {
        if (persistedState == expectedState)
        {
            if (persistedState.Matches(requirement))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Repository initialization state for '{requirement.RepositoryPath}' could not be verified" +
            $"{FormatStateLocation(stateStore.StateFilePath)} after it was written.");
    }

    private static string FormatStateLocation(string? stateFilePath) =>
        string.IsNullOrWhiteSpace(stateFilePath) ? string.Empty : $" at '{stateFilePath}'";

    private static ModuleRepositoryCheckoutOwnership ClassifyOwnership(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState? previousState,
        bool checkoutExists)
    {
        if (!requirement.UsesCanonicalCheckout)
        {
            return ModuleRepositoryCheckoutOwnership.Created;
        }

        if (!checkoutExists)
        {
            return ModuleRepositoryCheckoutOwnership.Created;
        }

        return GetExistingCheckoutOwnership(requirement, previousState);
    }

    private static ModuleRepositoryCheckoutOwnership GetExistingCheckoutOwnership(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState? previousState)
    {
        if (previousState is null)
        {
            return ModuleRepositoryCheckoutOwnership.Adopted;
        }

        return previousState.RefersTo(requirement)
            ? previousState.Ownership
            : ModuleRepositoryCheckoutOwnership.Adopted;
    }

    private static async Task ValidateCanonicalCheckoutAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        var isGitRepository = await InspectCanonicalCheckoutAsync(
            requirement,
            settings,
            cancellationToken).ConfigureAwait(false);
        ValidateCanonicalGitCheckout(requirement, isGitRepository);
        await ValidateCanonicalOriginAsync(requirement, settings, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> InspectCanonicalCheckoutAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RepositoryInspector.IsGitRepositoryAsync(
                requirement.RepositoryPath,
                settings.GitExecutablePath,
                settings.CommandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                CreateCanonicalCheckoutConflictException(
                    requirement,
                    actualOrigin: null,
                    $"the canonical checkout could not be inspected with Git executable " +
                    $"'{settings.GitExecutablePath}'").Message,
                exception);
        }
    }

    private static void ValidateCanonicalGitCheckout(
        ModuleRepositoryRequirement requirement,
        bool isGitRepository)
    {
        if (!isGitRepository)
        {
            throw CreateCanonicalCheckoutConflictException(
                requirement,
                actualOrigin: null,
                "the canonical directory exists but is not a Git checkout");
        }
    }

    private static async Task ValidateCanonicalOriginAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings settings,
        CancellationToken cancellationToken)
    {
        var origin = await RepositoryInspector.TryGetRemoteAsync(
            requirement.RepositoryPath,
            settings.GitExecutablePath,
            settings.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var normalizedOrigin = TryNormalizeOrigin(origin, requirement.RepositoryPath);
        if (!string.Equals(
                normalizedOrigin,
                requirement.NormalizedRepository,
                StringComparison.Ordinal))
        {
            throw CreateCanonicalCheckoutConflictException(
                requirement,
                normalizedOrigin,
                origin is null
                    ? "the checkout has no origin"
                    : "the checkout origin does not match the planned repository");
        }
    }

    private static InvalidOperationException CreateCanonicalCheckoutConflictException(
        ModuleRepositoryRequirement requirement,
        string? actualOrigin,
        string reason)
    {
        var configurationKeys = string.Join(
            ", ",
            requirement.CheckoutDirectoryNameConfigurationKeys
                .Order(StringComparer.Ordinal)
                .Select(key => $"'{key}'"));
        var actual = actualOrigin is null
            ? string.Empty
            : $" Actual normalized origin: '{actualOrigin}'.";
        return new InvalidOperationException(
            $"Canonical checkout conflict at '{requirement.RepositoryPath}': {reason}. " +
            $"Expected normalized repository identity: '{requirement.NormalizedRepository}'.{actual} " +
            $"Choose a distinct sibling name with CheckoutDirectoryName at configuration key(s) " +
            $"{configurationKeys}; initialization will not clone to a hashed fallback.");
    }

    private static string? TryNormalizeOrigin(string? origin, string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        try
        {
            return RepositoryIdentity.NormalizeRepositoryIdentity(origin, repositoryPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

}
