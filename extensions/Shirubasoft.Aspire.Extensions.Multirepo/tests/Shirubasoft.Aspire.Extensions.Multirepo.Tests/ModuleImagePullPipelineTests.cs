#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImagePullPipelineTests
{
    [Fact]
    public async Task Exported_project_with_an_explicit_image_registry_contributes_a_pull_step()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("orders-api", GetProjectPath(repository.Path))
                .ExportAsContainerWithCommand(new ModuleImageCommandOptions("orders-api", "dotnet", "publish")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "candidate"
                });
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var step = Assert.Single(await CreatePullStepsAsync(container));
        Assert.Equal("pull-orders-api", step.Name);
        Assert.Same(container, step.Resource);
        Assert.Contains(ModuleImagePullPipeline.PullPrerequisiteStepName, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.CheckContainerRuntime, step.DependsOnSteps);
        Assert.Contains(ModuleImagePullPipeline.PullStepName, step.RequiredBySteps);
        Assert.Contains(ModuleImagePullPipeline.PullContainerImageTag, step.Tags);
    }

    [Fact]
    public async Task Declared_and_factory_created_image_publishers_contribute_pull_steps()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("assets", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer(
                    "declared-static",
                    "registry.example.test/assets/declared-static",
                    "candidate")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "assets/declared-static",
                    "docker",
                    "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "candidate"
                });
            definition.AddResource<ContainerResource>(
                "factory-static",
                context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                new ModuleImageCommandOptions("assets/factory-static", "docker", "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "candidate"
                });
        });

        builder.AddModule(module);

        var containers = builder.Resources.OfType<ContainerResource>().ToArray();
        Assert.Equal(2, containers.Length);
        foreach (var container in containers)
        {
            var step = Assert.Single(await CreatePullStepsAsync(container));
            Assert.Equal($"pull-{container.Name}", step.Name);
        }
    }

    [Fact]
    public async Task Exported_project_resolves_its_registry_image_back_to_its_local_image()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "acme");
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("orders-api", GetProjectPath(repository.Path))
                .ExportAsContainerWithCommand(
                    new ModuleImageCommandOptions("orders-api", "dotnet", "publish")
                    {
                        ImageTag = "local"
                    },
                    (_, container) => container
                        .WithContainerRegistry(registry)
                        .WithRemoteImageName("services/orders")
                        .WithRemoteImageTag("candidate"));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/acme/services/orders:candidate", references.RemoteImage);
        Assert.Equal("orders-api:aspire-run", references.LocalImage);
    }

    [Fact]
    public async Task Module_registry_publisher_pulls_its_declared_tag_into_the_stable_local_alias()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("database", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("database", "registry.example.test/owner/database", "candidate")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "owner/database",
                    "docker",
                    "build")
                {
                    ImageRegistry = "registry.example.test",
                    ImageTag = "candidate"
                });
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/owner/database:candidate", references.RemoteImage);
        Assert.Equal("registry.example.test/owner/database:aspire-run", references.LocalImage);
    }

    [Fact]
    public async Task Explicit_pull_mapping_resolves_a_remote_image_from_a_different_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "ghcr.io/api", "1-0")
            .WithImagePullMapping("mycustomregistry.io/images:api-1-0")
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("mycustomregistry.io/images:api-1-0", references.RemoteImage);
        Assert.Equal("ghcr.io/api:1-0", references.LocalImage);
    }

    [Fact]
    public async Task Pull_mapping_reports_lifecycle_once_and_reserves_resource_logs_for_subprocess_output()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "ghcr.io/api", "1-0")
            .WithImagePullMapping("mycustomregistry.io/images:api-1-0")
            .Resource;
        await using var application = builder.Build();
        var resourceLoggerService = application.Services.GetRequiredService<ResourceLoggerService>();
        var pipelineLogger = new RecordingLogger();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            pipelineLogger,
            TestContext.Current.CancellationToken);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            "pull-orders-api",
            TestContext.Current.CancellationToken);
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        var commands = new List<(string Runtime, string[] Arguments)>();

        await ModuleImagePullPipeline.PullAsync(
            container,
            stepContext,
            _ => Task.FromResult("test-runtime"),
            (runtime, arguments, _, _, _) =>
            {
                commands.Add((runtime, arguments.ToArray()));
                return Task.CompletedTask;
            },
            (source, target, _) =>
            {
                commands.Add(("test-runtime", ["tag", source, target]));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Collection(
            commands,
            command =>
            {
                Assert.Equal("test-runtime", command.Runtime);
                Assert.Equal(["pull", "mycustomregistry.io/images:api-1-0"], command.Arguments);
            },
            command =>
            {
                Assert.Equal("test-runtime", command.Runtime);
                Assert.Equal(
                    ["tag", "mycustomregistry.io/images:api-1-0", "ghcr.io/api:1-0"],
                    command.Arguments);
            });

        resourceLoggerService.Complete(container);
        var logs = new List<LogLine>();
        await foreach (var lines in resourceLoggerService.WatchAsync(container))
        {
            logs.AddRange(lines);
        }

        Assert.Empty(logs);
        Assert.Contains(pipelineLogger.Messages, message => message.Contains(
            "Pulling remote image mycustomregistry.io/images:api-1-0 for resource orders-api.",
            StringComparison.Ordinal));
        Assert.Contains(pipelineLogger.Messages, message => message.Contains(
            "Re-tagging remote image mycustomregistry.io/images:api-1-0 as local image ghcr.io/api:1-0 for resource orders-api.",
            StringComparison.Ordinal));
        Assert.Contains(pipelineLogger.Messages, message => message.Contains(
            "Re-tagged remote image mycustomregistry.io/images:api-1-0 as local image ghcr.io/api:1-0 for resource orders-api.",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_pull_mapping_takes_precedence_over_an_Aspire_registry_mapping()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry("registry", "ignored.example.test", "ignored");
        var container = builder
            .AddContainer("orders-api", "ghcr.io/api", "1-0")
            .WithContainerRegistry(registry)
            .WithRemoteImageName("ignored-api")
            .WithRemoteImageTag("ignored-tag")
            .WithImagePullMapping("source.example.test/images:api-1-0")
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("source.example.test/images:api-1-0", references.RemoteImage);
        Assert.Equal("ghcr.io/api:1-0", references.LocalImage);
    }

    [Fact]
    public async Task Mapping_only_declared_container_contributes_a_pull_step_but_no_push_step()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("orders-api", "orders-api", "1-0")
                .WithImagePullMapping("source.example.test/images:api-1-0");
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(await CreatePullStepsAsync(container));
        Assert.DoesNotContain(
            await CreatePipelineStepsAsync(container),
            step => step.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
    }

    [Fact]
    public void A_later_explicit_pull_mapping_replaces_the_previous_mapping()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "1-0")
            .WithImagePullMapping("first.example.test/images:api-1-0")
            .WithImagePullMapping("second.example.test/images:api-1-0")
            .Resource;

        var mapping = Assert.Single(container.Annotations.OfType<ModuleImagePullMappingAnnotation>());
        Assert.Equal("second.example.test/images:api-1-0", mapping.RemoteImageReference);
    }

    [Fact]
    public async Task Explicit_pull_mapping_rejects_a_digest_pinned_local_image()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "1-0")
            .WithImageRegistry("ghcr.io")
            .WithImageSHA256(new string('a', 64))
            .WithImagePullMapping("source.example.test/images:api-1-0");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ResolveImageReferencesAsync(
                container.Resource,
                TestContext.Current.CancellationToken));

        Assert.Contains("digest-pinned", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithImagePullMapping", exception.Message, StringComparison.Ordinal);

        ModuleImagePullPipeline.AddPullStep(container);
        var pipelineException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePullStepsAsync(container.Resource));
        Assert.Contains("digest-pinned", pipelineException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_image_registry_uses_the_resource_image_as_remote_and_local_reference()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "candidate")
            .WithImageRegistry("registry.example.test")
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/orders-api:candidate", references.RemoteImage);
        Assert.Equal(references.RemoteImage, references.LocalImage);
    }

    [Fact]
    public async Task Explicit_registry_digest_uses_the_pinned_reference_directly()
    {
        var digest = new string('a', 64);
        var builder = DistributedApplication.CreateBuilder();
        var container = builder
            .AddContainer("orders-api", "orders-api", "candidate")
            .WithImageRegistry("registry.example.test")
            .WithImageSHA256(digest)
            .Resource;

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal($"registry.example.test/orders-api@sha256:{digest}", references.RemoteImage);
        Assert.Equal(references.RemoteImage, references.LocalImage);
    }

    [Fact]
    public async Task Registry_associated_digest_does_not_contribute_an_untaggable_pull_step()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry("registry", "registry.example.test");
        var container = builder
            .AddContainer("orders-api", "orders-api", "candidate")
            .WithImageSHA256(new string('a', 64))
            .WithContainerRegistry(registry);

        ModuleImagePullPipeline.AddPullStep(container);

        Assert.Empty(await CreatePullStepsAsync(container.Resource));
    }

    [Fact]
    public async Task Default_registry_resolves_the_default_remote_name_and_tag()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "acme");
        var container = builder.AddContainer("orders-api", "orders-api", "candidate").Resource;
        container.Annotations.Add(new RegistryTargetAnnotation(registry.Resource));

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/acme/orders-api:latest", references.RemoteImage);
        Assert.Equal("orders-api:candidate", references.LocalImage);
    }

    [Fact]
    public async Task Module_publisher_identity_wins_over_a_colliding_resource_name_default()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "mirror");
        var module = builder.ExportModule("database", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("postgres", "owner/database", "ci")
                .WithImagePublishCommand(new ModuleImageCommandOptions(
                    "owner/database",
                    "docker",
                    "build")
                {
                    ImageTag = "ci"
                });
        });
        builder.AddModule(module);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        container.Annotations.Add(new RegistryTargetAnnotation(registry.Resource));

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/mirror/owner/database:ci", references.RemoteImage);
        Assert.Equal("owner/database:aspire-run", references.LocalImage);
        Assert.DoesNotContain("postgres:latest", references.RemoteImage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_reference_resolution_rejects_a_resource_without_an_image()
    {
        var resource = new ContainerResource("orders-api");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ResolveImageReferencesAsync(
                resource,
                TestContext.Current.CancellationToken));

        Assert.Contains("container image reference", exception.Message, StringComparison.Ordinal);

        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddResource(resource);
        ModuleImagePullPipeline.AddPullStep(container);
        Assert.Empty(await CreatePullStepsAsync(resource));
    }

    [Fact]
    public async Task Pull_reference_resolution_rejects_an_image_without_a_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddContainer("orders-api", "orders-api", "candidate").Resource;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePullPipeline.ResolveImageReferencesAsync(
                resource,
                TestContext.Current.CancellationToken));

        Assert.Contains("container registry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_default_registries_require_an_explicit_resource_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var firstRegistry = builder.AddContainerRegistry("first", "first.example.test");
        var secondRegistry = builder.AddContainerRegistry("second", "second.example.test");
        var container = builder.AddContainer("orders-api", "orders-api", "candidate");
        container.Resource.Annotations.Add(new RegistryTargetAnnotation(firstRegistry.Resource));
        container.Resource.Annotations.Add(new RegistryTargetAnnotation(secondRegistry.Resource));
        ModuleImagePullPipeline.AddPullStep(container);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePullStepsAsync(container.Resource));

        Assert.Contains("multiple container registries", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithContainerRegistry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deployment_target_registry_is_used_when_no_resource_registry_is_configured()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "acme");
        var container = builder.AddContainer("orders-api", "orders-api", "candidate").Resource;
        container.Annotations.Add(new DeploymentTargetAnnotation(new ContainerResource("deployment"))
        {
            ContainerRegistry = registry.Resource
        });

        var references = await ModuleImagePullPipeline.ResolveImageReferencesAsync(
            container,
            TestContext.Current.CancellationToken);

        Assert.Equal("registry.example.test/acme/orders-api:latest", references.RemoteImage);
        Assert.Equal("orders-api:candidate", references.LocalImage);
    }

    [Fact]
    public async Task Module_registry_wins_over_a_compute_environment_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var environmentRegistry = builder.AddContainerRegistry(
            "environment-registry",
            "environment.example.test",
            "environment");
        var resource = builder
            .AddContainer("orders-api", "acme/orders", "candidate")
            .WithImageRegistry("module.example.test")
            .Resource;
        resource.Annotations.Add(new DeploymentTargetAnnotation(new ContainerResource("deployment"))
        {
            ContainerRegistry = environmentRegistry.Resource
        });

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken);

        Assert.Equal("module.example.test/acme/orders:candidate", resolved.PullReference);
        Assert.Equal(resolved.PullReference, resolved.PushReference);
        Assert.Equal(ModuleImagePushTargetKind.ContainerRuntime, resolved.PushTargetKind);
    }

    [Fact]
    public async Task Empty_compute_environment_registry_is_not_a_remote_target()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("orders-api", "acme/orders", "candidate")
            .WithImageRegistry("module.example.test")
            .Resource;
        resource.Annotations.Add(new DeploymentTargetAnnotation(new ContainerResource("deployment"))
        {
            ContainerRegistry = CreateEmptyRegistry()
        });

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken);

        Assert.Equal("module.example.test/acme/orders:candidate", resolved.PullReference);
        Assert.Equal(ModuleImagePushTargetKind.ContainerRuntime, resolved.PushTargetKind);
    }

    [Fact]
    public async Task Empty_explicit_resource_registry_falls_back_to_the_module_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("orders-api", "acme/orders", "candidate")
            .WithImageRegistry("module.example.test")
            .Resource;
        resource.Annotations.Add(new ContainerRegistryReferenceAnnotation(CreateEmptyRegistry()));

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken);

        Assert.Equal("module.example.test/acme/orders:candidate", resolved.PullReference);
        Assert.Equal(ModuleImagePushTargetKind.ContainerRuntime, resolved.PushTargetKind);
    }

    [Fact]
    public async Task Explicit_resource_registry_wins_over_the_module_and_environment_registries()
    {
        var builder = DistributedApplication.CreateBuilder();
        var explicitRegistry = builder.AddContainerRegistry(
            "explicit-registry",
            "explicit.example.test",
            "explicit");
        var environmentRegistry = builder.AddContainerRegistry(
            "environment-registry",
            "environment.example.test",
            "environment");
        var resource = builder
            .AddContainer("orders-api", "acme/orders", "candidate")
            .WithImageRegistry("module.example.test")
            .WithContainerRegistry(explicitRegistry)
            .WithRemoteImageName("orders")
            .WithRemoteImageTag("release")
            .Resource;
        resource.Annotations.Add(new DeploymentTargetAnnotation(new ContainerResource("deployment"))
        {
            ContainerRegistry = environmentRegistry.Resource
        });

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken);

        Assert.Equal("explicit.example.test/explicit/orders:release", resolved.PullReference);
        Assert.Equal(resolved.PullReference, resolved.PushReference);
        Assert.Equal(ModuleImagePushTargetKind.AspireRegistry, resolved.PushTargetKind);
    }

    [Fact]
    public async Task Empty_default_registry_is_ignored_when_a_remote_default_exists()
    {
        var builder = DistributedApplication.CreateBuilder();
        var remoteRegistry = builder.AddContainerRegistry(
            "remote-registry",
            "remote.example.test",
            "acme");
        var resource = builder.AddContainer("orders-api", "orders-api", "candidate").Resource;
        resource.Annotations.Add(new RegistryTargetAnnotation(CreateEmptyRegistry()));
        resource.Annotations.Add(new RegistryTargetAnnotation(remoteRegistry.Resource));

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken);

        Assert.Equal("remote.example.test/acme/orders-api:latest", resolved.PullReference);
        Assert.Equal(ModuleImagePushTargetKind.AspireRegistry, resolved.PushTargetKind);
    }

    private static ContainerRegistryResource CreateEmptyRegistry() =>
        new("local", ReferenceExpression.Empty, ReferenceExpression.Empty);

    private static async Task<IReadOnlyList<PipelineStep>> CreatePullStepsAsync(IResource resource)
    {
        return (await CreatePipelineStepsAsync(resource))
            .Where(step => step.Tags.Contains(ModuleImagePullPipeline.PullContainerImageTag))
            .ToArray();
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

    private static IDistributedApplicationBuilder CreatePublishBuilder(string projectDirectory)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:ProjectMode"] =
            nameof(ModuleProjectMode.Container);
        return builder;
    }

    private static TemporaryDirectory CreateProject()
    {
        var directory = TemporaryDirectory.Create();
        File.WriteAllText(
            GetProjectPath(directory.Path),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return directory;
    }

    private static string GetProjectPath(string directory) =>
        Path.Combine(directory, "Orders.Api.csproj");

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
