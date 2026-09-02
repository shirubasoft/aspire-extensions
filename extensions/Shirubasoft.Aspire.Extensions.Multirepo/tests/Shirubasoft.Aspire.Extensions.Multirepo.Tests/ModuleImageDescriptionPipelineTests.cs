#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREUSERSECRETS001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageDescriptionPipelineTests
{
    [Fact]
    public async Task Describes_contract_modules_without_container_images()
    {
        var builder = CreatePublishBuilder(Directory.GetCurrentDirectory());
        var module = builder.ExportModule(
            "contract-only",
            "Sample.ContractOnly",
            definition => definition.AddResource<ParameterResource>(
                "marker",
                context => context.ApplicationBuilder.AddParameter(context.ResourceName)));
        builder.AddModule(module);

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            builder.Resources,
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken,
            [module]);

        Assert.Empty(document.Images);
        var describedModule = Assert.Single(document.Modules);
        Assert.Equal("contract-only", describedModule.Name);
        Assert.Equal("Sample.ContractOnly", describedModule.ContractPackageId);
    }

    [Fact]
    public async Task Describes_explicit_tag_without_the_separate_build_checkout()
    {
        using var workspace = TemporaryDirectory.Create();
        var missingRepository = Path.Combine(workspace.Path, "missing-build-repository");
        var resource = new ContainerResource("orders-api");
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "registry.example.test",
            Image = "acme/orders-api",
            Tag = ModuleImageBuildRecipe.LocalRunTag
        });
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "orders",
            "orders-api",
            workspace.Path,
            imported: true));
        var options = new ModuleImageCommandOptions("acme/orders-api", "docker", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "production",
            PullBeforeBuild = true,
            BuildRepository = "https://example.test/acme/orders-images.git"
        };
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity("orders", "orders-api"),
            new ModuleImageRepositorySettings(
                missingRepository,
                missingRepository,
                options.BuildRepository,
                Revision: null,
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2),
                AppHostDirectory: workspace.Path,
                InitializerOwned: true,
                AllowsUnavailableSource: true),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(ModuleResourceKind.Container, recipe));

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("registry.example.test/acme/orders-api:production", image.Reference);
        Assert.Equal(image.Reference, image.PullReference);
        Assert.Equal(missingRepository, image.Build!.WorkingDirectory);
        Assert.False(Directory.Exists(missingRepository));
    }

    [Fact]
    public async Task Describes_effective_configured_images_for_all_module_publisher_kinds()
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "ImageProject.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var builder = CreatePublishBuilder(repository.Path);
        ConfigureImage(builder, "Projects", "project", "acme/project", "project-ci");
        ConfigureImage(builder, "Containers", "declared", "acme/declared", "declared-ci");
        ConfigureImage(builder, "Containers", "factory", "acme/factory", "factory-ci");
        var module = builder.ExportModule(
            "images",
            "Sample.Images.Contract",
            definition =>
            {
                definition.WithRepository(repository.Path);
                definition.AddProject("project", projectPath)
                    .ExportAsContainerWithCommand(new ModuleImageCommandOptions("old/project", "build-project", "publish")
                    {
                        ImageRegistry = "old.example.test",
                        ImageTag = "old"
                    });
                definition.AddContainer("declared", "old.example.test/old/declared", "old")
                    .WithImagePublishCommand(new ModuleImageCommandOptions(
                        "old/declared",
                        "build-declared",
                        "publish")
                    {
                        ImageRegistry = "old.example.test",
                        ImageTag = "old"
                    });
                definition.AddResource<ContainerResource>(
                    "factory",
                    context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                    new ModuleImageCommandOptions("old/factory", "build-factory", "publish")
                    {
                        ImageRegistry = "old.example.test",
                        ImageTag = "old"
                    });
                definition.AddContainer("consumed", "registry.example.test/library/redis", "7");
            });

        builder.AddModule(module);
        UsePreparedPublishers(builder.Resources);

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            builder.Resources,
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["consumed", "declared", "factory", "project"],
            document.Images.Select(image => image.EffectiveResource));
        var moduleDescription = Assert.Single(document.Modules);
        Assert.Equal("images", moduleDescription.Name);
        Assert.Equal("Sample.Images.Contract", moduleDescription.ContractPackageId);
        Assert.Collection(
            document.Images,
            image =>
            {
                Assert.Equal("consumed", image.Resource);
                Assert.Equal("registry.example.test", image.Registry);
                Assert.Equal("library/redis", image.Repository);
                Assert.Equal("7", image.Tag);
                Assert.Equal("registry.example.test/library/redis:7", image.Reference);
                Assert.Equal(image.Reference, image.PullReference);
                Assert.Null(image.Push);
                Assert.Null(image.Build);
            },
            image => AssertImage(image, "declared", "acme/declared", "declared-ci", "build-declared"),
            image => AssertImage(image, "factory", "acme/factory", "factory-ci", "build-factory"),
            image => AssertImage(image, "project", "acme/project", "project-ci", "build-project"));
    }

    [Fact]
    public async Task Description_selection_accepts_the_declared_resource_alias()
    {
        var resource = new ContainerResource("imported-api");
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "registry.example.test",
            Image = "acme/api",
            Tag = "candidate"
        });
        var options = new ModuleImageCommandOptions("acme/api", "docker", "build")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "candidate"
        };
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity("orders", "api"),
            new ModuleImageRepositorySettings(
                "/work",
                "/work",
                "https://example.test/orders.git",
                "main",
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2)),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));
        var sourceState = new ModuleImageSourceState(
            "main",
            "abcdef012345",
            IsDirty: false,
            StatusFingerprint: "CLEAN");
        var executionPlan = ModuleImageExecutionPlan.Create(recipe, sourceState);
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            (_, _, _, _) => Task.FromResult(new ModulePreparedImage(
                executionPlan.CanonicalImageReference,
                recipe.LocalImageReference,
                sourceState,
                ModuleImagePreparationDisposition.Built)),
            (_, _) => Task.FromResult<ModuleImageSourceState?>(sourceState)));
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "orders",
            "api",
            "/work",
            imported: true));

        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            new ModuleImageSelection([], ["api"]),
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        var module = Assert.Single(document.Modules);
        Assert.Equal("orders", module.Name);
        Assert.Null(module.ContractPackageId);
        Assert.Equal("imported-api", image.EffectiveResource);
        Assert.Equal("api", image.Resource);
    }

    [Fact]
    public async Task Description_selection_rejects_unknown_resources_and_lists_effective_and_declared_names()
    {
        var resource = new ContainerResource("imported-api");
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "registry.example.test",
            Image = "acme/api",
            Tag = "candidate"
        });
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "catalog",
            "api",
            "/work",
            imported: true));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageDescriptionPipeline.CreateDocumentAsync(
                [resource],
                new ModuleImageSelection([], ["missing-api"]),
                TestContext.Current.CancellationToken));

        Assert.Contains("missing-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("imported-api", exception.Message, StringComparison.Ordinal);
        Assert.Contains("api", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_step_writes_a_valid_document_to_the_pipeline_output()
    {
        using var output = TemporaryDirectory.Create();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true
        });
        var resource = builder.AddContainer(
            "imported-api",
            "registry.example.test/acme/api",
            "candidate").Resource;
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "catalog",
            "api",
            "/work",
            imported: true));
        builder.Services.AddSingleton<IPipelineOutputService>(new FixedPipelineOutputService(output.Path));
        var pipeline = new CapturingPipeline();
        ModuleImageDescriptionPipeline.Configure(new PipelineCapturingBuilder(builder, pipeline));
        var step = Assert.Single(pipeline.Steps);
        Assert.Equal(ModuleImageDescriptionPipeline.StepName, step.Name);

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

        var path = Path.Combine(output.Path, ModuleImageDescriptionPipeline.FileName);
        var document = await ModuleImageDescriptionDocument.LoadAsync(
            path,
            TestContext.Current.CancellationToken);
        var image = Assert.Single(document.Images);
        Assert.Equal("catalog", Assert.Single(document.Modules).Name);
        Assert.Equal("imported-api", image.EffectiveResource);
        Assert.Equal("registry.example.test/acme/api:candidate", image.Reference);
    }

    private static void AssertImage(
        ModuleImageDescription image,
        string resource,
        string repository,
        string tag,
        string command)
    {
        Assert.Equal("images", image.Module);
        Assert.Equal(resource, image.Resource);
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal(repository, image.Repository);
        Assert.Equal(tag, image.Tag);
        Assert.Null(image.Digest);
        Assert.Equal($"registry.example.test/{repository}:{tag}", image.Reference);
        Assert.Equal(image.Reference, image.PullReference);
        Assert.Equal("registry.example.test", image.Push!.Registry);
        Assert.Equal(repository, image.Push.Repository);
        Assert.Equal(tag, image.Push.Tag);
        Assert.Equal(image.Reference, image.Push.Reference);
        Assert.Equal(command, image.Build!.Command);
        Assert.Equal($"build-{resource}", image.Build.Step);
    }

    private static void ConfigureImage(
        IDistributedApplicationBuilder builder,
        string collection,
        string resource,
        string repository,
        string tag)
    {
        var section = $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:images:{collection}:{resource}";
        builder.Configuration[$"{section}:ImageRegistry"] = "registry.example.test";
        builder.Configuration[$"{section}:ImageName"] = repository;
        builder.Configuration[$"{section}:ImageTag"] = tag;
    }

    private static void UsePreparedPublishers(IEnumerable<IResource> resources)
    {
        foreach (var resource in resources)
        {
            var publisher = resource.Annotations
                .OfType<ModuleImagePublisherAnnotation>()
                .LastOrDefault();
            if (publisher is null)
            {
                continue;
            }

            var sourceState = new ModuleImageSourceState(
                "main",
                "abcdef012345",
                IsDirty: false,
                StatusFingerprint: "CLEAN");
            var executionPlan = ModuleImageExecutionPlan.Create(publisher.Recipe, sourceState);
            resource.Annotations.Add(new ModuleImagePublisherAnnotation(
                publisher.ResourceKind,
                publisher.Recipe,
                (_, _, _, _) => Task.FromResult(new ModulePreparedImage(
                    executionPlan.CanonicalImageReference,
                    publisher.Recipe.LocalImageReference,
                    sourceState,
                    ModuleImagePreparationDisposition.Built)),
                (_, _) => Task.FromResult<ModuleImageSourceState?>(sourceState)));
        }
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

        public void AddStep(
            string name,
            Func<PipelineStepContext, Task> action,
            object? dependsOn = null,
            object? requiredBy = null) => throw new NotSupportedException();

        public void AddStep(PipelineStep step) => Steps.Add(step);

        public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback) =>
            throw new NotSupportedException();

        public Task ExecuteAsync(PipelineContext context) => throw new NotSupportedException();
    }

    private sealed class FixedPipelineOutputService(string path) : IPipelineOutputService
    {
        public string GetOutputDirectory() => path;

        public string GetOutputDirectory(IResource resource) => path;

        public string GetTempDirectory() => path;

        public string GetTempDirectory(IResource resource) => path;
    }
}
