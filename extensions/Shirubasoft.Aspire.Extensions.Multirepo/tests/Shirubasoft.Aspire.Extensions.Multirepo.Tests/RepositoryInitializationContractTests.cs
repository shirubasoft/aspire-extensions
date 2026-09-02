#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class RepositoryInitializationContractTests
{
    [Fact]
    public async Task Missing_canonical_checkout_is_created_and_preserves_created_ownership()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "catalog-source");
        var firstCommit = await InitializeRepositoryAsync(sourcePath, "first");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        const string repository = "https://example.test/acme/Repo_A.git";
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "catalog",
            repository,
            revision: null,
            updateOnInitialize: true).Requirement;
        var stateStore = new InMemoryModuleRepositoryStateStore();
        var settings = new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1));
        var synchronizationCalls = 0;

        async Task SynchronizeCreatedAsync(
            ModuleRepositoryRequirement planned,
            ModuleRepositoryInitializationSettings _,
            Action<string> __,
            Action<RepositorySyncLifecycleEvent> ___,
            CancellationToken ____)
        {
            synchronizationCalls++;
            if (!Directory.Exists(planned.RepositoryPath))
            {
                await RunGitAsync(
                    Path.GetDirectoryName(planned.RepositoryPath)!,
                    "clone",
                    sourcePath,
                    planned.RepositoryPath);
                await RunGitAsync(planned.RepositoryPath, "remote", "set-url", "origin", repository);
                return;
            }

            await RunGitAsync(planned.RepositoryPath, "fetch", sourcePath, "HEAD");
            await RunGitAsync(planned.RepositoryPath, "merge", "--ff-only", "FETCH_HEAD");
        }

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            SynchronizeCreatedAsync);

        Assert.Equal("Repo_A", Path.GetFileName(requirement.RepositoryPath));
        Assert.Equal(firstCommit, await ReadGitAsync(requirement.RepositoryPath, "rev-parse", "HEAD"));
        var firstState = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Created, firstState!.Ownership);

        var secondCommit = await CommitAsync(sourcePath, "second");
        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            SynchronizeCreatedAsync);

        Assert.Equal(2, synchronizationCalls);
        Assert.Equal(secondCommit, await ReadGitAsync(requirement.RepositoryPath, "rev-parse", "HEAD"));
        var repeatedState = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Created, repeatedState!.Ownership);
    }

    [Fact]
    public async Task Deleted_adopted_checkout_is_recreated_with_created_ownership()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "orders-source");
        var sourceCommit = await InitializeRepositoryAsync(sourcePath, "first");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        const string repository = "https://example.test/acme/DB-orders.git";
        var legacyCheckout = Path.Combine(workspace.Path, "db-orders");
        await RunGitAsync(
            workspace.Path,
            "clone",
            sourcePath,
            legacyCheckout);
        await RunGitAsync(legacyCheckout, "remote", "set-url", "origin", repository);
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "orders",
            repository,
            revision: null,
            updateOnInitialize: true).Requirement;
        Assert.Equal(legacyCheckout, requirement.RepositoryPath);
        var statePath = Path.Combine(workspace.Path, "state", "modular-apphosts.json");
        using var stateStore = new FileModuleRepositoryStateStore(statePath);
        var settings = new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1));

        static Task FailIfSynchronizedAsync(
            ModuleRepositoryRequirement _,
            ModuleRepositoryInitializationSettings __,
            Action<string> ___,
            Action<RepositorySyncLifecycleEvent> ____,
            CancellationToken _____) =>
            throw new InvalidOperationException("An adopted checkout must not be synchronized.");

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            FailIfSynchronizedAsync);
        var adoptedState = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Adopted, adoptedState!.Ownership);

        TemporaryDirectory.DeleteRecursively(requirement.RepositoryPath);
        var recreatedRequirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "orders",
            repository,
            revision: null,
            updateOnInitialize: true).Requirement;
        Assert.Equal(requirement.StepKey, recreatedRequirement.StepKey);
        Assert.Equal("DB-orders", Path.GetFileName(recreatedRequirement.RepositoryPath));
        var synchronizationCalls = 0;
        async Task RecloneAsync(
            ModuleRepositoryRequirement planned,
            ModuleRepositoryInitializationSettings _,
            Action<string> __,
            Action<RepositorySyncLifecycleEvent> ___,
            CancellationToken ____)
        {
            synchronizationCalls++;
            await RunGitAsync(
                Path.GetDirectoryName(planned.RepositoryPath)!,
                "clone",
                sourcePath,
                planned.RepositoryPath);
            await RunGitAsync(planned.RepositoryPath, "remote", "set-url", "origin", repository);
        }

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            recreatedRequirement,
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            RecloneAsync);

        Assert.Equal(1, synchronizationCalls);
        Assert.Equal(sourceCommit, await ReadGitAsync(recreatedRequirement.RepositoryPath, "rev-parse", "HEAD"));
        var recreatedState = await stateStore.ReadAsync(
            recreatedRequirement,
            TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Created, recreatedState!.Ownership);
        Assert.Contains(
            "\"ownership\": \"Created\"",
            await File.ReadAllTextAsync(statePath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matching_canonical_checkout_is_adopted_and_never_synchronized_even_when_dirty()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "orders-source");
        var firstCommit = await InitializeRepositoryAsync(sourcePath, "first");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        const string repository = "https://example.test/acme/orders.git";
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "orders",
            repository,
            revision: null,
            updateOnInitialize: true).Requirement;
        await RunGitAsync(
            Path.GetDirectoryName(requirement.RepositoryPath)!,
            "clone",
            sourcePath,
            requirement.RepositoryPath);
        await RunGitAsync(requirement.RepositoryPath, "remote", "set-url", "origin", repository);
        _ = await CommitAsync(sourcePath, "upstream-second");
        var stateStore = new InMemoryModuleRepositoryStateStore();
        var settings = new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1));

        static Task FailIfSynchronizedAsync(
            ModuleRepositoryRequirement _,
            ModuleRepositoryInitializationSettings __,
            Action<string> ___,
            Action<RepositorySyncLifecycleEvent> ____,
            CancellationToken _____) =>
            throw new InvalidOperationException("An adopted checkout must not be synchronized.");

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            FailIfSynchronizedAsync);

        Assert.Equal(firstCommit, await ReadGitAsync(requirement.RepositoryPath, "rev-parse", "HEAD"));
        var adoptedState = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Adopted, adoptedState!.Ownership);

        var dirtyPath = Path.Combine(requirement.RepositoryPath, "developer-note.txt");
        await File.WriteAllTextAsync(
            dirtyPath,
            "developer-owned",
            TestContext.Current.CancellationToken);
        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            FailIfSynchronizedAsync);

        Assert.Equal(firstCommit, await ReadGitAsync(requirement.RepositoryPath, "rev-parse", "HEAD"));
        Assert.True(File.Exists(dirtyPath));
        var repeatedState = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Adopted, repeatedState!.Ownership);
    }

    [Fact]
    public async Task Matching_slug_equivalent_checkout_is_adopted_without_synchronization()
    {
        using var workspace = TemporaryDirectory.Create();
        var existingCheckout = Path.Combine(workspace.Path, "repo-a");
        var existingCommit = await InitializeRepositoryAsync(existingCheckout, "developer-checkout");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        const string repository = "https://example.test/acme/Repo_A.git";
        await RunGitAsync(existingCheckout, "remote", "add", "origin", repository);
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "catalog",
            repository,
            revision: null,
            updateOnInitialize: true).Requirement;
        var stateStore = new InMemoryModuleRepositoryStateStore();

        static Task FailIfSynchronizedAsync(
            ModuleRepositoryRequirement _,
            ModuleRepositoryInitializationSettings __,
            Action<string> ___,
            Action<RepositorySyncLifecycleEvent> ____,
            CancellationToken _____) =>
            throw new InvalidOperationException("An adopted checkout must not be synchronized.");

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            FailIfSynchronizedAsync);

        Assert.Equal(existingCheckout, requirement.RepositoryPath);
        Assert.Equal(existingCommit, await ReadGitAsync(existingCheckout, "rev-parse", "HEAD"));
        var state = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleRepositoryCheckoutOwnership.Adopted, state!.Ownership);
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "Repo_A")));
    }

    [Fact]
    public async Task Canonical_checkout_with_mismatched_origin_fails_without_a_hashed_fallback()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        const string repository = "https://example.test/acme/catalog.git";
        const string configurationKey =
            "Aspire:ModularAppHosts:Modules:catalog:CheckoutDirectoryName";
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "catalog",
            repository,
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryNameConfigurationKey: configurationKey).Requirement;
        await InitializeRepositoryAsync(requirement.RepositoryPath, "conflicting");
        await RunGitAsync(
            requirement.RepositoryPath,
            "remote",
            "add",
            "origin",
            "https://other-user:secret@example.test/other/catalog.git?token=secret");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
                requirement,
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                new InMemoryModuleRepositoryStateStore(),
                reportingStep: null,
                TestContext.Current.CancellationToken));

        Assert.Contains(requirement.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(requirement.NormalizedRepository, exception.Message, StringComparison.Ordinal);
        Assert.Contains("example.test/other/catalog", exception.Message, StringComparison.Ordinal);
        Assert.Contains(configurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("distinct sibling name", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(
            Directory.EnumerateDirectories(workspace.Path),
            path => PathSafety.AreEqual(path, requirement.RepositoryPath));
    }

    [Fact]
    public async Task Initialization_clones_the_planned_revision_and_emits_structured_events()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "orders-source");
        var firstCommit = await InitializeRepositoryAsync(sourcePath, "first");
        var secondCommit = await CommitAsync(sourcePath, "second");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var requirement = plans.Register(
            "orders",
            sourcePath,
            revision: firstCommit,
            updateOnInitialize: true).Requirement;
        var logger = new CapturingLogger();
        var stateStore = new InMemoryModuleRepositoryStateStore();

        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            logger,
            logger,
            stateStore,
            reportingStep: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(sourcePath, requirement.RepositoryPath);
        Assert.Equal(workspace.Path, Path.GetDirectoryName(requirement.RepositoryPath));
        Assert.False(requirement.UpdateOnInitialize);
        Assert.Equal(firstCommit, await ReadGitAsync(requirement.RepositoryPath, "rev-parse", "HEAD"));
        Assert.Equal(string.Empty, await ReadGitAsync(requirement.RepositoryPath, "branch", "--show-current"));
        var state = await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken);
        Assert.NotNull(state);
        Assert.True(state.Matches(requirement));
        Assert.Equal(firstCommit, state.ResolvedCommit, ignoreCase: true);

        await ModuleRepositoryPreflight.ValidateAsync(
            [requirement],
            [],
            stateStore,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            appHostPath,
            cancellationToken: TestContext.Current.CancellationToken);

        await RunGitAsync(requirement.RepositoryPath, "checkout", "--detach", secondCommit);
        var staleCommit = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                [requirement],
                [],
                stateStore,
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HEAD does not match", staleCommit.Message, StringComparison.Ordinal);

        await RunGitAsync(requirement.RepositoryPath, "checkout", "--detach", firstCommit);
        await RunGitAsync(requirement.RepositoryPath, "remote", "set-url", "origin", "https://example.test/other.git");
        var wrongOrigin = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                [requirement],
                [],
                stateStore,
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("checkout origin differs", wrongOrigin.Message, StringComparison.Ordinal);

        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 3);
        var operations = logger.Entries
            .Where(entry => entry.EventId.Id == 4)
            .Select(entry => (entry.State["Operation"], entry.State["State"]))
            .ToArray();
        Assert.Equal(
            [
                ("clone", "started"),
                ("clone", "completed"),
                ("fetch", "started"),
                ("fetch", "completed"),
                ("checkout", "started"),
                ("checkout", "completed"),
                ("submodule-update", "started"),
                ("submodule-update", "completed")
            ],
            operations);
        Assert.Contains(
            logger.Scopes,
            scope => Equals(scope["RepositoryKind"], "revision") &&
                Equals(scope["RepositoryPath"], requirement.RepositoryPath));
    }

    [Fact]
    public async Task Synchronization_fast_forwards_a_clean_checkout_with_an_upstream()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "catalog-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "catalog-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        var oldCommit = await ReadGitAsync(checkoutPath, "rev-parse", "HEAD");
        var newCommit = await CommitAsync(sourcePath, "second");
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        await SynchronizeAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            lifecycle.Add);

        Assert.NotEqual(oldCommit, newCommit);
        Assert.Equal(newCommit, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Equal("second", await File.ReadAllTextAsync(
            Path.Combine(checkoutPath, "content.txt"),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            [
                ("fast-forward", "started", (string?)null),
                ("fast-forward", "completed", (string?)null),
                ("submodule-update", "started", (string?)null),
                ("submodule-update", "completed", (string?)null)
            ],
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
    }

    [Fact]
    public async Task Synchronization_warns_and_preserves_a_checkout_when_its_remote_no_longer_exists()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "removed-source");
        var commit = await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "preserved-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        Directory.Move(sourcePath, Path.Combine(workspace.Path, "removed-source-backup"));
        var lifecycle = new List<RepositorySyncLifecycleEvent>();
        var progress = new List<string>();

        await RepositorySynchronizer.SynchronizeAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            TestContext.Current.CancellationToken,
            commandTimeout: TimeSpan.FromMinutes(1),
            progress: progress.Add,
            lifecycle: lifecycle.Add);

        Assert.Equal(commit, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Contains(progress, message =>
            message.Contains("Warning:", StringComparison.Ordinal) &&
            message.Contains("remote no longer exists", StringComparison.Ordinal));
        Assert.Equal(
            [
                ("fast-forward", "started", (string?)null, false),
                ("fast-forward", "skipped", "remote-missing", true),
                ("submodule-update", "started", (string?)null, false),
                ("submodule-update", "completed", (string?)null, false)
            ],
            lifecycle.Select(entry =>
                (entry.Operation, entry.State, entry.Reason, entry.IsWarning)).ToArray());
    }

    [Fact]
    public async Task Synchronization_still_fails_when_a_fast_forward_is_not_possible()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "diverged-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "diverged-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        await CommitAsync(sourcePath, "remote-change");
        await RunGitAsync(checkoutPath, "config", "user.name", "Modular AppHosts Tests");
        await RunGitAsync(checkoutPath, "config", "user.email", "tests@example.test");
        await CommitAsync(checkoutPath, "local-change");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true));

        Assert.Contains("exit code", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true, "dirty")]
    [InlineData(false, false, "disabled")]
    public async Task Synchronization_preserves_a_checkout_when_updating_is_not_allowed(
        bool makeDirty,
        bool updateRepository,
        string expectedReason)
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "inventory-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "inventory-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        var checkoutCommit = await ReadGitAsync(checkoutPath, "rev-parse", "HEAD");
        await CommitAsync(sourcePath, "upstream-change");
        if (makeDirty)
        {
            await File.WriteAllTextAsync(
                Path.Combine(checkoutPath, "content.txt"),
                "local-change",
                TestContext.Current.CancellationToken);
        }

        var lifecycle = new List<RepositorySyncLifecycleEvent>();
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository, lifecycle.Add);

        Assert.Equal(checkoutCommit, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        var expectedLifecycle = makeDirty
            ? [("update", "skipped", expectedReason)]
            : new[]
            {
                ("update", "skipped", expectedReason),
                ("submodule-update", "started", (string?)null),
                ("submodule-update", "completed", (string?)null)
            };
        Assert.Equal(
            expectedLifecycle,
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
        Assert.Equal(
            makeDirty ? "local-change" : "first",
            await File.ReadAllTextAsync(
                Path.Combine(checkoutPath, "content.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Synchronization_reports_a_clean_checkout_without_an_upstream()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "standalone");
        var commit = await InitializeRepositoryAsync(repositoryPath, "first");
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        await SynchronizeAsync(
            repositoryPath,
            repository: null,
            updateRepository: true,
            lifecycle.Add);

        Assert.Equal(commit, await ReadGitAsync(repositoryPath, "rev-parse", "HEAD"));
        Assert.Equal(
            [
                ("update", "skipped", "no-upstream"),
                ("submodule-update", "started", (string?)null),
                ("submodule-update", "completed", (string?)null)
            ],
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
    }

    [Fact]
    public async Task Repository_inspection_wrappers_report_roots_and_read_only_git_state()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "inspected");
        var commit = await InitializeRepositoryAsync(repositoryPath, "first");
        var nestedPath = Path.Combine(repositoryPath, "src", "AppHost");
        Directory.CreateDirectory(nestedPath);
        var projectPath = Path.Combine(nestedPath, "AppHost.csproj");
        var branch = await ReadGitAsync(repositoryPath, "branch", "--show-current");

        Assert.Equal(repositoryPath, RepositoryIdentity.FindRepositoryRoot(projectPath));
        Assert.Equal(repositoryPath, RepositoryIdentity.TryFindRepositoryRoot(projectPath));
        Assert.Equal(repositoryPath, await RepositoryInspector.FindRepositoryRootAsync(
            projectPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(repositoryPath, await RepositoryInspector.TryFindRepositoryRootAsync(
            projectPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(branch, await RepositoryInspector.TryGetBranchAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(commit[..12], await RepositoryInspector.TryGetCommitAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(commit, await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryResolveCommitAsync(
            repositoryPath,
            "missing-revision",
            cancellationToken: TestContext.Current.CancellationToken));

        await RunGitAsync(repositoryPath, "checkout", "--detach", "HEAD");
        Assert.Null(await RepositoryInspector.TryGetBranchAsync(
            repositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));

        var nonRepositoryPath = Path.Combine(workspace.Path, "not-a-repository");
        Directory.CreateDirectory(nonRepositoryPath);
        var missingFile = Path.Combine(nonRepositoryPath, "Missing.csproj");
        Assert.Equal(nonRepositoryPath, RepositoryIdentity.FindRepositoryRoot(missingFile));
        Assert.Null(RepositoryIdentity.TryFindRepositoryRoot(missingFile));
        Assert.Equal(nonRepositoryPath, await RepositoryInspector.FindRepositoryRootAsync(
            missingFile,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryFindRepositoryRootAsync(
            missingFile,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryInspector.TryGetCommitAsync(
            nonRepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await RepositoryInspector.IsDirtyAsync(
            nonRepositoryPath,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Required_inspection_explains_an_unusable_git_executable()
    {
        using var workspace = TemporaryDirectory.Create();
        var repositoryPath = Path.Combine(workspace.Path, "checkout");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        var missingGit = $"missing-git-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositoryInspector.IsGitRepositoryAsync(
                repositoryPath,
                gitExecutablePath: missingGit,
                requireSuccessfulInspection: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(repositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingGit, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ModularAppHostsOptions.GitExecutablePath), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_command_wrapper_returns_the_first_command_or_null()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "wrapper-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "wrapper-checkout");

        var clone = await RepositorySynchronizer.CreateCommandAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(clone);
        Assert.Equal("clone", clone.Operation);
        Assert.Equal("git", clone.Executable);
        Assert.Equal(
            ["clone", "--recurse-submodules", "--", sourcePath, checkoutPath],
            clone.Arguments);

        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        var submoduleUpdate = await RepositorySynchronizer.CreateCommandAsync(
            checkoutPath,
            sourcePath,
            updateRepository: false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(submoduleUpdate);
        Assert.Equal("submodule-update", submoduleUpdate.Operation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronization_rejects_a_missing_or_mismatched_origin(bool addMismatchedOrigin)
    {
        using var workspace = TemporaryDirectory.Create();
        var expectedSource = Path.Combine(workspace.Path, "expected-source");
        await InitializeRepositoryAsync(expectedSource, "expected");
        var checkoutPath = Path.Combine(workspace.Path, "checkout");
        await InitializeRepositoryAsync(checkoutPath, "checkout");
        string? mismatchedSource = null;
        if (addMismatchedOrigin)
        {
            mismatchedSource = Path.Combine(workspace.Path, "other-source");
            await InitializeRepositoryAsync(mismatchedSource, "other");
            await RunGitAsync(checkoutPath, "remote", "add", "origin", mismatchedSource);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                expectedSource,
                updateRepository: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(
            RepositoryIdentity.NormalizeRepositoryIdentity(expectedSource, workspace.Path),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            mismatchedSource is null
                ? "(missing or unavailable)"
                : RepositoryIdentity.NormalizeRepositoryIdentity(mismatchedSource, workspace.Path),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_mismatch_errors_report_normalized_origins_without_credentials()
    {
        using var workspace = TemporaryDirectory.Create();
        var checkoutPath = Path.Combine(workspace.Path, "checkout");
        await InitializeRepositoryAsync(checkoutPath, "checkout");
        const string actualRepository =
            "https://actual-user:actual-password@example.test/acme/actual.git?auth=actual-query";
        const string expectedRepository =
            "https://expected-user:expected-password@example.test/acme/expected.git?auth=expected-query";
        await RunGitAsync(checkoutPath, "remote", "add", "origin", actualRepository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                expectedRepository,
                updateRepository: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("example.test/acme/actual", exception.Message, StringComparison.Ordinal);
        Assert.Contains("example.test/acme/expected", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("actual-user", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("actual-password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-user", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("auth=", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_non_git_directory_rejects_a_configured_remote_without_credentials()
    {
        using var workspace = TemporaryDirectory.Create();
        var checkoutPath = Path.Combine(workspace.Path, "checkout");
        Directory.CreateDirectory(checkoutPath);
        await File.WriteAllTextAsync(
            Path.Combine(checkoutPath, "existing.txt"),
            "developer-owned",
            TestContext.Current.CancellationToken);
        const string repository =
            "https://expected-user:expected-password@example.test/acme/expected.git?auth=expected-query";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                repository,
                updateRepository: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("example.test/acme/expected", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-user", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("auth=", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dirty_checkout_at_the_pinned_revision_is_preserved()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "pinned-source");
        var revision = await InitializeRepositoryAsync(sourcePath, "first");
        var checkoutPath = Path.Combine(workspace.Path, "pinned-checkout");
        await SynchronizeAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            revision: revision);
        await File.WriteAllTextAsync(
            Path.Combine(checkoutPath, "content.txt"),
            "local-change",
            TestContext.Current.CancellationToken);
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        var commands = await RepositorySynchronizer.CreateCommandsAsync(
            checkoutPath,
            sourcePath,
            updateRepository: true,
            revision: revision,
            lifecycle: lifecycle.Add,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(commands);
        Assert.Equal(revision, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Equal(
            [("update", "skipped", "dirty")],
            lifecycle.Select(entry => (entry.Operation, entry.State, entry.Reason)).ToArray());
    }

    [Fact]
    public async Task Dirty_checkout_at_a_different_revision_fails_before_checkout()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "changed-source");
        var pinnedRevision = await InitializeRepositoryAsync(sourcePath, "first");
        var currentRevision = await CommitAsync(sourcePath, "second");
        var checkoutPath = Path.Combine(workspace.Path, "changed-checkout");
        await SynchronizeAsync(checkoutPath, sourcePath, updateRepository: true);
        await File.WriteAllTextAsync(
            Path.Combine(checkoutPath, "content.txt"),
            "local-change",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.CreateCommandsAsync(
                checkoutPath,
                sourcePath,
                updateRepository: true,
                revision: pinnedRevision,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(currentRevision, await ReadGitAsync(checkoutPath, "rev-parse", "HEAD"));
        Assert.Contains(pinnedRevision, exception.Message, StringComparison.Ordinal);
        Assert.Contains("local changes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_clone_reports_the_origin_error_without_a_completed_event()
    {
        using var workspace = TemporaryDirectory.Create();
        var missingSource = Path.Combine(workspace.Path, "missing-source");
        var checkoutPath = Path.Combine(workspace.Path, "failed-checkout");
        var lifecycle = new List<RepositorySyncLifecycleEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RepositorySynchronizer.SynchronizeAsync(
                checkoutPath,
                missingSource,
                updateRepository: true,
                TestContext.Current.CancellationToken,
                commandTimeout: TimeSpan.FromMinutes(1),
                lifecycle: lifecycle.Add));

        Assert.Contains(checkoutPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [("clone", "started")],
            lifecycle.Select(entry => (entry.Operation, entry.State)).ToArray());
    }

    [Fact]
    public async Task Initialization_fails_when_repository_state_does_not_round_trip()
    {
        using var workspace = TemporaryDirectory.Create();
        var sourcePath = Path.Combine(workspace.Path, "catalog-source");
        await InitializeRepositoryAsync(sourcePath, "first");
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "catalog",
            sourcePath,
            revision: null,
            updateOnInitialize: true).Requirement;
        var statePath = Path.Combine(workspace.Path, "state", "modular-apphosts.json");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
                requirement,
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                new DiscardingModuleRepositoryStateStore(statePath),
                reportingStep: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("could not be verified", exception.Message, StringComparison.Ordinal);
        Assert.Contains(statePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_names_the_fixed_state_file_for_missing_and_stale_state()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var requirement = new ModuleRepositoryPlanRegistry(appHostPath).Register(
            "catalog",
            "https://example.test/acme/catalog.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        await InitializeRepositoryAsync(requirement.RepositoryPath, "first");
        var statePath = Path.Combine(workspace.Path, "state", "modular-apphosts.json");
        using var stateStore = new FileModuleRepositoryStateStore(statePath);
        var settings = new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                [requirement],
                [],
                stateStore,
                settings,
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("no initialization state", missing.Message, StringComparison.Ordinal);
        Assert.Contains($"expected state at '{statePath}'", missing.Message, StringComparison.Ordinal);

        await stateStore.WriteAsync(
            requirement,
            new ModuleRepositoryInitializationState(
                ModuleRepositoryInitializationState.CurrentSchemaVersion,
                requirement.NormalizedRepository,
                requirement.RepositoryPath,
                requirement.Revision,
                "stale-configuration-fingerprint",
                requirement.NormalizedRepository,
                "0123456789abcdef0123456789abcdef01234567",
                ModuleRepositoryCheckoutOwnership.Created,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                [requirement],
                [],
                stateStore,
                settings,
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not match", stale.Message, StringComparison.Ordinal);
        Assert.Contains($"expected state at '{statePath}'", stale.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_logs_aggregate_failure_details_and_the_exact_initialize_command()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var requirement = plans.Register(
            "payments",
            "https://example.test/acme/payments.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var missingProject = Path.Combine(requirement.RepositoryPath, "src", "Payments.csproj");
        var logger = new CapturingLogger();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                plans.Requirements,
                [new ModuleRequiredPath(
                    "payments",
                    "project 'payments-api'",
                    missingProject,
                    ModuleRequiredPathKind.File)],
                new InMemoryModuleRepositoryStateStore(),
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                logger,
                TestContext.Current.CancellationToken));

        var failure = Assert.Single(logger.Entries, entry => entry.EventId.Id == 10);
        Assert.Equal(2, failure.State["FailureCount"]);
        var initializeCommand = ModuleRepositoryPreflight.CreateInitializeCommand(appHostPath);
        Assert.Equal(initializeCommand, failure.State["InitializeCommand"]);
        Assert.Contains(initializeCommand, exception.Message, StringComparison.Ordinal);
        Assert.Contains(requirement.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingProject, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_allows_missing_source_optional_repositories_and_paths()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var requirement = plans.Register(
            "payments/image",
            "https://example.test/acme/payments-image.git",
            revision: null,
            updateOnInitialize: true,
            requiredOnRun: false).Requirement;
        var missingBuildDirectory = Path.Combine(requirement.RepositoryPath, "src");

        await ModuleRepositoryPreflight.ValidateAsync(
            plans.Requirements,
            [new ModuleRequiredPath(
                "payments",
                "image build directory for resource 'payments-api'",
                missingBuildDirectory,
                ModuleRequiredPathKind.Directory,
                RequiredOnRun: false)],
            new InMemoryModuleRepositoryStateStore(),
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            appHostPath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(requirement.RepositoryPath));
    }

    [Fact]
    public async Task Preflight_ignores_an_invalid_present_source_optional_repository_without_inspection()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var requirement = plans.Register(
            "payments/image",
            "https://example.test/acme/payments-image.git",
            revision: null,
            updateOnInitialize: true,
            requiredOnRun: false).Requirement;
        Directory.CreateDirectory(requirement.RepositoryPath);

        await ModuleRepositoryPreflight.ValidateAsync(
            plans.Requirements,
            [],
            new ThrowingModuleRepositoryStateStore(
                Path.Combine(workspace.Path, "state-that-must-not-be-read.json")),
            new ModuleRepositoryInitializationSettings(
                "git-that-must-not-run",
                "gh-that-must-not-run",
                TimeSpan.FromMinutes(1)),
            appHostPath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(requirement.RepositoryPath));
    }

    [Fact]
    public async Task Shared_optional_repository_becomes_mandatory_when_any_consumer_requires_it()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = CreateAppHostRepository(workspace.Path);
        var plans = new ModuleRepositoryPlanRegistry(appHostPath);
        var optional = plans.Register(
            "payments/image",
            "https://example.test/acme/shared.git",
            revision: null,
            updateOnInitialize: true,
            requiredOnRun: false).Requirement;
        var required = plans.Register(
            "payments",
            "https://example.test/acme/shared.git",
            revision: null,
            updateOnInitialize: true,
            requiredOnRun: true).Requirement;

        Assert.Same(optional, required);
        Assert.True(required.RequiredOnRun);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                plans.Requirements,
                [],
                new InMemoryModuleRepositoryStateStore(),
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
                appHostPath,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(required.RepositoryPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialization_routes_lifecycle_to_pipeline_logs_and_raw_output_only_to_the_resource()
    {
        using var workspace = TemporaryDirectory.Create();
        var plans = new ModuleRepositoryPlanRegistry(CreateAppHostRepository(workspace.Path));
        var requirement = plans.Register(
            "payments",
            "https://example.test/acme/payments.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var lifecycleLogger = new CapturingLogger();
        var resourceLogger = new CapturingLogger();

        await ModuleRepositoryInitializationPipeline.InitializeAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            lifecycleLogger,
            resourceLogger,
            (_, _, progress, lifecycle, _) =>
            {
                progress("clone output");
                lifecycle(new RepositorySyncLifecycleEvent("clone", "started"));
                lifecycle(new RepositorySyncLifecycleEvent(
                    "clone",
                    "completed",
                    ElapsedMilliseconds: 12));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 4, 4, 3], lifecycleLogger.Entries.Select(entry => entry.EventId.Id));
        Assert.Equal([2], resourceLogger.Entries.Select(entry => entry.EventId.Id));
        Assert.DoesNotContain(lifecycleLogger.Entries, entry => entry.EventId.Id == 2);
        Assert.Contains(resourceLogger.Entries, entry =>
            entry.EventId.Id == 2 && Equals(entry.State["Output"], "clone output"));
        Assert.Contains(lifecycleLogger.Scopes, scope => scope.ContainsKey("OperationId"));
        Assert.Contains(resourceLogger.Scopes, scope => scope.ContainsKey("OperationId"));
    }

    [Fact]
    public async Task Initialization_logs_repository_lifecycle_warnings_at_warning_level()
    {
        using var workspace = TemporaryDirectory.Create();
        var plans = new ModuleRepositoryPlanRegistry(CreateAppHostRepository(workspace.Path));
        var requirement = plans.Register(
            "payments",
            "https://example.test/acme/payments.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var logger = new CapturingLogger();

        await ModuleRepositoryInitializationPipeline.InitializeAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            logger,
            (_, _, _, lifecycle, _) =>
            {
                lifecycle(new RepositorySyncLifecycleEvent(
                    "fast-forward",
                    "skipped",
                    "remote-missing",
                    IsWarning: true));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var warning = Assert.Single(logger.Entries, entry => entry.EventId.Id == 7);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("fast-forward", warning.State["Operation"]);
        Assert.Equal("remote-missing", warning.State["Reason"]);
    }

    private static string CreateAppHostRepository(string workspacePath)
    {
        var repositoryPath = Path.Combine(workspacePath, "consumer");
        var appHostPath = Path.Combine(repositoryPath, "src", "AppHost");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        Directory.CreateDirectory(appHostPath);
        return appHostPath;
    }

    private static async Task<string> InitializeRepositoryAsync(string repositoryPath, string contents)
    {
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, "init");
        await RunGitAsync(repositoryPath, "config", "user.name", "Modular AppHosts Tests");
        await RunGitAsync(repositoryPath, "config", "user.email", "tests@example.test");
        return await CommitAsync(repositoryPath, contents);
    }

    private static async Task<string> CommitAsync(string repositoryPath, string contents)
    {
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "content.txt"),
            contents,
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "content.txt");
        await RunGitAsync(repositoryPath, "commit", "-m", contents);
        return await ReadGitAsync(repositoryPath, "rev-parse", "HEAD");
    }

    private static async Task SynchronizeAsync(
        string repositoryPath,
        string? repository,
        bool updateRepository,
        Action<RepositorySyncLifecycleEvent>? lifecycle = null,
        string? revision = null) =>
        await RepositorySynchronizer.SynchronizeAsync(
            repositoryPath,
            repository,
            updateRepository,
            TestContext.Current.CancellationToken,
            revision: revision,
            commandTimeout: TimeSpan.FromMinutes(1),
            lifecycle: lifecycle);

    private static async Task RunGitAsync(string repositoryPath, params string[] arguments)
    {
        await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(repositoryPath)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadGitAsync(string repositoryPath, params string[] arguments)
    {
        var result = await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(repositoryPath)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
        return result.StandardOutput.Trim();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, Dictionary<string, object?> State)> Entries { get; } = [];

        public List<Dictionary<string, object?>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Entries.Add((logLevel, eventId, values.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
