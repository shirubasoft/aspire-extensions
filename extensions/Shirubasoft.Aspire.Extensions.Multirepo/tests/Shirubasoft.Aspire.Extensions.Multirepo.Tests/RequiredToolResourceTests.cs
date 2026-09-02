#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMMAND001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREUSERSECRETS001

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class RequiredToolResourceTests
{
    [Fact]
    public async Task Required_tool_is_a_health_gated_local_dependency_with_dashboard_commands()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var tool = builder.AddRequiredTool("dotnet-sdk", "dotnet")
            .WithWebsite("https://dotnet.microsoft.com/download")
            .WithInstallCommand("installer-command", "install", "dotnet");
        var dependent = builder.AddExecutable(
                "dependent",
                "dotnet",
                directory.Path,
                "--info")
            .WaitFor(tool);

        Assert.Equal("dotnet", tool.Resource.Command);
        Assert.True(tool.Resource.IsExcludedFromPublish());
        var snapshot = Assert.Single(tool.Resource.Annotations.OfType<ResourceSnapshotAnnotation>())
            .InitialSnapshot;
        Assert.Equal("Required Tool", snapshot.ResourceType);
        Assert.Equal(KnownResourceStates.Waiting, snapshot.State);
        Assert.Contains(snapshot.Properties, property =>
            property.Name == "tool.command" && Equals(property.Value, "dotnet"));
        var health = Assert.Single(tool.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal(RequiredToolHealthCheck.GetKey(tool.Resource), health.Key);
        Assert.DoesNotContain(tool.Resource.Annotations, annotation => annotation is RequiredCommandAnnotation);
        Assert.Equal(
            ["install", "website"],
            tool.Resource.Annotations
                .OfType<ResourceCommandAnnotation>()
                .Select(command => command.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Single(tool.Resource.Annotations.OfType<RequiredToolWebsiteAnnotation>());
        var wait = Assert.Single(dependent.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(tool.Resource, wait.Resource);
        Assert.Equal(WaitType.WaitUntilHealthy, wait.WaitType);

        var installStep = Assert.Single(await CreatePipelineStepsAsync(tool.Resource));
        Assert.Equal(RequiredToolInstallationPipeline.GetStepName(tool.Resource), installStep.Name);
        Assert.Same(tool.Resource, installStep.Resource);
        Assert.Contains(ModuleRepositoryInitializationPipeline.StepName, installStep.RequiredBySteps);
        Assert.Contains(RequiredToolInstallationPipeline.StepTag, installStep.Tags);

        await using var application = builder.Build();
        var healthCheckService = application.Services.GetRequiredService<HealthCheckService>();
        var report = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == health.Key,
            TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Missing_tool_health_check_is_unhealthy_and_can_be_cancelled()
    {
        using var directory = TemporaryDirectory.Create();
        var resource = new RequiredToolResource(
            "missing-tool",
            Path.Combine(directory.Path, "missing-tool"));
        var healthCheck = new RequiredToolHealthCheck(resource);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(resource.Command, result.Description, StringComparison.Ordinal);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            healthCheck.CheckHealthAsync(new HealthCheckContext(), cancelled.Token));
    }

    [Fact]
    public async Task Installer_is_skipped_when_tool_is_already_available()
    {
        var resource = new RequiredToolResource("dotnet-sdk", "dotnet");
        var installer = new RequiredToolInstallerAnnotation(
            "installer-that-must-not-run",
            [],
            Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(1));

        var resolvedPath = await RequiredToolOperations.EnsureInstalledAsync(
            resource,
            installer,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(RequiredToolPathResolver.Resolve("dotnet"), resolvedPath);
    }

    [Fact]
    public async Task Installer_must_make_the_required_command_available()
    {
        using var directory = TemporaryDirectory.Create();
        var installedTool = Path.Combine(directory.Path, "installed-tool");
        var installerProject = Path.Combine(directory.Path, "InstallTool.proj");
        await File.WriteAllTextAsync(
            installerProject,
            $"""
            <Project>
              <Target Name="Install">
                <WriteLinesToFile File="{installedTool}" Lines="installed" Overwrite="true" />
              </Target>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        var resource = new RequiredToolResource("installed-tool", installedTool);
        var installer = new RequiredToolInstallerAnnotation(
            "dotnet",
            ["msbuild", installerProject, "-target:Install", "-nologo"],
            directory.Path,
            TimeSpan.FromMinutes(1));

        var resolvedPath = await RequiredToolOperations.EnsureInstalledAsync(
            resource,
            installer,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(installedTool, resolvedPath);
        Assert.True(File.Exists(installedTool));
    }

    [Fact]
    public async Task Successful_installer_that_does_not_supply_tool_is_rejected()
    {
        using var directory = TemporaryDirectory.Create();
        var resource = new RequiredToolResource(
            "missing-tool",
            Path.Combine(directory.Path, "missing-tool"));
        var installer = new RequiredToolInstallerAnnotation(
            "dotnet",
            ["--version"],
            directory.Path,
            TimeSpan.FromMinutes(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RequiredToolOperations.EnsureInstalledAsync(
                resource,
                installer,
                NullLogger.Instance,
                TestContext.Current.CancellationToken));

        Assert.Contains("succeeded", exception.Message, StringComparison.Ordinal);
        Assert.Contains(resource.Command, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_installer_reports_exit_code_and_diagnostics()
    {
        using var directory = TemporaryDirectory.Create();
        var resource = new RequiredToolResource(
            "missing-tool",
            Path.Combine(directory.Path, "missing-tool"));
        var installer = new RequiredToolInstallerAnnotation(
            "dotnet",
            ["definitely-not-a-dotnet-command"],
            directory.Path,
            TimeSpan.FromMinutes(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RequiredToolOperations.EnsureInstalledAsync(
                resource,
                installer,
                NullLogger.Instance,
                TestContext.Current.CancellationToken));

        Assert.Contains("exited with code", exception.Message, StringComparison.Ordinal);
        Assert.Contains("required tool 'missing-tool'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_registrations_share_one_initialize_aggregate_step()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var builder = new PipelineCapturingBuilder(inner, pipeline);

        RequiredToolInstallationPipeline.Configure(builder);
        builder.AddRequiredTool("first-tool", "dotnet")
            .WithInstallCommand("first-installer");
        builder.AddRequiredTool("second-tool", "dotnet")
            .WithInstallCommand("second-installer");

        var aggregate = Assert.Single(pipeline.Steps);
        Assert.Equal(ModuleRepositoryInitializationPipeline.StepName, aggregate.Name);
        Assert.Contains("prerequisites", aggregate.Description, StringComparison.Ordinal);
        Assert.Equal(1, pipeline.ConfigurationCount);
        await aggregate.Action(null!);
    }

    [Fact]
    public void Tool_installers_run_before_other_initialize_prerequisites()
    {
        var firstTool = CreatePipelineStep(
            "install-first-tool",
            tags: [RequiredToolInstallationPipeline.StepTag],
            requiredBy: [ModuleRepositoryInitializationPipeline.StepName]);
        var secondTool = CreatePipelineStep(
            "install-second-tool",
            tags: [RequiredToolInstallationPipeline.StepTag],
            requiredBy: [ModuleRepositoryInitializationPipeline.StepName]);
        var repository = CreatePipelineStep(
            "initialize-repository",
            tags: [ModuleRepositoryInitializationPipeline.RepositoryStepTag],
            requiredBy: [ModuleRepositoryInitializationPipeline.StepName]);

        RequiredToolInstallationPipeline.ConfigureInitializationDependencies(
            [firstTool, secondTool, repository]);
        RequiredToolInstallationPipeline.ConfigureInitializationDependencies(
            [firstTool, secondTool, repository]);

        Assert.Equal([firstTool.Name, secondTool.Name], repository.DependsOnSteps);
        Assert.Empty(firstTool.DependsOnSteps);
        Assert.Empty(secondTool.DependsOnSteps);
    }

    [Fact]
    public async Task Tool_without_installer_has_no_install_pipeline_step()
    {
        using var directory = TemporaryDirectory.Create();
        var inner = CreateBuilder(directory.Path);
        var pipeline = new CapturingPipeline();
        var builder = new PipelineCapturingBuilder(inner, pipeline);
        var tool = builder.AddRequiredTool("dotnet-sdk", "dotnet");

        var steps = await CreatePipelineStepsAsync(tool.Resource);

        Assert.Empty(steps);
        Assert.Empty(pipeline.Steps);
        Assert.Equal(0, pipeline.ConfigurationCount);
    }

    [Fact]
    public void Installer_configuration_replaces_the_previous_command_and_resolves_working_directory()
    {
        using var directory = TemporaryDirectory.Create();
        var workingDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "installer"));
        var builder = CreateBuilder(directory.Path);
        var tool = builder.AddRequiredTool("dotnet-sdk", "dotnet")
            .WithInstallCommand("first-installer", "first")
            .WithInstallCommand(new RequiredToolInstallOptions("second-installer", "second")
            {
                WorkingDirectory = "installer",
                Timeout = TimeSpan.FromMinutes(2)
            });

        var installer = Assert.Single(tool.Resource.Annotations.OfType<RequiredToolInstallerAnnotation>());
        Assert.Equal("second-installer", installer.Command);
        Assert.Equal(["second"], installer.Arguments);
        Assert.Equal(workingDirectory.FullName, installer.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(2), installer.Timeout);
        Assert.Single(
            tool.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            command => command.Name == "install");
    }

    [Fact]
    public void Invalid_required_tool_configuration_is_rejected()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var tool = builder.AddRequiredTool("tool", "dotnet");

        Assert.Throws<ArgumentException>(() => new RequiredToolResource("tool", " "));
        Assert.Throws<ArgumentException>(() => new RequiredToolInstallOptions(" "));
        Assert.Throws<ArgumentException>(() =>
            new RequiredToolInstallOptions("installer", [null!]));
        Assert.Throws<ArgumentException>(() => tool.WithWebsite("relative/path"));
        Assert.Throws<ArgumentException>(() => tool.WithWebsite(new Uri("file:///tmp/tool")));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            tool.WithInstallCommand(new RequiredToolInstallOptions("installer")
            {
                Timeout = TimeSpan.Zero
            }));
        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Path_resolver_supports_absolute_paths_and_missing_path_configuration()
    {
        using var directory = TemporaryDirectory.Create();
        var command = Path.Combine(directory.Path, "tool");
        File.WriteAllText(command, string.Empty);

        Assert.Equal(command, RequiredToolPathResolver.Resolve(command));
        Assert.Null(RequiredToolPathResolver.Resolve(Path.Combine(directory.Path, "missing")));
        Assert.Null(RequiredToolPathResolver.Resolve("missing-from-empty-path", searchPath: null));
    }

    [Fact]
    public void Executable_extension_resolution_covers_windows_and_non_windows_rules()
    {
        var customPathExtensions = string.Join(
            Path.PathSeparator,
            ["EXE", ".cmd", ".CMD"]);

        Assert.Equal(
            [string.Empty],
            RequiredToolPathResolver.GetExecutableExtensions("tool", isWindows: false, pathExtensions: null));
        Assert.Equal(
            [string.Empty],
            RequiredToolPathResolver.GetExecutableExtensions("tool.exe", isWindows: true, pathExtensions: null));
        Assert.Equal(
            [string.Empty, ".exe", ".cmd", ".bat", ".com"],
            RequiredToolPathResolver.GetExecutableExtensions("tool", isWindows: true, pathExtensions: null));
        Assert.Equal(
            [string.Empty, ".EXE", ".cmd"],
            RequiredToolPathResolver.GetExecutableExtensions(
                "tool",
                isWindows: true,
                customPathExtensions));
    }

    [Fact]
    public async Task Installation_step_reports_an_already_available_tool()
    {
        using var directory = TemporaryDirectory.Create();
        var builder = CreateBuilder(directory.Path);
        var tool = builder.AddRequiredTool("dotnet-sdk", "dotnet")
            .WithInstallCommand("unused-installer");
        var step = Assert.Single(await CreatePipelineStepsAsync(tool.Resource));
        await using var application = builder.Build();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await using var reportingStep = new RecordingReportingStep();

        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        var reportingTask = Assert.Single(reportingStep.Tasks);
        Assert.Equal("Install dotnet-sdk", reportingTask.InitialStatusText);
        Assert.Equal(
            $"Available at {RequiredToolPathResolver.Resolve("dotnet")}",
            reportingTask.CompletionMessage);
        Assert.Equal(CompletionState.Completed, reportingTask.RecordedCompletionState);
        Assert.True(reportingTask.IsDisposed);
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePipelineStepsAsync(IResource resource)
    {
        var steps = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = null!,
                Resource = resource
            }));
        }

        return steps;
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });

    private static PipelineStep CreatePipelineStep(
        string name,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> requiredBy) => new()
        {
            Name = name,
            Description = name,
            Action = static _ => Task.CompletedTask,
            Tags = [.. tags],
            RequiredBySteps = [.. requiredBy]
        };

    private sealed class PipelineCapturingBuilder(
        IDistributedApplicationBuilder inner,
        IDistributedApplicationPipeline pipeline) : IDistributedApplicationBuilder
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

        public IUserSecretsManager UserSecretsManager => inner.UserSecretsManager;

        public IResourceBuilder<T> AddResource<T>(T resource)
            where T : IResource => inner.AddResource(resource);

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
            where T : IResource => inner.CreateResourceBuilder(resource);

        public DistributedApplication Build() => inner.Build();
    }

    private sealed class CapturingPipeline : IDistributedApplicationPipeline
    {
        public IList<PipelineStep> Steps { get; } = [];

        public int ConfigurationCount { get; private set; }

        public void AddStep(
            string name,
            Func<PipelineStepContext, Task> action,
            object? dependsOn = null,
            object? requiredBy = null) => throw new NotSupportedException();

        public void AddStep(PipelineStep step) => Steps.Add(step);

        public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ConfigurationCount++;
        }

        public Task ExecuteAsync(PipelineContext context) => throw new NotSupportedException();
    }
}
