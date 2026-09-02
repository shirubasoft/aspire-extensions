#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleRepositoryInitializationPipelineTests
{
    [Fact]
    public void Planning_finds_git_file_without_invoking_git()
    {
        using var workspace = TemporaryDirectory.Create();
        var gitRoot = Path.Combine(workspace.Path, "consumer");
        var appHost = Path.Combine(gitRoot, "src", "AppHost");
        Directory.CreateDirectory(appHost);
        File.WriteAllText(Path.Combine(gitRoot, ".git"), "gitdir: ../metadata/worktrees/consumer\n");

        var registry = new ModuleRepositoryPlanRegistry(appHost);

        Assert.Equal(gitRoot, registry.AppHostRepositoryRoot);
        Assert.Equal(workspace.Path, registry.SiblingParent);
    }

    [Fact]
    public void Sibling_names_are_remote_specific_and_revision_specific()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var branch = registry.Register(
            "catalog",
            "https://github.com/Acme/Services.git",
            revision: null,
            updateOnInitialize: true).Requirement;
        var revision = registry.Register(
            "orders",
            "git@github.com:acme/services.git",
            revision: "release/2026.08",
            updateOnInitialize: true).Requirement;

        Assert.Equal(workspace.Path, Path.GetDirectoryName(branch.RepositoryPath));
        Assert.Equal("Services", Path.GetFileName(branch.RepositoryPath));
        Assert.Equal(workspace.Path, Path.GetDirectoryName(revision.RepositoryPath));
        Assert.NotEqual(branch.RepositoryPath, revision.RepositoryPath);
        Assert.Contains("-rev-", revision.RepositoryPath, StringComparison.Ordinal);
        Assert.False(revision.UpdateOnInitialize);
    }

    [Fact]
    public void Missing_unpinned_remote_preserves_the_repository_name_for_its_sibling()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var requirement = registry.Register(
            "orders-database",
            "https://github.com/acme/DB-orders_bo.git",
            revision: null,
            updateOnInitialize: true).Requirement;

        Assert.Equal("DB-orders_bo", Path.GetFileName(requirement.RepositoryPath));
    }

    [Fact]
    public void Existing_slug_equivalent_sibling_is_selected_for_an_unpinned_remote()
    {
        using var workspace = CreateGitWorkspace();
        var existingCheckout = Path.Combine(workspace.Path, "Repo_A");
        Directory.CreateDirectory(existingCheckout);
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var requirement = registry.Register(
            "catalog",
            "https://example.test/acme/Repo_A.git",
            revision: null,
            updateOnInitialize: true).Requirement;

        Assert.Equal(existingCheckout, requirement.RepositoryPath);
    }

    [Fact]
    public void Multiple_slug_equivalent_siblings_require_an_explicit_checkout_directory_name()
    {
        using var workspace = CreateGitWorkspace();
        var underscoreCheckout = Path.Combine(workspace.Path, "DB-orders_bo");
        var hyphenCheckout = Path.Combine(workspace.Path, "DB-orders-bo");
        Directory.CreateDirectory(underscoreCheckout);
        Directory.CreateDirectory(hyphenCheckout);
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        const string configurationKey =
            "Aspire:ModularAppHosts:Modules:orders:CheckoutDirectoryName";

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "orders",
            "https://github.com/acme/DB-orders_bo.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryNameConfigurationKey: configurationKey));

        Assert.Contains(underscoreCheckout, exception.Message, StringComparison.Ordinal);
        Assert.Contains(hyphenCheckout, exception.Message, StringComparison.Ordinal);
        Assert.Contains("db-orders-bo", exception.Message, StringComparison.Ordinal);
        Assert.Contains(configurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("CheckoutDirectoryName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_repository_names_with_the_same_slug_require_explicit_distinct_siblings()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var first = registry.Register(
            "orders",
            "https://github.com/acme/DB-orders_bo.git",
            revision: null,
            updateOnInitialize: true).Requirement;

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "billing",
            "https://github.com/acme/DB-orders-bo.git",
            revision: null,
            updateOnInitialize: true));

        Assert.Equal("DB-orders_bo", Path.GetFileName(first.RepositoryPath));
        Assert.Contains("same canonical checkout slug 'db-orders-bo'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Aspire:ModularAppHosts:Modules:orders:CheckoutDirectoryName",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Aspire:ModularAppHosts:Modules:billing:CheckoutDirectoryName",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("explicit distinct CheckoutDirectoryName", exception.Message, StringComparison.Ordinal);

        var explicitRegistry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var underscore = explicitRegistry.Register(
            "orders",
            "https://github.com/acme/DB-orders_bo.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: "orders-underscore").Requirement;
        var hyphen = explicitRegistry.Register(
            "billing",
            "https://github.com/acme/DB-orders-bo.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: "orders-hyphen").Requirement;

        Assert.Equal("orders-underscore", Path.GetFileName(underscore.RepositoryPath));
        Assert.Equal("orders-hyphen", Path.GetFileName(hyphen.RepositoryPath));
    }

    [Fact]
    public void Explicit_checkout_directory_name_wins_over_a_slug_equivalent_sibling()
    {
        using var workspace = CreateGitWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "Repo_A"));
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var requirement = registry.Register(
            "catalog",
            "https://example.test/acme/Repo_A.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: "catalog-source").Requirement;

        Assert.Equal("catalog-source", Path.GetFileName(requirement.RepositoryPath));
    }

    [Fact]
    public void Pinned_remote_ignores_a_slug_equivalent_sibling()
    {
        using var workspace = CreateGitWorkspace();
        var existingCheckout = Path.Combine(workspace.Path, "Repo_A");
        Directory.CreateDirectory(existingCheckout);
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var requirement = registry.Register(
            "catalog",
            "https://example.test/acme/Repo_A.git",
            revision: "v2",
            updateOnInitialize: true).Requirement;

        Assert.NotEqual(existingCheckout, requirement.RepositoryPath);
        Assert.Contains("-rev-", requirement.RepositoryPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Slug_equivalent_sibling_preserves_same_name_repository_collision_diagnostic()
    {
        using var workspace = CreateGitWorkspace();
        var existingCheckout = Path.Combine(workspace.Path, "Repo_A");
        Directory.CreateDirectory(existingCheckout);
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var first = registry.Register(
            "catalog",
            "https://github.com/acme/Repo_A.git",
            revision: null,
            updateOnInitialize: true).Requirement;

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "orders",
            "https://gitlab.example.test/acme/repo-a.git",
            revision: null,
            updateOnInitialize: true));

        Assert.Equal(existingCheckout, first.RepositoryPath);
        Assert.Contains("resolve to the same canonical checkout path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("explicit distinct CheckoutDirectoryName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_remotes_with_the_same_repository_name_require_explicit_distinct_siblings()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var github = registry.Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryNameConfigurationKey:
                "Aspire:ModularAppHosts:Modules:catalog:CheckoutDirectoryName").Requirement;

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "orders",
            "https://gitlab.example.test/acme/services.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryNameConfigurationKey:
                "Aspire:ModularAppHosts:Modules:orders:CheckoutDirectoryName"));

        Assert.Contains(github.NormalizedRepository, exception.Message, StringComparison.Ordinal);
        Assert.Contains("gitlab.example.test/acme/services", exception.Message, StringComparison.Ordinal);
        Assert.Contains(github.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Aspire:ModularAppHosts:Modules:catalog:CheckoutDirectoryName",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Aspire:ModularAppHosts:Modules:orders:CheckoutDirectoryName",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("explicit distinct CheckoutDirectoryName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_names_resolve_same_repository_name_collisions()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var github = registry.Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: "services-github").Requirement;
        var gitlab = registry.Register(
            "orders",
            "https://gitlab.example.test/acme/services.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: "services-gitlab").Requirement;

        Assert.NotEqual(github.NormalizedRepository, gitlab.NormalizedRepository);
        Assert.NotEqual(github.RepositoryPath, gitlab.RepositoryPath);
        Assert.Equal("services-github", Path.GetFileName(github.RepositoryPath));
        Assert.Equal("services-gitlab", Path.GetFileName(gitlab.RepositoryPath));
        Assert.Equal(2, registry.Requirements.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../services")]
    [InlineData("nested/services")]
    [InlineData("nested\\services")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("services*invalid")]
    [InlineData(" services")]
    [InlineData("services ")]
    [InlineData("services.")]
    [InlineData("CON")]
    [InlineData("com1.txt")]
    [InlineData("LPT9")]
    public void Invalid_checkout_directory_names_report_the_value_and_configuration_key(string value)
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        const string configurationKey =
            "Aspire:ModularAppHosts:Modules:catalog:CheckoutDirectoryName";

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: value,
            checkoutDirectoryNameConfigurationKey: configurationKey));

        Assert.Contains(value.Replace("\r", "\\r").Replace("\n", "\\n"), exception.Message, StringComparison.Ordinal);
        Assert.Contains(configurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checkout_directory_name_rejects_filename_segments_longer_than_255_characters()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: null,
            updateOnInitialize: true,
            checkoutDirectoryName: new string('a', 256)));

        Assert.Contains("longer than 255", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pinned_repository_rejects_checkout_directory_name_override()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        const string configurationKey =
            "Aspire:ModularAppHosts:Modules:catalog:CheckoutDirectoryName";

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "catalog",
            "https://github.com/acme/services.git",
            revision: "v2",
            updateOnInitialize: true,
            checkoutDirectoryName: "services-v2",
            checkoutDirectoryNameConfigurationKey: configurationKey));

        Assert.Contains("services-v2", exception.Message, StringComparison.Ordinal);
        Assert.Contains(configurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("pinned repository revision 'v2'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_repository_with_revision_uses_initializer_owned_sibling()
    {
        using var workspace = CreateGitWorkspace();
        var localSource = Path.Combine(workspace.Path, "local-source");
        Directory.CreateDirectory(Path.Combine(localSource, ".git"));
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var requirement = registry.Register(
            "orders",
            localSource,
            revision: "v2",
            updateOnInitialize: true).Requirement;

        Assert.NotEqual(localSource, requirement.RepositoryPath);
        Assert.Equal(workspace.Path, Path.GetDirectoryName(requirement.RepositoryPath));
        Assert.StartsWith("file:", requirement.NormalizedRepository, StringComparison.Ordinal);
        Assert.Contains("-rev-", requirement.RepositoryPath, StringComparison.Ordinal);
        Assert.False(requirement.UpdateOnInitialize);
    }

    [Fact]
    public void Equivalent_remotes_share_one_plan_and_collect_module_names()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var first = registry.Register(
            "catalog",
            "https://build-user:secret@github.com/Acme/Services.git?token=secret",
            revision: null,
            updateOnInitialize: true);
        var second = registry.Register(
            "orders",
            "git@github.com:acme/services.git",
            revision: null,
            updateOnInitialize: true);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Same(first.Requirement, second.Requirement);
        Assert.Equal(2, first.Requirement.ModuleNames.Count);
        Assert.Equal("github.com/acme/services", first.Requirement.NormalizedRepository);
        Assert.Equal("Services", Path.GetFileName(first.Requirement.RepositoryPath));
        Assert.Single(registry.Requirements);
    }

    [Fact]
    public void Shared_checkout_rejects_conflicting_initialization_policy()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var first = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "orders",
            "https://github.com/acme/services.git",
            first.Requirement.RepositoryPath,
            revision: null,
            updateOnInitialize: false));

        Assert.Contains("conflicting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_checkout_must_be_a_direct_sibling()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register(
            "catalog",
            "acme/services",
            Path.Combine(workspace.Path, "nested", "services"),
            revision: null,
            updateOnInitialize: true));

        Assert.Contains("direct sibling", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_exposes_aggregate_and_per_repository_steps()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;

        var aggregate = ModuleRepositoryInitializationPipeline.CreateAggregateStep();
        var repository = ModuleRepositoryInitializationPipeline.CreateRepositoryStep(
            requirement,
            static () => new ModuleRepositoryInitializationSettings(
                "git",
                "gh",
                TimeSpan.FromMinutes(2)));

        Assert.Equal(ModuleRepositoryInitializationPipeline.StepName, aggregate.Name);
        Assert.Equal(
            ModuleRepositoryInitializationPipeline.GetRepositoryStepName(requirement),
            repository.Name);
        Assert.Contains(ModuleRepositoryInitializationPipeline.StepName, repository.RequiredBySteps);
        Assert.Contains(ModuleRepositoryInitializationPipeline.RepositoryStepTag, repository.Tags);
    }

    [Fact]
    public async Task Repository_step_reports_success_after_initialization()
    {
        using var workspace = CreateGitWorkspace();
        var requirement = new ModuleRepositoryPlanRegistry(workspace.AppHostPath).Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var step = ModuleRepositoryInitializationPipeline.CreateRepositoryStep(
            requirement,
            static () => new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(2)),
            static (_, _, _) => Task.CompletedTask);
        var builder = DistributedApplication.CreateBuilder();
        await using var application = builder.Build();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);

        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });
    }

    [Theory]
    [InlineData(nameof(ModuleRepositoryCheckoutOwnership.Created))]
    [InlineData(nameof(ModuleRepositoryCheckoutOwnership.Adopted))]
    public async Task Repository_state_file_round_trips_credential_free_initialization_state(
        string ownershipName)
    {
        var ownership = Enum.Parse<ModuleRepositoryCheckoutOwnership>(ownershipName);
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "https://build-user:secret@github.com/acme/services.git?token=secret",
            revision: null,
            updateOnInitialize: true).Requirement;
        var statePath = Path.Combine(workspace.Path, "modular-apphosts.json");
        using var store = new FileModuleRepositoryStateStore(statePath);
        var state = new ModuleRepositoryInitializationState(
            ModuleRepositoryInitializationState.CurrentSchemaVersion,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            requirement.Revision,
            requirement.ConfigurationFingerprint,
            requirement.NormalizedRepository,
            "0123456789abcdef0123456789abcdef01234567",
            ownership,
            DateTimeOffset.UtcNow);

        await store.WriteAsync(
            requirement,
            state,
            TestContext.Current.CancellationToken);

        var restored = await store.ReadAsync(
            requirement,
            TestContext.Current.CancellationToken);
        var serialized = await File.ReadAllTextAsync(
            statePath,
            TestContext.Current.CancellationToken);

        Assert.Equal(state, restored);
        Assert.True(restored!.Matches(requirement));
        Assert.Contains("\"schemaVersion\": 2", serialized, StringComparison.Ordinal);
        Assert.Contains($"\"ownership\": \"{ownership}\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"repositories\"", serialized, StringComparison.Ordinal);
        Assert.Contains(requirement.StepKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build-user", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repository_state_records_upgrade_inside_the_existing_state_file_envelope()
    {
        using var workspace = CreateGitWorkspace();
        var requirement = new ModuleRepositoryPlanRegistry(workspace.AppHostPath).Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var statePath = Path.Combine(workspace.Path, "modular-apphosts.json");
        var legacyRecord = new
        {
            schemaVersion = 1,
            repository = requirement.NormalizedRepository,
            destination = requirement.RepositoryPath,
            revision = requirement.Revision,
            configurationFingerprint = requirement.ConfigurationFingerprint,
            origin = requirement.NormalizedRepository,
            resolvedCommit = "0123456789abcdef0123456789abcdef01234567",
            initializedAtUtc = DateTimeOffset.UtcNow
        };
        var legacyDocument = new
        {
            schemaVersion = 1,
            repositories = new Dictionary<string, object>
            {
                [requirement.StepKey] = legacyRecord
            }
        };
        await File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(legacyDocument),
            TestContext.Current.CancellationToken);
        using var store = new FileModuleRepositoryStateStore(statePath);
        var expected = CreateState(requirement) with
        {
            Ownership = ModuleRepositoryCheckoutOwnership.Adopted
        };

        await store.WriteAsync(requirement, expected, TestContext.Current.CancellationToken);

        Assert.Equal(
            expected,
            await store.ReadAsync(requirement, TestContext.Current.CancellationToken));
        using var upgradedDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(statePath, TestContext.Current.CancellationToken));
        Assert.Equal(1, upgradedDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        var upgradedRecord = upgradedDocument.RootElement
            .GetProperty("repositories")
            .GetProperty(requirement.StepKey);
        Assert.Equal(2, upgradedRecord.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Adopted", upgradedRecord.GetProperty("ownership").GetString());
    }

    [Fact]
    public async Task Malformed_repository_state_is_treated_as_uninitialized_and_repaired_on_write()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var statePath = Path.Combine(workspace.Path, "modular-apphosts.json");
        await File.WriteAllTextAsync(
            statePath,
            "{not valid JSON",
            TestContext.Current.CancellationToken);
        using var store = new FileModuleRepositoryStateStore(statePath);

        Assert.Null(await store.ReadAsync(
            requirement,
            TestContext.Current.CancellationToken));

        var expected = CreateState(requirement);
        await store.WriteAsync(requirement, expected, TestContext.Current.CancellationToken);

        Assert.Equal(
            expected,
            await store.ReadAsync(requirement, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repository_state_file_preserves_multiple_repository_entries()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var catalog = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var orders = registry.Register(
            "orders",
            "acme/orders",
            revision: "v2",
            updateOnInitialize: true).Requirement;
        using var store = new FileModuleRepositoryStateStore(
            Path.Combine(workspace.Path, "modular-apphosts.json"));
        var catalogState = CreateState(catalog);
        var ordersState = CreateState(orders);

        await store.WriteAsync(catalog, catalogState, TestContext.Current.CancellationToken);
        await store.WriteAsync(orders, ordersState, TestContext.Current.CancellationToken);

        Assert.Equal(
            catalogState,
            await store.ReadAsync(catalog, TestContext.Current.CancellationToken));
        Assert.Equal(
            ordersState,
            await store.ReadAsync(orders, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Legacy_environment_deployment_state_is_not_read()
    {
        using var workspace = CreateGitWorkspace();
        var requirement = new ModuleRepositoryPlanRegistry(workspace.AppHostPath).Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var deploymentDirectory = Path.Combine(workspace.Path, "deployment-state");
        Directory.CreateDirectory(deploymentDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(deploymentDirectory, "production.json"),
            $$"""
            {
              "modular-apphosts:repositories:{{requirement.StepKey}}": "legacy state"
            }
            """,
            TestContext.Current.CancellationToken);
        using var store = new FileModuleRepositoryStateStore(
            Path.Combine(deploymentDirectory, "modular-apphosts.json"));

        Assert.Null(await store.ReadAsync(requirement, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Repository_state_path_is_environment_independent()
    {
        using var workspace = CreateGitWorkspace();
        var statePath = FileModuleRepositoryStateStore.ResolveStateFilePath(
            "apphost-sha",
            workspace.AppHostPath);

        Assert.EndsWith(
            Path.Combine(".aspire", "deployments", "apphost-sha", "modular-apphosts.json"),
            statePath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_initialization_does_not_write_repository_state()
    {
        using var workspace = CreateGitWorkspace();
        var registry = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = registry.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var settings = new ModuleRepositoryInitializationSettings(
            "git",
            "gh",
            TimeSpan.FromMinutes(2));
        var stateStore = new InMemoryModuleRepositoryStateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryInitializationPipeline.InitializeAsync(
                requirement,
                settings,
                NullLogger.Instance,
                (_, _, _, _, _) => throw new InvalidOperationException("clone failed"),
                TestContext.Current.CancellationToken));

        Assert.Null(await stateStore.ReadAsync(requirement, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preflight_aggregates_missing_repositories_and_required_paths()
    {
        using var workspace = CreateGitWorkspace();
        var plans = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = plans.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var missingProject = Path.Combine(requirement.RepositoryPath, "src", "Catalog.csproj");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleRepositoryPreflight.ValidateAsync(
                plans.Requirements,
                [new ModuleRequiredPath(
                    "catalog",
                    "project 'catalog-api'",
                    missingProject,
                    ModuleRequiredPathKind.File)],
                new InMemoryModuleRepositoryStateStore(),
                new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(2)),
                workspace.AppHostPath,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(requirement.RepositoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingProject, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ModuleRepositoryPreflight.CreateInitializeCommand(workspace.AppHostPath), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialization_emits_scoped_structured_repository_operation_events()
    {
        using var workspace = CreateGitWorkspace();
        var plans = new ModuleRepositoryPlanRegistry(workspace.AppHostPath);
        var requirement = plans.Register(
            "catalog",
            "acme/services",
            revision: null,
            updateOnInitialize: true).Requirement;
        var logger = new CapturingLogger();

        await ModuleRepositoryInitializationPipeline.InitializeAsync(
            requirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(2)),
            logger,
            (_, _, _, lifecycle, _) =>
            {
                lifecycle(new RepositorySyncLifecycleEvent("clone", "started"));
                lifecycle(new RepositorySyncLifecycleEvent(
                    "clone",
                    "completed",
                    ElapsedMilliseconds: 12));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        var operations = logger.Entries.Where(entry => entry.EventId.Id == 4).ToArray();
        Assert.Equal(2, operations.Length);
        Assert.Equal("clone", operations[0].State["Operation"]);
        Assert.Equal("started", operations[0].State["State"]);
        Assert.Equal(requirement.RepositoryPath, operations[0].State["RepositoryPath"]);
        Assert.Contains(logger.Scopes, scope => scope.ContainsKey("OperationId"));
        Assert.Contains(logger.Scopes, scope => Equals(scope["RepositoryKind"], "branch"));
    }

    private static GitWorkspace CreateGitWorkspace()
    {
        var temporaryDirectory = TemporaryDirectory.Create();
        var gitRoot = Path.Combine(temporaryDirectory.Path, "consumer");
        var appHostPath = Path.Combine(gitRoot, "src", "AppHost");
        Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
        Directory.CreateDirectory(appHostPath);
        return new GitWorkspace(temporaryDirectory, appHostPath);
    }

    private static ModuleRepositoryInitializationState CreateState(
        ModuleRepositoryRequirement requirement) =>
        new(
            ModuleRepositoryInitializationState.CurrentSchemaVersion,
            requirement.NormalizedRepository,
            requirement.RepositoryPath,
            requirement.Revision,
            requirement.ConfigurationFingerprint,
            requirement.NormalizedRepository,
            "0123456789abcdef0123456789abcdef01234567",
            ModuleRepositoryCheckoutOwnership.Created,
            DateTimeOffset.UtcNow);

    private sealed class GitWorkspace(TemporaryDirectory temporaryDirectory, string appHostPath)
        : IDisposable
    {
        public string Path => temporaryDirectory.Path;

        public string AppHostPath { get; } = appHostPath;

        public void Dispose() => temporaryDirectory.Dispose();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.EventId EventId, Dictionary<string, object?> State)> Entries { get; } = [];

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

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Entries.Add((eventId, values.ToDictionary(pair => pair.Key, pair => pair.Value)));
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
