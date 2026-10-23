#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImagePushPipelineTests
{
    [Fact]
    public async Task Exported_project_with_an_explicit_image_registry_contributes_a_push_step()
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
        var step = Assert.Single(await CreatePushStepsAsync(container));
        Assert.Equal("push-orders-api", step.Name);
        Assert.Same(container, step.Resource);
        Assert.Contains("build-orders-api", step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.PushPrereq, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.CheckContainerRuntime, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Push, step.RequiredBySteps);
        Assert.Contains(WellKnownPipelineTags.PushContainerImage, step.Tags);
    }

    [Theory]
    [InlineData("Podman", "localhost:5000/acme/api:test", true)]
    [InlineData("podman", "127.0.0.1:5000/acme/api:test", true)]
    [InlineData("podman", "[::1]:5000/acme/api:test", true)]
    [InlineData("podman", "registry.example.test/acme/api:test", false)]
    [InlineData("docker", "localhost:5000/acme/api:test", false)]
    public void Podman_disables_tls_verification_only_for_loopback_registry_pushes(
        string runtime,
        string reference,
        bool disablesTls)
    {
        var arguments = ModuleImagePushPipeline.CreatePushArguments(runtime, reference);

        Assert.Equal("push", arguments[0]);
        Assert.Equal(reference, arguments[^1]);
        Assert.Equal(disablesTls, arguments.Contains("--tls-verify=false"));
    }

    [Fact]
    public async Task Declared_and_factory_created_image_publishers_contribute_push_steps()
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
            var step = Assert.Single(await CreatePushStepsAsync(container));
            Assert.Equal($"push-{container.Name}", step.Name);
        }
    }

    [Fact]
    public async Task Exported_project_reuses_its_container_registry_and_remote_image_options()
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
                        .WithRemoteImageTag("candidate")
                        .WithImagePushTarget("mirror.example.test/team/orders"));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var registryAnnotation = Assert.Single(
            container.Annotations.OfType<ContainerRegistryReferenceAnnotation>());
        Assert.Same(registry.Resource, registryAnnotation.Registry);

        var pushOptions = new ContainerImagePushOptions();
        var pushOptionsContext = new ContainerImagePushOptionsCallbackContext
        {
            Resource = container,
            Options = pushOptions,
            CancellationToken = TestContext.Current.CancellationToken
        };
        foreach (var annotation in container.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>())
        {
            await annotation.Callback(pushOptionsContext);
        }

        Assert.Equal("services/orders", pushOptions.RemoteImageName);
        Assert.Equal("candidate", pushOptions.RemoteImageTag);
        Assert.Single(await CreatePushStepsAsync(container));
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(container, TestContext.Current.CancellationToken);
        Assert.Equal("registry.example.test/acme/services/orders:candidate", resolved.PushReference);
        Assert.Equal(
            $"mirror.example.test/team/orders:{ModuleImageBuildRecipe.LocalRunTag}",
            Assert.Single(ModuleImagePushPipeline.GetPushTargets(container, resolved.Reference)));
    }

    [Fact]
    public async Task Publisher_supplies_remote_defaults_to_aspires_push_options()
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
                    new ModuleImageCommandOptions("services/orders", "dotnet", "publish")
                    {
                        ImageTag = "candidate"
                    },
                    (_, container) => container.WithContainerRegistry(registry));
        });

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var pushOptions = new ContainerImagePushOptions();
        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = container,
            Options = pushOptions,
            CancellationToken = TestContext.Current.CancellationToken
        };
        foreach (var annotation in container.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        Assert.Equal("services/orders", pushOptions.RemoteImageName);
        Assert.Equal("candidate", pushOptions.RemoteImageTag);
    }

    [Fact]
    public async Task Clean_publisher_resolves_a_branch_alias_in_the_remote_repository()
    {
        var resource = await CreateBranchAliasResourceAsync("feature-orders", repositoryDirty: false);
        var resolved = new ModuleEffectiveImage(
            "orders-api:candidate",
            "registry.example.test/acme/orders-api:candidate",
            "registry.example.test/acme/orders-api:candidate",
            ModuleImagePushTargetKind.AspireRegistry,
            null,
            "orders-api",
            "candidate",
            null,
            new ModuleRemoteImage(
                "registry.example.test",
                "acme/orders-api",
                "candidate",
                "registry.example.test/acme/orders-api:candidate"));

        var alias = ModuleImagePushPipeline.GetBranchAliasReference(resource, resolved);

        Assert.Equal("registry.example.test/acme/orders-api:feature-orders", alias);
    }

    [Fact]
    public async Task Detached_clean_AppHost_source_uses_its_configured_CI_branch_alias()
    {
        var resource = await CreateBranchAliasResourceAsync(
            branchImageTag: null,
            repositoryDirty: false,
            detachedBranchAlias: "pull-request/orders");
        var resolved = new ModuleEffectiveImage(
            "orders-api:candidate",
            "registry.example.test/acme/orders-api:candidate",
            "registry.example.test/acme/orders-api:candidate",
            ModuleImagePushTargetKind.ContainerRuntime,
            null,
            "orders-api",
            "candidate",
            null,
            new ModuleRemoteImage(
                "registry.example.test",
                "acme/orders-api",
                "candidate",
                "registry.example.test/acme/orders-api:candidate"));

        var alias = ModuleImagePushPipeline.GetBranchAliasReference(resource, resolved);

        Assert.Equal("registry.example.test/acme/orders-api:pull-request-orders", alias);
    }

    [Fact]
    public async Task Unavailable_source_never_publishes_a_mutable_branch_alias()
    {
        var resource = await CreateBranchAliasResourceAsync(
            branchImageTag: null,
            repositoryDirty: false,
            detachedBranchAlias: "pull-request/orders",
            sourceAvailable: false);
        var reference = "registry.example.test/acme/orders-api:candidate";
        var resolved = new ModuleEffectiveImage(
            "orders-api:candidate",
            reference,
            reference,
            ModuleImagePushTargetKind.ContainerRuntime,
            null,
            "orders-api",
            "candidate",
            null,
            new ModuleRemoteImage(
                "registry.example.test",
                "acme/orders-api",
                "candidate",
                reference));

        Assert.Null(ModuleImagePushPipeline.GetBranchAliasReference(resource, resolved));
    }

    [Theory]
    [InlineData(true, "feature-orders", "candidate")]
    [InlineData(false, null, "candidate")]
    [InlineData(false, "candidate", "candidate")]
    public async Task Branch_alias_is_skipped_when_it_is_not_safe_or_distinct(
        bool repositoryDirty,
        string? branchImageTag,
        string pushedTag)
    {
        var resource = await CreateBranchAliasResourceAsync(branchImageTag, repositoryDirty);
        var reference = $"registry.example.test/acme/orders-api:{pushedTag}";
        var resolved = new ModuleEffectiveImage(
            "orders-api:candidate",
            reference,
            reference,
            ModuleImagePushTargetKind.ContainerRuntime,
            null,
            "orders-api",
            "candidate",
            null,
            new ModuleRemoteImage(
                "registry.example.test",
                "acme/orders-api",
                pushedTag,
                reference));

        Assert.Null(ModuleImagePushPipeline.GetBranchAliasReference(resource, resolved));
    }

    [Fact]
    public async Task Push_targets_use_a_deployment_target_registry_that_before_start_attaches()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("orders-api", "acme/orders-api", "1.0")
                .WithImagePublishCommand(new ModuleImageCommandOptions("acme/orders-api", "docker", "build"))
                .WithImagePushTarget("mirror.example.test/acme/orders-api");
        });
        builder.AddModule(module);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var registry = new ContainerRegistryResource(
            "environment-registry",
            ReferenceExpression.Create($"registry.example.test"));
        builder.Pipeline.AddStep(
            "attach-deployment-target",
            _ =>
            {
                container.Annotations.Add(new DeploymentTargetAnnotation(new ContainerResource("environment-app"))
                {
                    ContainerRegistry = registry
                });
                return Task.CompletedTask;
            },
            requiredBy: WellKnownPipelineSteps.BeforeStart);
        var resolutions = new List<string[]>();
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            resolutions.Add(context.Steps.Select(step => step.Name).ToArray());
            return Task.CompletedTask;
        });

        await RunPushPrerequisitesAsync(builder);

        Assert.DoesNotContain("push-orders-api", resolutions[0]);
        Assert.Contains("push-orders-api", resolutions[^1]);
        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            container,
            TestContext.Current.CancellationToken);
        Assert.Equal("registry.example.test/acme/orders-api:latest", resolved.PushReference);
        Assert.Equal(
            $"mirror.example.test/acme/orders-api:{ModuleImageBuildRecipe.LocalRunTag}",
            Assert.Single(ModuleImagePushPipeline.GetPushTargets(container, resolved.Reference)));
    }

    [Fact]
    public async Task Consumer_running_an_external_image_ignores_the_producer_push_targets()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        builder.ConfigureModularAppHosts(options =>
        {
            options.Modules["orders"] = new DistributedApplicationModuleOptions
            {
                Containers =
                {
                    ["orders-api"] = new DistributedApplicationModuleContainerOptions
                    {
                        PublishImage = false,
                        ImageRegistry = "registry.example.test",
                        ImageName = "acme/orders-api",
                        ImageTag = "main-0123456789ab"
                    }
                }
            };
        });
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.AddContainer("orders-api", "acme/orders-api")
                .WithImagePublishCommand(new ModuleImageCommandOptions("acme/orders-api", "docker", "build")
                {
                    ImageRegistry = "registry.example.test"
                })
                .WithImagePushTarget("mirror.example.test/acme/orders-api");
        });
        builder.ImportModule("orders");

        await RunPushPrerequisitesAsync(builder);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(container.Annotations.OfType<ModuleImagePushTargetAnnotation>());
        Assert.Empty(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
        Assert.Empty(await CreatePushStepsAsync(container));
    }

    [Fact]
    public async Task Push_prerequisites_reject_push_targets_on_a_publisher_without_a_registry_target()
    {
        using var repository = CreateProject();
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddContainer("orders-api", "acme/orders-api", "1.0")
                .WithImagePublishCommand(new ModuleImageCommandOptions("acme/orders-api", "docker", "build"))
                .WithImagePushTarget("mirror.example.test/acme/orders-api");
        });
        builder.AddModule(module);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunPushPrerequisitesAsync(builder));

        Assert.Contains("'orders-api' declares WithImagePushTarget", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithContainerRegistry", exception.Message, StringComparison.Ordinal);
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(await CreatePushStepsAsync(container));
    }

    [Fact]
    public void Push_targets_are_rejected_on_a_native_image_publisher()
    {
        using var repository = CreateProject();
        var worker = DistributedApplication.CreateBuilder()
            .AddDockerfile("worker", repository.Path)
            .WithAnnotation(new ModuleNativeImagePublisherAnnotation(ModuleResourceKind.Container))
            .WithImagePushTarget("mirror.example.test/acme/worker")
            .Resource;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleImagePushPipeline.ValidatePushTargets([worker]));

        Assert.Contains("'worker' declares WithImagePushTarget", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("registry.example.test/acme/api@sha256:0123456789abcdef")]
    [InlineData(" ")]
    public void Push_target_requires_a_tag_reference(string reference)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ModuleImagePushTargetAnnotation(reference));
    }

    [Fact]
    public void Push_targets_are_used_as_written_and_reuse_the_local_tag_when_it_is_omitted()
    {
        var container = DistributedApplication.CreateBuilder()
            .AddContainer("orders-api", "acme/orders-api", "candidate")
            .WithImagePushTarget("mirror.example.test/acme/orders-api")
            .WithImagePushTarget("localhost:5000/acme/orders-api")
            .WithImagePushTarget("mirror.example.test/acme/orders-api:stable")
            .WithImagePushTarget("mirror.example.test/acme/orders-api:main-0123456789ab")
            .Resource;

        var targets = ModuleImagePushPipeline.GetPushTargets(
            container,
            "registry.example.test/acme/orders-api:main-0123456789ab");

        Assert.Equal(
            [
                "mirror.example.test/acme/orders-api:main-0123456789ab",
                "localhost:5000/acme/orders-api:main-0123456789ab",
                "mirror.example.test/acme/orders-api:stable"
            ],
            targets);
    }

    [Fact]
    public async Task Every_push_target_is_attempted_before_failures_are_reported()
    {
        var resource = new ContainerResource("orders-api");
        var errors = new Dictionary<string, string>
        {
            ["first.example.test/api:1"] = "denied by first",
            ["third.example.test/api:1"] = "denied by third"
        };
        var attempted = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImagePushPipeline.PushTargetsAsync(
                resource,
                ["first.example.test/api:1", "second.example.test/api:1", "third.example.test/api:1"],
                (target, _) =>
                {
                    attempted.Add(target);
                    return errors.TryGetValue(target, out var error)
                        ? Task.FromException(new InvalidOperationException(error))
                        : Task.CompletedTask;
                },
                NullLogger.Instance,
                TestContext.Current.CancellationToken));

        Assert.Equal(["first.example.test/api:1", "second.example.test/api:1", "third.example.test/api:1"], attempted);
        Assert.StartsWith(
            "Resource 'orders-api' failed to push 2 of 3 image push targets:",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("- first.example.test/api:1: denied by first", exception.Message, StringComparison.Ordinal);
        Assert.Contains("- third.example.test/api:1: denied by third", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second.example.test", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Log in to each target registry", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, Assert.IsType<AggregateException>(exception.InnerException).InnerExceptions.Count);
    }

    [Fact]
    public Task Push_targets_that_all_succeed_complete_the_push() =>
        ModuleImagePushPipeline.PushTargetsAsync(
            new ContainerResource("orders-api"),
            ["first.example.test/api:1", "second.example.test/api:1"],
            (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Canceling_a_push_skips_the_remaining_targets()
    {
        using var cancellation = new CancellationTokenSource();
        var attempted = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModuleImagePushPipeline.PushTargetsAsync(
                new ContainerResource("orders-api"),
                ["first.example.test/api:1", "second.example.test/api:1"],
                (target, token) =>
                {
                    attempted.Add(target);
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                },
                NullLogger.Instance,
                cancellation.Token));

        Assert.Equal(["first.example.test/api:1"], attempted);
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePushStepsAsync(IResource resource)
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

        return steps
            .Where(step => step.Tags.Contains(WellKnownPipelineTags.PushContainerImage))
            .ToArray();
    }

    private static async Task<ContainerResource> CreateBranchAliasResourceAsync(
        string? branchImageTag,
        bool repositoryDirty,
        string? detachedBranchAlias = null,
        bool sourceAvailable = true)
    {
        var resource = new ContainerResource("orders-api");
        var options = new ModuleImageCommandOptions("orders-api", "docker", "build")
        {
            ImageTag = "candidate"
        };
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity("orders", "api"),
            new ModuleImageRepositorySettings(
                "/work",
                "/work",
                "https://example.test/orders.git",
                Revision: null,
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2),
                detachedBranchAlias),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));
        var sourceState = new ModuleImageSourceState(
            branchImageTag,
            "abcdef012345",
            repositoryDirty,
            repositoryDirty ? "DIRTY" : "CLEAN",
            sourceAvailable);
        var plan = ModuleImageExecutionPlan.Create(recipe, sourceState);
        var prepared = new ModulePreparedImage(
            plan.CanonicalImageReference,
            recipe.LocalImageReference,
            sourceState,
            ModuleImagePreparationDisposition.Built);
        var publisher = new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            (_, _, _, _) => Task.FromResult(prepared));
        resource.Annotations.Add(publisher);
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        return resource;
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

    private static Task RunPushPrerequisitesAsync(IDistributedApplicationBuilder builder) =>
        PipelineStepRunner.RunAsync(builder, WellKnownPipelineSteps.BeforeStart, WellKnownPipelineSteps.PushPrereq);

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
}
