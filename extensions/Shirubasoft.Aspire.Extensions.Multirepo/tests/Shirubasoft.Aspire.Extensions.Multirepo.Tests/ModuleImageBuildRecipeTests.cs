using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageBuildRecipeTests
{
    [Theory]
    [InlineData("Docker", "docker")]
    [InlineData("Podman", "podman")]
    public void Aspire_runtime_names_are_normalized_for_direct_cli_operations(
        string runtimeName,
        string expectedExecutable)
    {
        Assert.Equal(
            expectedExecutable,
            ModuleImageRecipeOperations.GetContainerRuntimeExecutableName(runtimeName));
    }

    private static readonly ModuleImageSourceState CleanMain = new(
        "main",
        "abcdef012345",
        IsDirty: false,
        StatusFingerprint: "CLEAN");

    [Fact]
    public void Recipe_uses_a_stable_resource_specific_local_alias()
    {
        var recipe = CreateRecipe("acme/orders-api");
        var anotherRecipe = CreateRecipe("acme/orders-worker");

        Assert.Equal("registry.example.test/acme/orders-api:aspire-run", recipe.LocalImageReference);
        Assert.Equal("registry.example.test/acme/orders-worker:aspire-run", anotherRecipe.LocalImageReference);
        Assert.DoesNotContain("main", recipe.LocalImageReference, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", recipe.LocalImageReference, StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_plan_resolves_runtime_source_identity_and_publish_placeholders()
    {
        var options = CreateOptions("acme/orders-api");
        var recipe = CreateRecipe(options: new ModuleImageCommandOptions(
            options.ImageName,
            options.PublishCommand,
            "publish",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            ModuleImageCommandOptions.ImageTagPlaceholder)
        {
            ImageRegistry = options.ImageRegistry,
            ProducedImageReference = "legacy/orders-api:{image-tag}"
        });

        var plan = ModuleImageExecutionPlan.Create(recipe, CleanMain);

        Assert.Equal(
            "registry.example.test/acme/orders-api:main-abcdef012345",
            plan.CanonicalImageReference);
        Assert.Equal("legacy/orders-api:main-abcdef012345", plan.ProducedImageReference);
        Assert.Equal(
            [
                "publish",
                "registry.example.test/acme/orders-api:main-abcdef012345",
                "main-abcdef012345"
            ],
            plan.PublishArguments);
    }

    [Fact]
    public async Task Clean_local_canonical_image_is_reused_and_retagged_to_the_run_alias()
    {
        var operations = new FakeOperations(CleanMain)
        {
            ImageExists = true
        };

        var prepared = await PrepareAsync(CreateRecipe(), operations);

        Assert.Equal(ModuleImagePreparationDisposition.Reused, prepared.Disposition);
        Assert.Equal(
            "registry.example.test/acme/orders-api:main-abcdef012345",
            prepared.CanonicalImageReference);
        Assert.Equal(
            [(prepared.CanonicalImageReference, prepared.LocalImageReference)],
            operations.Tags);
        Assert.Equal(0, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
        Assert.Equal(1, operations.ResolveRuntimeCount);
        Assert.Equal(TimeSpan.FromMinutes(10), operations.ImageExistsTimeout);
        Assert.All(operations.UsedRuntimes, runtime => Assert.Equal("test-runtime", runtime));
    }

    [Fact]
    public async Task Clean_missing_image_is_pulled_before_build_when_enabled()
    {
        var options = CreateOptions();
        options.PullBeforeBuild = true;
        var operations = new FakeOperations(CleanMain)
        {
            PullSucceeds = true
        };

        var prepared = await PrepareAsync(CreateRecipe(options: options), operations);

        Assert.Equal(ModuleImagePreparationDisposition.Pulled, prepared.Disposition);
        Assert.Equal(1, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
        Assert.Single(operations.Tags);
        Assert.Equal(1, operations.ResolveRuntimeCount);
        Assert.Equal(TimeSpan.FromMinutes(10), operations.PullTimeout);
        Assert.All(operations.UsedRuntimes, runtime => Assert.Equal("test-runtime", runtime));
    }

    [Fact]
    public async Task Missing_separate_source_reuses_an_explicit_local_image()
    {
        var options = CreateOptions();
        options.ImageTag = "production";
        options.PullBeforeBuild = true;
        var operations = new FakeOperations((ModuleImageSourceState?)null)
        {
            ImageExists = true
        };

        var prepared = await PrepareAsync(
            CreateRecipe(options: options, allowsUnavailableSource: true),
            operations);

        Assert.Equal(ModuleImagePreparationDisposition.Reused, prepared.Disposition);
        Assert.Equal("registry.example.test/acme/orders-api:production", prepared.CanonicalImageReference);
        Assert.False(prepared.SourceState.IsAvailable);
        Assert.Equal(1, operations.ImageExistsCount);
        Assert.Equal(0, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
        Assert.Equal([(prepared.CanonicalImageReference, prepared.LocalImageReference)], operations.Tags);
    }

    [Fact]
    public async Task Missing_separate_source_pulls_an_explicit_image_without_building()
    {
        var options = CreateOptions();
        options.ImageTag = "production";
        options.PullBeforeBuild = true;
        var operations = new FakeOperations((ModuleImageSourceState?)null)
        {
            PullSucceeds = true
        };

        var prepared = await PrepareAsync(
            CreateRecipe(options: options, allowsUnavailableSource: true),
            operations);

        Assert.Equal(ModuleImagePreparationDisposition.Pulled, prepared.Disposition);
        Assert.False(prepared.SourceState.IsAvailable);
        Assert.Equal(1, operations.ImageExistsCount);
        Assert.Equal(1, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
        Assert.Equal([(prepared.CanonicalImageReference, prepared.LocalImageReference)], operations.Tags);
    }

    [Fact]
    public async Task Failed_pull_with_missing_managed_source_reports_initialize_recovery()
    {
        var options = CreateOptions();
        options.ImageTag = "missing";
        options.PullBeforeBuild = true;
        var operations = new FakeOperations((ModuleImageSourceState?)null);
        var recipe = CreateRecipe(
            options: options,
            allowsUnavailableSource: true,
            initializerOwned: true,
            appHostDirectory: "/work/apphost");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(recipe, operations));

        Assert.Contains(recipe.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            ModuleRepositoryPreflight.CreateInitializeCommand(recipe.AppHostDirectory),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
        Assert.Empty(operations.Tags);
    }

    [Fact]
    public async Task Missing_source_without_the_pull_shortcut_fails_before_image_operations()
    {
        var options = CreateOptions();
        options.ImageTag = "production";
        var operations = new FakeOperations((ModuleImageSourceState?)null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(CreateRecipe(options: options), operations));

        Assert.Contains("Provide the checkout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, operations.ImageExistsCount);
        Assert.Equal(0, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
    }

    [Fact]
    public async Task Missing_source_cannot_use_pull_before_build_without_an_explicit_tag()
    {
        var options = CreateOptions();
        options.PullBeforeBuild = true;
        var operations = new FakeOperations((ModuleImageSourceState?)null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(CreateRecipe(options: options), operations));

        Assert.Contains("Provide the checkout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, operations.ImageExistsCount);
        Assert.Equal(0, operations.PullCount);
        Assert.Equal(0, operations.BuildCount);
    }

    [Fact]
    public async Task Failed_pull_falls_back_to_build_and_retags_legacy_output_then_run_alias()
    {
        var options = CreateOptions();
        options.PullBeforeBuild = true;
        options.ProducedImageReference = "legacy/orders-api:output";
        var operations = new FakeOperations(CleanMain, CleanMain);

        var prepared = await PrepareAsync(CreateRecipe(options: options), operations);

        Assert.Equal(ModuleImagePreparationDisposition.Built, prepared.Disposition);
        Assert.Equal(1, operations.PullCount);
        Assert.Equal(1, operations.BuildCount);
        Assert.Equal(
            [
                ("legacy/orders-api:output", prepared.CanonicalImageReference),
                (prepared.CanonicalImageReference, prepared.LocalImageReference)
            ],
            operations.Tags);
        Assert.Equal(1, operations.ResolveRuntimeCount);
        Assert.All(operations.UsedRuntimes, runtime => Assert.Equal("test-runtime", runtime));
    }

    [Fact]
    public async Task Dirty_source_always_builds_without_image_inspection_or_pull()
    {
        var dirty = CleanMain with
        {
            IsDirty = true,
            StatusFingerprint = "DIRTY"
        };
        var options = CreateOptions();
        options.PullBeforeBuild = true;
        var operations = new FakeOperations(dirty, dirty)
        {
            ImageExists = true,
            PullSucceeds = true
        };

        var prepared = await PrepareAsync(CreateRecipe(options: options), operations);

        Assert.Equal(ModuleImagePreparationDisposition.Built, prepared.Disposition);
        Assert.EndsWith("main-abcdef012345-dirty", prepared.CanonicalImageReference, StringComparison.Ordinal);
        Assert.Equal(0, operations.ImageExistsCount);
        Assert.Equal(0, operations.PullCount);
        Assert.Equal(1, operations.BuildCount);
    }

    [Fact]
    public void Dirty_literal_references_are_not_magically_rewritten()
    {
        var options = CreateOptions();
        options.ImageTag = "candidate";
        options.ProducedImageReference = "registry.example.test/acme/orders-api:candidate";
        var recipe = CreateRecipe(options: new ModuleImageCommandOptions(
            options.ImageName,
            options.PublishCommand,
            "registry.example.test/acme/orders-api:candidate")
        {
            ImageRegistry = options.ImageRegistry,
            ImageTag = options.ImageTag,
            ProducedImageReference = options.ProducedImageReference
        });
        var dirty = CleanMain with { IsDirty = true, StatusFingerprint = "DIRTY" };

        var plan = ModuleImageExecutionPlan.Create(recipe, dirty);

        Assert.Equal(
            "registry.example.test/acme/orders-api:candidate-dirty",
            plan.CanonicalImageReference);
        Assert.Equal(options.ProducedImageReference, plan.ProducedImageReference);
        Assert.Equal([options.ProducedImageReference], plan.PublishArguments);
    }

    [Fact]
    public async Task Optional_refresh_only_updates_clean_unpinned_checkouts_and_uses_refreshed_state()
    {
        var refreshed = new ModuleImageSourceState(
            "main",
            "fedcba987654",
            IsDirty: false,
            StatusFingerprint: "CLEAN");
        var operations = new FakeOperations(CleanMain, refreshed)
        {
            HasUpstream = true,
            ImageExists = true
        };

        var prepared = await PrepareAsync(
            CreateRecipe(refreshCleanCheckout: true),
            operations);

        Assert.Equal(1, operations.HasUpstreamCount);
        Assert.Equal(1, operations.RefreshCount);
        Assert.EndsWith("main-fedcba987654", prepared.CanonicalImageReference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "v1.2.3")]
    public async Task Runtime_refresh_preserves_dirty_and_pinned_checkouts(
        bool dirty,
        string? revision)
    {
        var state = CleanMain with
        {
            IsDirty = dirty,
            StatusFingerprint = dirty ? "DIRTY" : "CLEAN"
        };
        var operations = new FakeOperations(state, state)
        {
            HasUpstream = true,
            ImageExists = true
        };

        await PrepareAsync(
            CreateRecipe(
                refreshCleanCheckout: true,
                revision: revision),
            operations);

        Assert.Equal(0, operations.HasUpstreamCount);
        Assert.Equal(0, operations.RefreshCount);
    }

    [Fact]
    public async Task Build_fails_before_retagging_when_source_changes_during_the_command()
    {
        var changed = CleanMain with { StatusFingerprint = "CHANGED" };
        var operations = new FakeOperations(CleanMain, changed);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(CreateRecipe(), operations));

        Assert.Contains("Build inputs changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, operations.BuildCount);
        Assert.Empty(operations.Tags);
    }

    [Fact]
    public async Task Command_output_is_written_unchanged_only_to_the_resource_logger()
    {
        var operations = new FakeOperations(CleanMain, CleanMain);
        var lifecycleLogger = new RecordingLogger();
        var resourceLogger = new RecordingLogger();

        await ModuleImageRecipeEvaluator.PrepareAsync(
            CreateRecipe(),
            lifecycleLogger,
            resourceLogger,
            operations,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            lifecycleLogger.Messages,
            message => message.Contains("build output", StringComparison.Ordinal));
        Assert.Contains(
            resourceLogger.Messages,
            message => message.Contains("build output", StringComparison.Ordinal));
        Assert.Contains(
            resourceLogger.Messages,
            message => message.Contains("user:secret", StringComparison.Ordinal) ||
                message.Contains("token=secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_output_is_written_unchanged_only_to_the_resource_logger()
    {
        var options = CreateOptions();
        options.PullBeforeBuild = true;
        var operations = new FakeOperations(CleanMain)
        {
            PullSucceeds = true
        };
        var lifecycleLogger = new RecordingLogger();
        var resourceLogger = new RecordingLogger();

        await ModuleImageRecipeEvaluator.PrepareAsync(
            CreateRecipe(options: options),
            lifecycleLogger,
            resourceLogger,
            operations,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            lifecycleLogger.Messages,
            message => message.Contains("pull output", StringComparison.Ordinal));
        Assert.Contains(
            resourceLogger.Messages,
            message => message.Contains("pull output", StringComparison.Ordinal) &&
                message.Contains("token=secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retag_completion_includes_elapsed_time()
    {
        var operations = new FakeOperations(CleanMain)
        {
            ImageExists = true
        };
        var logger = new RecordingLogger();

        await ModuleImageRecipeEvaluator.PrepareAsync(
            CreateRecipe(),
            logger,
            logger,
            operations,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            logger.Messages,
            message => message.StartsWith("Tagged image ", StringComparison.Ordinal) &&
                message.Contains(" in ", StringComparison.Ordinal) &&
                message.EndsWith(" ms.", StringComparison.Ordinal));
        var retag = Assert.Single(
            logger.Entries,
            entry => string.Equals(
                entry.EventId.Name,
                "LogRetagCompleted",
                StringComparison.Ordinal));
        Assert.True(Assert.IsType<double>(retag.Properties["ElapsedMilliseconds"]) >= 0);
    }

    [Fact]
    public async Task Publisher_annotation_caches_one_in_flight_preparation_without_blocking()
    {
        var recipe = CreateRecipe();
        var completion = new TaskCompletionSource<ModulePreparedImage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var publisher = new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            (_, _, _, _) =>
            {
                calls++;
                return completion.Task;
            });

        var first = publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var second = publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.False(first.IsCompleted);

        var prepared = CreatePreparedImage(recipe, CleanMain);
        completion.SetResult(prepared);
        Assert.Same(prepared, await first);
        Assert.Same(prepared, await second);
        Assert.True(publisher.TryGetPreparedImage(out var cached));
        Assert.Same(prepared, cached);
    }

    [Fact]
    public async Task Publisher_annotation_caller_cancellation_does_not_cancel_shared_preparation()
    {
        var recipe = CreateRecipe();
        var completion = new TaskCompletionSource<ModulePreparedImage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var publisher = new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            (_, _, _, operationToken) =>
            {
                Assert.False(operationToken.CanBeCanceled);
                calls++;
                return completion.Task;
            });
        using var callerCancellation = new CancellationTokenSource();

        var canceledWait = publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            callerCancellation.Token);
        var successfulWait = publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await callerCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        Assert.False(successfulWait.IsCompleted);
        var prepared = CreatePreparedImage(recipe, CleanMain);
        completion.SetResult(prepared);

        Assert.Same(prepared, await successfulWait);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Publisher_annotation_retries_after_a_canceled_shared_preparation()
    {
        var recipe = CreateRecipe();
        var prepared = CreatePreparedImage(recipe, CleanMain);
        var calls = 0;
        var publisher = new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            (_, _, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromCanceled<ModulePreparedImage>(new CancellationToken(canceled: true))
                    : Task.FromResult(prepared);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken));
        var retried = await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Same(prepared, retried);
    }

    [Fact]
    public async Task Publisher_annotation_retries_after_a_failed_preparation()
    {
        var recipe = CreateRecipe();
        var prepared = CreatePreparedImage(recipe, CleanMain);
        var calls = 0;
        var publisher = new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            (_, _, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<ModulePreparedImage>(new InvalidOperationException("first attempt failed"))
                    : Task.FromResult(prepared);
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken));
        var retried = await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal("first attempt failed", exception.Message);
        Assert.Equal(2, calls);
        Assert.Same(prepared, retried);
    }

    private static Task<ModulePreparedImage> PrepareAsync(
        ModuleImageBuildRecipe recipe,
        IModuleImageRecipeOperations operations) =>
        ModuleImageRecipeEvaluator.PrepareAsync(
            recipe,
            NullLogger.Instance,
            NullLogger.Instance,
            operations,
            TestContext.Current.CancellationToken);

    private static ModuleImageBuildRecipe CreateRecipe(
        string imageName = "acme/orders-api",
        ModuleImageCommandOptions? options = null,
        bool refreshCleanCheckout = false,
        string? revision = null,
        bool allowsUnavailableSource = false,
        bool initializerOwned = false,
        string? appHostDirectory = null) =>
        new(
            new ModuleImageRecipeIdentity("orders", "orders-api"),
            new ModuleImageRepositorySettings(
                "/work/orders",
                "/work/orders/src",
                "https://example.test/acme/orders.git",
                revision,
                refreshCleanCheckout,
                "git",
                "gh",
                TimeSpan.FromMinutes(2),
                AppHostDirectory: appHostDirectory,
                InitializerOwned: initializerOwned,
                AllowsUnavailableSource: allowsUnavailableSource),
            new ModuleImageCommandSettings(
                options ?? CreateOptions(imageName),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));

    private static ModuleImageCommandOptions CreateOptions(string imageName = "acme/orders-api") =>
        new(imageName, "build-image", "publish", ModuleImageCommandOptions.ImageReferencePlaceholder)
        {
            ImageRegistry = "registry.example.test"
        };

    private static ModulePreparedImage CreatePreparedImage(
        ModuleImageBuildRecipe recipe,
        ModuleImageSourceState sourceState)
    {
        var executionPlan = ModuleImageExecutionPlan.Create(recipe, sourceState);
        return new ModulePreparedImage(
            executionPlan.CanonicalImageReference,
            recipe.LocalImageReference,
            sourceState,
            ModuleImagePreparationDisposition.Built);
    }

    private sealed class FakeOperations : IModuleImageRecipeOperations
    {
        private readonly Queue<ModuleImageSourceState?> _sourceStates;
        private ModuleImageSourceState? _lastSourceState;

        public FakeOperations(params ModuleImageSourceState?[] sourceStates)
        {
            Assert.NotEmpty(sourceStates);
            _sourceStates = new Queue<ModuleImageSourceState?>(sourceStates);
            _lastSourceState = sourceStates[^1];
        }

        public bool ImageExists { get; init; }

        public bool PullSucceeds { get; init; }

        public bool HasUpstream { get; init; }

        public int ImageExistsCount { get; private set; }

        public int PullCount { get; private set; }

        public int BuildCount { get; private set; }

        public int HasUpstreamCount { get; private set; }

        public int RefreshCount { get; private set; }

        public int ResolveRuntimeCount { get; private set; }

        public TimeSpan? ImageExistsTimeout { get; private set; }

        public TimeSpan? PullTimeout { get; private set; }

        public List<(string Source, string Target)> Tags { get; } = [];

        public List<string> UsedRuntimes { get; } = [];

        public Task<string> ResolveContainerRuntimeAsync(CancellationToken cancellationToken)
        {
            ResolveRuntimeCount++;
            return Task.FromResult("test-runtime");
        }

        public Task<ModuleImageSourceState?> TryCaptureSourceStateAsync(
            ModuleImageBuildRecipe recipe,
            CancellationToken cancellationToken)
        {
            if (_sourceStates.TryDequeue(out var state))
            {
                _lastSourceState = state;
            }

            return Task.FromResult(_lastSourceState);
        }

        public Task<bool> HasUpstreamAsync(
            ModuleImageBuildRecipe recipe,
            CancellationToken cancellationToken)
        {
            HasUpstreamCount++;
            return Task.FromResult(HasUpstream);
        }

        public Task RefreshRepositoryAsync(
            ModuleImageBuildRecipe recipe,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            RefreshCount++;
            progress("fetch completed with token=secret");
            return Task.CompletedTask;
        }

        public Task<bool> ImageExistsAsync(
            string containerRuntime,
            string imageReference,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ImageExistsCount++;
            ImageExistsTimeout = timeout;
            UsedRuntimes.Add(containerRuntime);
            return Task.FromResult(ImageExists);
        }

        public Task<bool> PullImageAsync(
            string containerRuntime,
            string imageReference,
            TimeSpan timeout,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            PullCount++;
            PullTimeout = timeout;
            UsedRuntimes.Add(containerRuntime);
            progress("pull output with https://user:secret@example.test/path?token=secret");
            return Task.FromResult(PullSucceeds);
        }

        public Task BuildImageAsync(
            ModuleImageBuildRecipe recipe,
            ModuleImageExecutionPlan plan,
            string containerRuntime,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            BuildCount++;
            UsedRuntimes.Add(containerRuntime);
            progress("build output with https://user:secret@example.test/path?token=secret");
            return Task.CompletedTask;
        }

        public Task TagImageAsync(
            ModuleImageBuildRecipe recipe,
            string containerRuntime,
            string sourceImageReference,
            string targetImageReference,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            UsedRuntimes.Add(containerRuntime);
            Tags.Add((sourceImageReference, targetImageReference));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public List<RecordedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new RecordedLogEntry(eventId, properties));
        }
    }

    private sealed record RecordedLogEntry(
        EventId EventId,
        IReadOnlyDictionary<string, object?> Properties);
}
