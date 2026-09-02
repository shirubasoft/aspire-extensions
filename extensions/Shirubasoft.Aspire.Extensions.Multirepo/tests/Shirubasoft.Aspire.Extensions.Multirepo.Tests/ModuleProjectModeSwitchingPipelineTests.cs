#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001
#pragma warning disable ASPIREFILESYSTEM001

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleProjectModeSwitchingPipelineTests
{
    [Fact]
    public void Pipeline_reports_declared_remote_repository_that_no_resource_can_use()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = Path.Combine(workspace.Path, "consumer");
        Directory.CreateDirectory(Path.Combine(appHostPath, ".git"));
        var pipeline = new CapturingPipeline();
        var builder = new TestBuilder(
            CreateBuilder(appHostPath, ["--publisher", "manifest"]),
            pipeline,
            new TestUserSecretsManager(Path.Combine(appHostPath, "secrets.json")));
        builder.ExportModule("notifications", module =>
        {
            module.WithRepository("https://example.test/acme/notifications.git");
            module.AddContainer("notifications-api", "busybox", "1.37");
        });

        builder.ImportModule("notifications");

        var diagnostic = Assert.Single(pipeline.Steps, step =>
            step.Tags.Contains(ModuleRepositoryInitializationPipeline.SkippedRepositoryStepTag));
        Assert.Contains("notifications", diagnostic.Name, StringComparison.Ordinal);
        Assert.Contains("notifications", diagnostic.Description, StringComparison.Ordinal);
        Assert.Contains("example.test/acme/notifications", diagnostic.Description, StringComparison.Ordinal);
        Assert.Contains("do not require repository content", diagnostic.Description, StringComparison.Ordinal);
        Assert.Contains(
            ModuleRepositoryInitializationPipeline.RepositoryContentNotRequiredTag,
            diagnostic.Tags);
        Assert.Contains(ModuleRepositoryInitializationPipeline.StepName, diagnostic.RequiredBySteps);
    }

    [Fact]
    public void Pipeline_does_not_report_repository_as_skipped_when_a_build_repository_plans_it()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = Path.Combine(workspace.Path, "consumer");
        Directory.CreateDirectory(Path.Combine(appHostPath, ".git"));
        var pipeline = new CapturingPipeline();
        var builder = new TestBuilder(
            CreateBuilder(appHostPath, ["--publisher", "manifest"]),
            pipeline,
            new TestUserSecretsManager(Path.Combine(appHostPath, "secrets.json")));
        const string repository = "https://example.test/acme/notifications.git";
        builder.ExportModule("notifications", module =>
        {
            module.WithRepository(repository);
            module.AddContainer("notifications-api", "notifications-api")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "notifications-api",
                    "publisher",
                    "build")
                {
                    BuildRepository = repository
                });
        });

        builder.ImportModule("notifications");

        Assert.Single(pipeline.Steps, step =>
            step.Tags.Contains(ModuleRepositoryInitializationPipeline.RepositoryStepTag));
        Assert.DoesNotContain(pipeline.Steps, step =>
            step.Tags.Contains(ModuleRepositoryInitializationPipeline.SkippedRepositoryStepTag));
    }

    [Fact]
    public async Task Skipped_repository_step_reports_declaration_and_reason_when_executed()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = Path.Combine(workspace.Path, "consumer");
        Directory.CreateDirectory(Path.Combine(appHostPath, ".git"));
        var pipeline = new CapturingPipeline();
        var inner = CreateBuilder(appHostPath, ["--publisher", "manifest"]);
        var builder = new TestBuilder(
            inner,
            pipeline,
            new TestUserSecretsManager(Path.Combine(appHostPath, "secrets.json")));
        builder.ExportModule("notifications", module =>
        {
            module.WithRepository("https://example.test/acme/notifications.git");
            module.AddContainer("notifications-api", "busybox", "1.37");
        });
        builder.ImportModule("notifications");
        await using var application = inner.Build();
        var logger = new CapturingLogger();
        var diagnostic = Assert.Single(pipeline.Steps, step =>
            step.Tags.Contains(ModuleRepositoryInitializationPipeline.SkippedRepositoryStepTag));

        await ExecuteAsync(application, diagnostic, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("notifications", entry.State["Module"]);
        Assert.Equal("example.test/acme/notifications", entry.State["Repository"]);
        Assert.Equal(
            "the module's resources do not require repository content",
            entry.State["Reason"]);
    }

    [Fact]
    public async Task Pipeline_initialize_satisfies_repository_needed_by_run_project_override()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = Path.Combine(workspace.Path, "consumer");
        Directory.CreateDirectory(Path.Combine(appHostPath, ".git"));
        var secrets = new TestUserSecretsManager(Path.Combine(appHostPath, "secrets.json"));
        new ModuleProjectModeSwitchStore(secrets).Write(new ModuleProjectModeSwitchState
        {
            Mode = ModuleProjectModeSwitchValue.Project
        });

        var pipeline = new CapturingPipeline();
        var pipelineBuilder = new TestBuilder(
            CreateBuilder(appHostPath, ["--publisher", "manifest"]),
            pipeline,
            secrets);
        ConfigureExternalProjectModule(pipelineBuilder);

        var pipelineRequirement = Assert.Single(
            GetRegistry(pipelineBuilder).RepositoryPlans!.Requirements);
        Assert.False(pipelineRequirement.RequiredOnRun);
        Assert.Contains(pipeline.Steps, step =>
            step.Tags.Contains(ModuleRepositoryInitializationPipeline.RepositoryStepTag));
        Assert.DoesNotContain(pipeline.Steps, step =>
            step.Tags.Contains(ModuleRepositoryInitializationPipeline.SkippedRepositoryStepTag));

        var stateStore = new InMemoryModuleRepositoryStateStore();
        await ModuleRepositoryInitializationPipeline.InitializeAndRecordAsync(
            pipelineRequirement,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            NullLogger.Instance,
            NullLogger.Instance,
            stateStore,
            reportingStep: null,
            TestContext.Current.CancellationToken,
            InitializeCheckoutAsync);

        var runBuilder = new TestBuilder(
            CreateBuilder(appHostPath),
            new CapturingPipeline(),
            secrets);
        ConfigureExternalProjectModule(runBuilder);
        var runRegistry = GetRegistry(runBuilder);
        var runRequirement = Assert.Single(runRegistry.RepositoryPlans!.Requirements);
        Assert.True(runRequirement.RequiredOnRun);
        Assert.Equal(pipelineRequirement.RepositoryPath, runRequirement.RepositoryPath);
        var runScope = runRegistry.GetRepositoryPreflightScope("orders", "orders-worker");
        Assert.Contains(runRequirement, runScope.Repositories);

        await runRegistry.ValidateRepositoryPreflightAsync(
            stateStore,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            appHostPath,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Container_override_does_not_require_selectable_project_repository_on_run()
    {
        using var workspace = TemporaryDirectory.Create();
        var appHostPath = Path.Combine(workspace.Path, "consumer");
        Directory.CreateDirectory(Path.Combine(appHostPath, ".git"));
        var secrets = new TestUserSecretsManager(Path.Combine(appHostPath, "secrets.json"));
        new ModuleProjectModeSwitchStore(secrets).Write(new ModuleProjectModeSwitchState
        {
            Mode = ModuleProjectModeSwitchValue.Container
        });
        var builder = new TestBuilder(
            CreateBuilder(appHostPath),
            new CapturingPipeline(),
            secrets);
        ConfigureExternalProjectModule(builder);

        var registry = GetRegistry(builder);
        var requirement = Assert.Single(registry.RepositoryPlans!.Requirements);
        Assert.False(requirement.RequiredOnRun);
        Assert.False(Directory.Exists(requirement.RepositoryPath));
        await registry.ValidateRepositoryPreflightAsync(
            new InMemoryModuleRepositoryStateStore(),
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            appHostPath,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Global_and_resource_steps_use_effective_resource_names()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        var switching = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, pipeline, secrets));

        switching.RegisterProject("sales-orders-api");
        switching.RegisterProject("sales-orders-api");

        Assert.Equal(
            [
                "use-projects",
                "use-containers",
                "use-configured-modes",
                "use-project-sales-orders-api",
                "use-container-sales-orders-api",
                "use-configured-sales-orders-api"
            ],
            pipeline.Steps.Select(step => step.Name));
    }

    [Fact]
    public void Imported_project_steps_follow_the_effective_alias()
    {
        using var directory = TemporaryDirectory.Create();
        var projectPath = Path.Combine(directory.Path, "Orders.Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var builder = new TestBuilder(
            inner,
            pipeline,
            new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json")));
        builder.ExportModule("orders", module =>
        {
            module.WithRepository(directory.Path);
            module.AddProject("orders-api", projectPath)
                .ExportAsContainer("example/orders-api");
        });
        var import = new ModuleImportOptions();
        import.ResourceAliases["orders-api"] = "sales-api";

        builder.ImportModule("orders", import);

        Assert.Contains(pipeline.Steps, step => step.Name == "use-project-sales-api");
        Assert.Contains(pipeline.Steps, step => step.Name == "use-container-sales-api");
        Assert.DoesNotContain(pipeline.Steps, step => step.Name == "use-project-orders-api");
    }

    [Fact]
    public async Task Pipeline_steps_persist_global_exceptions_and_exact_reset()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        var switching = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, pipeline, secrets));
        switching.RegisterProject("orders-api");
        switching.RegisterProject("catalog-api");
        await using var application = inner.Build();

        await ExecuteAsync(application, pipeline["use-containers"]);
        var state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Container, state.Mode);
        Assert.Empty(state.Resources);

        await ExecuteAsync(application, pipeline["use-project-orders-api"]);
        state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Project, state.Resources["orders-api"]);

        await ExecuteAsync(application, pipeline["use-configured-orders-api"]);
        state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Configured, state.Resources["orders-api"]);

        await ExecuteAsync(application, pipeline["use-projects"]);
        state = new ModuleProjectModeSwitchStore(secrets).Read();
        Assert.Equal(ModuleProjectModeSwitchValue.Project, state.Mode);
        Assert.Empty(state.Resources);

        await ExecuteAsync(application, pipeline["use-configured-modes"]);
        Assert.Null(secrets.Get(ModuleProjectModeSwitchStore.SecretName));
    }

    [Fact]
    public void Resource_and_global_switches_override_configuration_with_configured_escape_hatch()
    {
        using var directory = TemporaryDirectory.Create();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        new ModuleProjectModeSwitchStore(secrets).Write(new ModuleProjectModeSwitchState
        {
            Mode = ModuleProjectModeSwitchValue.Container,
            Resources = new Dictionary<string, ModuleProjectModeSwitchValue>
            {
                ["orders-api"] = ModuleProjectModeSwitchValue.Project,
                ["catalog-api"] = ModuleProjectModeSwitchValue.Configured
            }
        });
        var inner = CreateBuilder(directory.Path);
        var switching = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, new CapturingPipeline(), secrets));

        Assert.Equal(
            ModuleProjectMode.Project,
            switching.Resolve("orders-api", ModuleProjectMode.Container, imported: true));
        Assert.Equal(
            ModuleProjectMode.Project,
            switching.Resolve("catalog-api", ModuleProjectMode.Project, imported: true));
        Assert.Equal(
            ModuleProjectMode.Container,
            switching.Resolve("inventory-api", ModuleProjectMode.Project, imported: false));
    }

    [Fact]
    public async Task Unavailable_user_secrets_produce_setup_remediation()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        _ = new ModuleProjectModeSwitchingPipeline(
            new TestBuilder(inner, pipeline, new TestUserSecretsManager(string.Empty, available: false)));

        await using var application = inner.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteAsync(application, pipeline["use-containers"]));

        Assert.Contains("UserSecretsId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets init --project", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_state_fails_with_reset_command()
    {
        using var directory = TemporaryDirectory.Create();
        var secrets = new TestUserSecretsManager(Path.Combine(directory.Path, "secrets.json"));
        Assert.True(secrets.TrySetSecret(
            ModuleProjectModeSwitchStore.SecretName,
            "{\"version\":99,\"mode\":\"Container\",\"resources\":{}}"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ModuleProjectModeSwitchStore(secrets).Read());

        Assert.Contains(ModuleProjectModeSwitchStore.SecretName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("use-configured-modes", exception.Message, StringComparison.Ordinal);

        new ModuleProjectModeSwitchStore(secrets).Delete();
        Assert.Null(secrets.Get(ModuleProjectModeSwitchStore.SecretName));
    }

    private static void ConfigureExternalProjectModule(IDistributedApplicationBuilder builder)
    {
        builder.ConfigureModularAppHosts(options =>
            options.Modules["orders"] = new DistributedApplicationModuleOptions
            {
                Projects =
                {
                    ["orders-worker"] = new DistributedApplicationModuleProjectOptions
                    {
                        PublishImage = false,
                        ImageRegistry = "docker.io/library",
                        ImageName = "busybox",
                        ImageTag = "1.37"
                    }
                }
            });
        builder.ExportModule("orders", module =>
        {
            module.WithRepository("https://example.test/acme/orders.git");
            module.AddProject(
                    "orders-worker",
                    "src/Orders.Worker/Orders.Worker.csproj",
                    ModuleProjectPathBase.Repository)
                .ExportAsContainerWithCommand(new ModuleImageCommandOptions(
                    "orders-worker",
                    ModuleImageCommandOptions.ContainerRuntimePlaceholder,
                    "build",
                    "-t",
                    "orders-worker:test",
                    "."));
        });
        builder.ImportModule("orders");
    }

    private static async Task InitializeCheckoutAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationSettings _,
        Action<string> __,
        Action<RepositorySyncLifecycleEvent> ___,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(requirement.RepositoryPath);
        await RunGitAsync(requirement.RepositoryPath, cancellationToken, "init");
        await RunGitAsync(requirement.RepositoryPath, cancellationToken, "config", "user.name", "Modular AppHosts Tests");
        await RunGitAsync(requirement.RepositoryPath, cancellationToken, "config", "user.email", "tests@example.test");
        await File.WriteAllTextAsync(
            Path.Combine(requirement.RepositoryPath, "content.txt"),
            "initialized",
            cancellationToken);
        var projectDirectory = Path.Combine(requirement.RepositoryPath, "src", "Orders.Worker");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "Orders.Worker.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            cancellationToken);
        await RunGitAsync(requirement.RepositoryPath, cancellationToken, "add", "--", ".");
        await RunGitAsync(requirement.RepositoryPath, cancellationToken, "commit", "-m", "initialized");
        await RunGitAsync(requirement.RepositoryPath, cancellationToken, "remote", "add", "origin", requirement.Repository);
    }

    private static async Task RunGitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .ExecuteBufferedAsync(cancellationToken);
    }

    private static ModuleApplicationRegistry GetRegistry(IDistributedApplicationBuilder builder) =>
        Assert.IsType<ModuleApplicationRegistry>(builder.Services
            .Last(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog))
            .ImplementationInstance);

    private static IDistributedApplicationBuilder CreateBuilder(
        string projectDirectory,
        string[]? args = null) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args ?? [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });

    private static async Task ExecuteAsync(
        DistributedApplication application,
        PipelineStep step,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            logger ?? NullLogger.Instance,
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

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.EventId EventId, Dictionary<string, object?> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

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
    }

    private sealed class CapturingPipeline : IDistributedApplicationPipeline
    {
        public IList<PipelineStep> Steps { get; } = [];

        public PipelineStep this[string name] => Assert.Single(
            Steps,
            step => string.Equals(step.Name, name, StringComparison.Ordinal));

        public void AddStep(
            string name,
            Func<PipelineStepContext, Task> action,
            object? dependsOn = null,
            object? requiredBy = null) => throw new NotSupportedException();

        public void AddStep(PipelineStep step) => Steps.Add(step);

        public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback)
        {
        }

        public Task ExecuteAsync(PipelineContext context) => throw new NotSupportedException();
    }

    private sealed class TestBuilder(
        IDistributedApplicationBuilder inner,
        IDistributedApplicationPipeline pipeline,
        IUserSecretsManager userSecretsManager) : IDistributedApplicationBuilder
    {
        public ConfigurationManager Configuration => inner.Configuration;
        public string AppHostDirectory => inner.AppHostDirectory;
        public Assembly? AppHostAssembly => inner.AppHostAssembly;
        public IHostEnvironment Environment => inner.Environment;
        public IServiceCollection Services => inner.Services;
        public IDistributedApplicationEventing Eventing => inner.Eventing;
        public DistributedApplicationExecutionContext ExecutionContext => inner.ExecutionContext;
        public IResourceCollection Resources => inner.Resources;
        public IDistributedApplicationPipeline Pipeline => pipeline;
        public IFileSystemService FileSystemService => inner.FileSystemService;
        public IUserSecretsManager UserSecretsManager => userSecretsManager;

        public IResourceBuilder<T> AddResource<T>(T resource)
            where T : IResource => inner.AddResource(resource);

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
            where T : IResource => inner.CreateResourceBuilder(resource);

        public DistributedApplication Build() => inner.Build();
    }

    private sealed class TestUserSecretsManager(string filePath, bool available = true) : IUserSecretsManager
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public bool IsAvailable { get; } = available;

        public string FilePath { get; } = filePath;

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public bool TrySetSecret(string name, string value)
        {
            if (!IsAvailable)
            {
                return false;
            }

            _values[name] = value;
            Save();
            return true;
        }

        public bool TryDeleteSecret(string name)
        {
            if (!IsAvailable)
            {
                return false;
            }

            _values.Remove(name);
            Save();
            return true;
        }

        public void GetOrSetSecret(
            IConfigurationManager configuration,
            string name,
            Func<string> valueGenerator) => throw new NotSupportedException();

        public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private void Save()
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(_values));
        }
    }
}
