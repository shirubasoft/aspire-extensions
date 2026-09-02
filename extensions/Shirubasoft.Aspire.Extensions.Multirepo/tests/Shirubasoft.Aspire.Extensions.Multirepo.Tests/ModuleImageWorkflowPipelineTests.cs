#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageWorkflowPipelineTests
{
    [Fact]
    public async Task Scoped_selection_preserves_subsequent_Aspire_manifest_output()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true
        });
        var selected = builder.AddContainer("orders-api", "acme/orders-api", "candidate").Resource;
        selected.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "orders",
            "api",
            "/work/orders",
            imported: true));
        var unselected = builder.AddContainer("orders-worker", "acme/orders-worker", "candidate").Resource;
        unselected.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "orders",
            "worker",
            "/work/orders",
            imported: true));
        var ordinary = builder.AddContainer("ordinary", "acme/ordinary", "candidate").Resource;
        var selectedPush = CreatePushStep("push-orders-api", selected);
        var unselectedPush = CreatePushStep("push-orders-worker", unselected);
        var ordinaryPush = CreatePushStep("push-ordinary", ordinary);
        var workflowStep = new PipelineStep
        {
            Name = ModuleImageWorkflowPipeline.StepName,
            Description = "test",
            Action = static _ => Task.CompletedTask
        };
        var workflow = new ModuleImageWorkflowOptions(
            new ModuleImageSelection([], ["api"]),
            GlobalTag: null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var resources = new IResource[] { selected, unselected, ordinary };
        var manifestBeforeSelection = await RenderManifestResourcesAsync(
            resources,
            builder.ExecutionContext,
            TestContext.Current.CancellationToken);

        ModuleImageWorkflowPipeline.ConfigureSelectedDependencies(
            [selectedPush, unselectedPush, ordinaryPush],
            workflow,
            workflowStep);
        var manifestAfterSelection = await RenderManifestResourcesAsync(
            resources,
            builder.ExecutionContext,
            TestContext.Current.CancellationToken);

        Assert.Equal([selectedPush.Name], workflowStep.DependsOnSteps);
        Assert.Equal(manifestBeforeSelection, manifestAfterSelection);
        Assert.DoesNotContain(
            selected.Annotations.OfType<ManifestPublishingCallbackAnnotation>(),
            annotation => ReferenceEquals(annotation, ManifestPublishingCallbackAnnotation.Ignore));
        Assert.DoesNotContain(
            unselected.Annotations.OfType<ManifestPublishingCallbackAnnotation>(),
            annotation => ReferenceEquals(annotation, ManifestPublishingCallbackAnnotation.Ignore));
    }

    [Fact]
    public async Task Includes_Aspire_native_Dockerfile_publishers_with_canonical_push_defaults()
    {
        using var repository = TemporaryDirectory.Create();
        File.WriteAllText(
            Path.Combine(repository.Path, "Dockerfile"),
            "FROM scratch\n");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
            ProjectDirectory = repository.Path
        });
        builder.Configuration[
            $"{ModuleImageWorkflowConfiguration.ConfigurationSectionName}:" +
            ModuleImageWorkflowConfiguration.TagConfigurationName] = "candidate";
        var registry = builder.AddContainerRegistry("registry", "registry.example.test", "team");
        var module = builder.ExportModule("workers", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddResource<ContainerResource>(
                "default-worker",
                context => context.ApplicationBuilder
                    .AddDockerfile(context.ResourceName, repository.Path)
                    .WithContainerRegistry(registry));
            definition.AddResource<ContainerResource>(
                "custom-worker",
                context => context.ApplicationBuilder
                    .AddDockerfile(context.ResourceName, repository.Path)
                    .WithContainerRegistry(registry)
                    .WithRemoteImageName("services/custom-worker")
                    .WithRemoteImageTag("pinned"));
        });
        builder.AddModule(module);

        var containers = builder.Resources.OfType<ContainerResource>().ToArray();
        Assert.Equal(2, containers.Length);
        Assert.All(containers, container =>
        {
            Assert.True(container.RequiresImageBuildAndPush());
            Assert.Single(container.Annotations.OfType<ModuleNativeImagePublisherAnnotation>());
            Assert.Empty(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
        });

        var steps = new List<PipelineStep>();
        foreach (var container in containers)
        {
            foreach (var annotation in container.Annotations.OfType<PipelineStepAnnotation>())
            {
                steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
                {
                    PipelineContext = null!,
                    Resource = container
                }));
            }
        }

        ModuleImageWorkflowPipeline.AttachNativeValidationDependencies(steps);
        foreach (var container in containers)
        {
            var validation = Assert.Single(steps, step =>
                ReferenceEquals(step.Resource, container) &&
                step.Tags.Contains(ModuleNativeImageValidationPipeline.StepTag));
            var push = Assert.Single(steps, step =>
                ReferenceEquals(step.Resource, container) &&
                step.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
            Assert.Contains(validation.Name, push.DependsOnSteps);
        }

        var document = await ModuleImageWorkflowPipeline.CreateDocumentAsync(
            builder.Resources,
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            document.Images,
            image =>
            {
                Assert.Equal("custom-worker", image.Resource);
                Assert.Equal(ModuleResourceKind.Container, image.ResourceKind);
                Assert.Equal(
                    "registry.example.test/team/services/custom-worker:pinned",
                    image.Reference);
            },
            image =>
            {
                Assert.Equal("default-worker", image.Resource);
                Assert.Equal(ModuleResourceKind.Container, image.ResourceKind);
                Assert.Equal("registry.example.test/team/default-worker:candidate", image.Reference);
            });
    }

    [Fact]
    public async Task Includes_Aspire_native_project_publishers()
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "Orders.Api.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
            ProjectDirectory = repository.Path
        });
        var registry = builder.AddContainerRegistry("registry", "registry.example.test", "team");
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("api", projectPath)
                .ExportAsContainer(
                    "services/orders",
                    (_, container) => container.WithContainerRegistry(registry));
        });
        builder.AddModule(module);

        var document = await ModuleImageWorkflowPipeline.CreateDocumentAsync(
            builder.Resources,
            new ModuleImageSelection(["orders"], []),
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal(ModuleResourceKind.Project, image.ResourceKind);
        Assert.Equal("registry.example.test/team/services/orders:latest", image.Reference);
    }

    [Fact]
    public async Task Uses_structured_remote_registry_identity_and_declared_resource_alias()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry("registry", "registry.example.test", "acme");
        var container = builder
            .AddContainer("imported-api", "local/api", "local")
            .WithContainerRegistry(registry)
            .WithRemoteImageName("services/orders")
            .WithRemoteImageTag("candidate")
            .Resource;
        await AddPublisherAsync(container, "orders", "api");

        var document = await ModuleImageWorkflowPipeline.CreateDocumentAsync(
            builder.Resources,
            new ModuleImageSelection([], ["api"]),
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("orders", image.Module);
        Assert.Equal("api", image.Resource);
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal("acme/services/orders", image.Repository);
        Assert.Equal("candidate", image.Tag);
        Assert.Equal("registry.example.test/acme/services/orders:candidate", image.Reference);
    }

    [Fact]
    public async Task Rejects_unknown_or_non_publishable_selectors()
    {
        var resource = new ContainerResource("imported-api");
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "registry.example.test",
            Image = "acme/api",
            Tag = "candidate"
        });
        await AddPublisherAsync(resource, "orders", "api");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageWorkflowPipeline.CreateDocumentAsync(
                [resource],
                new ModuleImageSelection([], ["missing"]),
                TestContext.Current.CancellationToken));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("api", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<string> RenderManifestResourcesAsync(
        IReadOnlyList<IResource> resources,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var resource in resources)
        {
            var annotation = resource.Annotations
                .OfType<ManifestPublishingCallbackAnnotation>()
                .LastOrDefault();
            if (annotation?.Callback is not { } callback)
            {
                continue;
            }

            writer.WritePropertyName(resource.Name);
            writer.WriteStartObject();
            await callback(new ManifestPublishingContext(
                executionContext,
                Path.Combine(Path.GetTempPath(), "module-manifest.json"),
                writer,
                cancellationToken));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task AddPublisherAsync(
        ContainerResource resource,
        string module,
        string declaredResource)
    {
        var options = new ModuleImageCommandOptions("local/api", "docker", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "local"
        };
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity(module, declaredResource),
            new ModuleImageRepositorySettings(
                "/work",
                "/work",
                Repository: null,
                Revision: null,
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2)),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Project,
            recipe));
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            module,
            declaredResource,
            "/work",
            imported: true));
        return Task.CompletedTask;
    }

    private static PipelineStep CreatePushStep(string name, IResource resource) => new()
    {
        Name = name,
        Description = "test",
        Action = static _ => Task.CompletedTask,
        Tags = [WellKnownPipelineTags.PushContainerImage],
        Resource = resource
    };
}
