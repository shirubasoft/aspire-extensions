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
                        .WithRemoteImageTag("candidate"));
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
