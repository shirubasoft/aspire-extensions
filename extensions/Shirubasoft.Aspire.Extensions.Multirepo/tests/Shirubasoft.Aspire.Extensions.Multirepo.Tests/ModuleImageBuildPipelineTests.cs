#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageBuildPipelineTests
{
    [Fact]
    public async Task Every_module_publisher_kind_contributes_a_build_step()
    {
        using var repository = TemporaryDirectory.Create();
        var projectPath = Path.Combine(repository.Path, "ImageProject.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var builder = CreatePublishBuilder(repository.Path);
        var module = builder.ExportModule("images", definition =>
        {
            definition.WithRepository(repository.Path);
            definition.AddProject("project", projectPath)
                .ExportAsContainerWithCommand(Publisher("project"));
            definition.AddContainer("declared", "registry.example.test/acme/declared", "ci")
                .WithImagePublishCommand(Publisher("declared"));
            definition.AddResource<ContainerResource>(
                "factory",
                context => context.ApplicationBuilder.AddContainer(context.ResourceName, "placeholder"),
                Publisher("factory"));
        });

        builder.AddModule(module);

        foreach (var resource in builder.Resources.OfType<ContainerResource>())
        {
            var step = Assert.Single(await CreateBuildStepsAsync(resource));
            Assert.Equal($"build-{resource.Name}", step.Name);
            Assert.Contains(WellKnownPipelineSteps.BuildPrereq, step.DependsOnSteps);
            Assert.Contains(WellKnownPipelineSteps.Build, step.RequiredBySteps);
            Assert.Contains(ModuleImageBuildPipeline.BuildContainerImageTag, step.Tags);
        }
    }

    [Fact]
    public async Task Build_step_delegates_to_the_publisher_recipe_once()
    {
        var options = Publisher("api");
        var preparations = 0;
        var (resource, context, application) = await CreateContextAsync(
            options,
            (_, _, _, _) =>
            {
                preparations++;
                return Task.FromResult(CreatePreparedImage(options));
            });
        await using (application)
        {
            await ModuleImageBuildPipeline.BuildAsync(resource, context);
            await ModuleImageBuildPipeline.BuildAsync(resource, context);

            Assert.Equal(1, preparations);
        }
    }

    [Fact]
    public async Task Recipe_preparation_failures_propagate_from_the_build_step()
    {
        var options = Publisher("api");
        var (resource, context, application) = await CreateContextAsync(
            options,
            (_, _, _, _) => throw new InvalidOperationException("build failed"));
        await using (application)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ModuleImageBuildPipeline.BuildAsync(resource, context));

            Assert.Equal("build failed", exception.Message);
        }
    }

    private static ModuleImageCommandOptions Publisher(string resource) =>
        new($"acme/{resource}", $"build-{resource}", "publish", "{image}")
        {
            ImageRegistry = "registry.example.test",
            ImageTag = "ci"
        };

    private static async Task<(
        ContainerResource Resource,
        PipelineStepContext Context,
        DistributedApplication Application)> CreateContextAsync(
        ModuleImageCommandOptions options,
        Func<
            ModuleImageBuildRecipe,
            Microsoft.Extensions.Logging.ILogger,
            Microsoft.Extensions.Logging.ILogger,
            CancellationToken,
            Task<ModulePreparedImage>> prepareAsync)
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("api", options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(options.ImageRegistry)
            .Resource;
        var recipe = CreateRecipe(
            options,
            "api");
        resource.Annotations.Add(new ModuleImagePublisherAnnotation(
            ModuleResourceKind.Container,
            recipe,
            prepareAsync));
        var application = builder.Build();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            "build-api",
            TestContext.Current.CancellationToken);
        var context = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };
        return (resource, context, application);
    }

    private static ModuleImageBuildRecipe CreateRecipe(
        ModuleImageCommandOptions options,
        string resourceName) =>
        new(
            new ModuleImageRecipeIdentity("images", resourceName),
            new ModuleImageRepositorySettings(
                "/work",
                "/work",
                "https://example.test/images.git",
                Revision: null,
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2)),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));

    private static ModulePreparedImage CreatePreparedImage(ModuleImageCommandOptions options)
    {
        var sourceState = new ModuleImageSourceState(
            "main",
            "abcdef012345",
            IsDirty: false,
            StatusFingerprint: "CLEAN");
        var recipe = CreateRecipe(options, "api");
        var plan = ModuleImageExecutionPlan.Create(recipe, sourceState);
        return new ModulePreparedImage(
            plan.CanonicalImageReference,
            recipe.LocalImageReference,
            sourceState,
            ModuleImagePreparationDisposition.Built);
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreateBuildStepsAsync(IResource resource)
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

        return steps.Where(step => step.Tags.Contains(ModuleImageBuildPipeline.BuildContainerImageTag)).ToArray();
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
}
