#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREUSERSECRETS001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

[Collection("Module image runtime adapter coverage")]
public sealed class ModuleImageDeferredCoverageTests
{
    private static readonly ModuleImageSourceState CleanSource = new(
        "feature/coverage",
        "0123456789ab",
        IsDirty: false,
        StatusFingerprint: "CLEAN");

    [Fact]
    public async Task Clean_explicit_run_tag_reuses_without_a_redundant_retag()
    {
        var options = CreateOptions();
        options.ImageTag = ModuleImageBuildRecipe.LocalRunTag;
        var recipe = CreateRecipe(options);
        var operations = new RecordingOperations(CleanSource)
        {
            ImageExists = true
        };

        var prepared = await ModuleImageRecipeEvaluator.PrepareAsync(
            recipe,
            NullLogger.Instance,
            NullLogger.Instance,
            operations,
            TestContext.Current.CancellationToken);

        Assert.Equal(recipe.LocalImageReference, prepared.CanonicalImageReference);
        Assert.Equal(ModuleImagePreparationDisposition.Reused, prepared.Disposition);
        Assert.Empty(operations.Tags);
    }

    [Fact]
    public async Task Clean_refresh_without_an_upstream_skips_refresh_and_reuses_the_image()
    {
        var recipe = CreateRecipe(refreshCleanCheckout: true);
        var operations = new RecordingOperations(CleanSource)
        {
            ImageExists = true,
            HasUpstream = false
        };

        await ModuleImageRecipeEvaluator.PrepareAsync(
            recipe,
            NullLogger.Instance,
            NullLogger.Instance,
            operations,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, operations.HasUpstreamCount);
        Assert.Equal(0, operations.RefreshCount);
        Assert.Equal(1, operations.ImageExistsCount);
    }

    [Fact]
    public async Task Cancellation_before_preparation_does_not_invoke_operations()
    {
        var operations = new RecordingOperations(CleanSource);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModuleImageRecipeEvaluator.PrepareAsync(
                CreateRecipe(),
                NullLogger.Instance,
                NullLogger.Instance,
                operations,
                cancellation.Token));

        Assert.Equal(0, operations.ResolveRuntimeCount);
        Assert.Equal(0, operations.CaptureCount);
    }

    [Fact]
    public async Task Publisher_rejects_missing_loggers_before_starting_preparation()
    {
        var publisher = CreatePublisher(CreateRecipe());

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PrepareAsync(
            null!,
            NullLogger.Instance,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PrepareAsync(
            NullLogger.Instance,
            null!,
            TestContext.Current.CancellationToken));
        Assert.False(publisher.TryGetPreparedImage(out _));
    }

    [Fact]
    public async Task Canonical_resolution_requires_preparation_when_requested()
    {
        var (resource, publisher) = CreatePublishedContainer(CreateRecipe());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleEffectiveImageResolver.ResolveAsync(
                resource,
                TestContext.Current.CancellationToken,
                usePreparedPublisherImage: true));

        Assert.Contains("has not prepared", exception.Message, StringComparison.Ordinal);
        Assert.False(publisher.TryGetPreparedImage(out _));
    }

    [Fact]
    public async Task Prepared_explicit_registry_resolution_uses_canonical_not_stable_alias_identity()
    {
        var recipe = CreateRecipe();
        var (resource, publisher) = CreatePublishedContainer(recipe);
        var prepared = await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken,
            usePreparedPublisherImage: true);

        Assert.Equal(prepared.CanonicalImageReference, resolved.Reference);
        Assert.Equal(prepared.CanonicalImageReference, resolved.PullReference);
        Assert.Equal(prepared.CanonicalImageReference, resolved.PushReference);
        Assert.Equal(ModuleImagePushTargetKind.ContainerRuntime, resolved.PushTargetKind);
        Assert.Equal("feature-coverage-0123456789ab", resolved.Tag);
        Assert.Equal("registry.example.test/acme/api:aspire-run", recipe.LocalImageReference);
    }

    [Fact]
    public async Task Prepared_registry_target_preserves_canonical_tag_in_remote_identity()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "team");
        var options = new ModuleImageCommandOptions("acme/api", "dotnet", "--version");
        var recipe = CreateRecipe(options);
        var resource = builder
            .AddContainer("api", options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithContainerRegistry(registry)
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        var prepared = await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var resolved = await ModuleEffectiveImageResolver.ResolveAsync(
            resource,
            TestContext.Current.CancellationToken,
            usePreparedPublisherImage: true);

        Assert.Equal(ModuleImagePushTargetKind.AspireRegistry, resolved.PushTargetKind);
        Assert.Equal(prepared.CanonicalImageReference, resolved.Reference);
        Assert.Equal("registry.example.test", resolved.PushImage!.Registry);
        Assert.Equal("team/acme/api", resolved.PushImage.Repository);
        Assert.Equal("feature-coverage-0123456789ab", resolved.PushImage.Tag);
    }

    [Fact]
    public async Task Description_inspects_source_without_preparing_and_emits_runtime_arguments()
    {
        var recipe = CreateRecipe();
        var prepareCalls = 0;
        var inspectCalls = 0;
        var publisher = CreatePublisher(
            recipe,
            (_, _, _, _) =>
            {
                prepareCalls++;
                return Task.FromResult(CreatePreparedImage(recipe));
            },
            (_, _) =>
            {
                inspectCalls++;
                return Task.FromResult<ModuleImageSourceState?>(CleanSource);
            });
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("effective-api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(recipe.Options.ImageRegistry)
            .Resource;
        resource.Annotations.Add(publisher);
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "coverage",
            "api",
            recipe.RepositoryPath,
            imported: true));
        var document = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);
        var second = await ModuleImageDescriptionPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("feature-coverage-0123456789ab", image.Tag);
        Assert.Equal(
            "registry.example.test/acme/api:feature-coverage-0123456789ab",
            image.Reference);
        Assert.Contains(image.Reference, image.Build!.Arguments);
        Assert.Single(second.Images);
        Assert.Equal(0, prepareCalls);
        Assert.Equal(2, inspectCalls);
    }

    [Fact]
    public async Task Workflow_document_uses_prepared_canonical_tag()
    {
        var recipe = CreateRecipe();
        var (resource, publisher) = CreatePublishedContainer(recipe);
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "coverage",
            "api",
            recipe.RepositoryPath,
            imported: true));
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var document = await ModuleImageWorkflowPipeline.CreateDocumentAsync(
            [resource],
            ModuleImageSelection.All,
            TestContext.Current.CancellationToken);

        var image = Assert.Single(document.Images);
        Assert.Equal("coverage", image.Module);
        Assert.Equal("api", image.Resource);
        Assert.Equal("feature-coverage-0123456789ab", image.Tag);
        Assert.Equal(
            "registry.example.test/acme/api:feature-coverage-0123456789ab",
            image.Reference);
    }

    [Fact]
    public async Task Default_operations_capture_clean_and_dirty_git_source_states()
    {
        using var repository = TemporaryDirectory.Create();
        var trackedFile = Path.Combine(repository.Path, "tracked.txt");
        var untrackedFile = Path.Combine(repository.Path, "untracked.txt");
        var ignoredFile = Path.Combine(repository.Path, "ignored.txt");
        File.WriteAllText(trackedFile, "clean");
        File.WriteAllText(Path.Combine(repository.Path, ".gitignore"), "ignored.txt\n");
        await RunGitAsync(repository.Path, "init");
        await RunGitAsync(repository.Path, "config", "user.email", "coverage@example.test");
        await RunGitAsync(repository.Path, "config", "user.name", "Coverage Tests");
        await RunGitAsync(repository.Path, "add", "tracked.txt", ".gitignore");
        await RunGitAsync(repository.Path, "-c", "commit.gpgsign=false", "commit", "-m", "initial");
        var recipe = CreateRecipe(
            repositoryPath: repository.Path,
            workingDirectory: repository.Path,
            repository: repository.Path);

        var clean = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        File.WriteAllText(trackedFile, "dirty");
        var dirty = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        var dirtyPorcelain = await RunGitAsync(
            repository.Path,
            "status",
            "--porcelain=v1",
            "--untracked-files=all");
        File.WriteAllText(trackedFile, "different dirty content");
        var changedTracked = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        var changedTrackedPorcelain = await RunGitAsync(
            repository.Path,
            "status",
            "--porcelain=v1",
            "--untracked-files=all");
        ModuleImageSourceState? changedMode = null;
        string? changedModePorcelain = null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                trackedFile,
                File.GetUnixFileMode(trackedFile) ^ UnixFileMode.UserExecute);
            changedMode = await ModuleImageRecipeOperations.InspectSourceStateAsync(
                recipe,
                TestContext.Current.CancellationToken);
            changedModePorcelain = await RunGitAsync(
                repository.Path,
                "status",
                "--porcelain=v1",
                "--untracked-files=all");
        }

        File.WriteAllText(untrackedFile, "first untracked content");
        var untracked = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        var untrackedPorcelain = await RunGitAsync(
            repository.Path,
            "status",
            "--porcelain=v1",
            "--untracked-files=all");
        File.WriteAllText(untrackedFile, "different untracked content");
        var changedUntracked = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        var changedUntrackedPorcelain = await RunGitAsync(
            repository.Path,
            "status",
            "--porcelain=v1",
            "--untracked-files=all");
        var beforeIgnored = changedUntracked.StatusFingerprint;
        File.WriteAllText(ignoredFile, "first ignored content");
        var ignored = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);
        File.WriteAllText(ignoredFile, "different ignored content");
        var changedIgnored = await ModuleImageRecipeOperations.InspectSourceStateAsync(
            recipe,
            TestContext.Current.CancellationToken);

        Assert.False(clean.IsDirty);
        Assert.NotNull(clean.Branch);
        Assert.Equal(12, clean.Commit!.Length);
        Assert.True(dirty.IsDirty);
        Assert.NotEqual(clean.StatusFingerprint, dirty.StatusFingerprint);
        Assert.Equal(dirtyPorcelain, changedTrackedPorcelain);
        Assert.NotEqual(dirty.StatusFingerprint, changedTracked.StatusFingerprint);
        if (changedMode is not null)
        {
            Assert.Equal(changedTrackedPorcelain, changedModePorcelain);
            Assert.NotEqual(changedTracked.StatusFingerprint, changedMode.StatusFingerprint);
        }

        Assert.Equal(untrackedPorcelain, changedUntrackedPorcelain);
        Assert.NotEqual(untracked.StatusFingerprint, changedUntracked.StatusFingerprint);
        Assert.Equal(beforeIgnored, ignored.StatusFingerprint);
        Assert.Equal(beforeIgnored, changedIgnored.StatusFingerprint);
        Assert.False(await ModuleImageRecipeOperations.Instance.HasUpstreamAsync(
            recipe,
            TestContext.Current.CancellationToken));

        var progress = new List<string>();
        await ModuleImageRecipeOperations.Instance.RefreshRepositoryAsync(
            recipe,
            progress.Add,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Default_source_capture_reports_non_repository_as_inspection_failure()
    {
        using var directory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            repositoryPath: directory.Path,
            workingDirectory: directory.Path);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageRecipeOperations.InspectSourceStateAsync(
                recipe,
                TestContext.Current.CancellationToken));

        Assert.Contains("Unable to inspect", exception.Message, StringComparison.Ordinal);
        Assert.Contains("coverage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_build_operation_runs_cross_platform_managed_command_and_reports_output()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            options: new ModuleImageCommandOptions("acme/api", "dotnet", "--version"),
            repositoryPath: workingDirectory.Path,
            workingDirectory: workingDirectory.Path);
        var plan = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["--version"]);
        var output = new List<string>();

        await ModuleImageRecipeOperations.Instance.BuildImageAsync(
            recipe,
            plan,
            "dotnet",
            output.Add,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(output);
    }

    [Fact]
    public async Task Default_build_operation_resolves_the_container_runtime_placeholder()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            options: new ModuleImageCommandOptions(
                "acme/api",
                ModuleImageCommandOptions.ContainerRuntimePlaceholder,
                "--version"),
            repositoryPath: workingDirectory.Path,
            workingDirectory: workingDirectory.Path);
        var plan = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["--version"]);
        var output = new List<string>();

        await ModuleImageRecipeOperations.Instance.BuildImageAsync(
            recipe,
            plan,
            "dotnet",
            output.Add,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(output);
    }

    [Fact]
    public async Task Default_build_and_tag_operations_surface_managed_command_failures()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var recipe = CreateRecipe(
            options: new ModuleImageCommandOptions("acme/api", "dotnet", "missing-coverage-command"),
            repositoryPath: workingDirectory.Path,
            workingDirectory: workingDirectory.Path);
        var invalidBuild = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["missing-coverage-command"]);

        var buildException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageRecipeOperations.Instance.BuildImageAsync(
                recipe,
                invalidBuild,
                "dotnet",
                _ => { },
                TestContext.Current.CancellationToken));
        var tagException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModuleImageRecipeOperations.Instance.TagImageAsync(
                recipe,
                "dotnet",
                "source:test",
                "target:test",
                _ => { },
                TestContext.Current.CancellationToken));

        Assert.Contains("failed", buildException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IContainerRuntimeResolver", tagException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_build_operation_converts_its_own_timeout_to_a_clear_error()
    {
        using var workingDirectory = TemporaryDirectory.Create();
        var options = new ModuleImageCommandOptions("acme/api", "dotnet", "--info");
        var recipe = new ModuleImageBuildRecipe(
            new ModuleImageRecipeIdentity("coverage", "api"),
            new ModuleImageRepositorySettings(
                workingDirectory.Path,
                workingDirectory.Path,
                Repository: null,
                Revision: null,
                RefreshCleanCheckout: false,
                "git",
                "gh",
                TimeSpan.FromMinutes(2)),
            new ModuleImageCommandSettings(
                options,
                TimeSpan.FromTicks(1),
                TimeSpan.FromMinutes(10)));
        var plan = new ModuleImageExecutionPlan(
            "acme/api:test",
            ProducedImageReference: null,
            PublishArguments: ["--info"]);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ModuleImageRecipeOperations.Instance.BuildImageAsync(
                recipe,
                plan,
                "dotnet",
                _ => { },
                TestContext.Current.CancellationToken));

        Assert.Contains("exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operation_timeout_distinguishes_deadline_from_caller_cancellation()
    {
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            ModuleOperationTimeout.RunAsync(
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                TimeSpan.FromTicks(1),
                "test transfer",
                TestContext.Current.CancellationToken));
        Assert.Contains("test transfer", timeout.Message, StringComparison.Ordinal);

        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModuleOperationTimeout.RunAsync(
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                TimeSpan.FromMinutes(1),
                "test transfer",
            cancellationSource.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Push_operations_use_the_image_transfer_timeout(bool useAspireRegistry)
    {
        var resource = new ContainerResource("api");
        var targetKind = useAspireRegistry
            ? ModuleImagePushTargetKind.AspireRegistry
            : ModuleImagePushTargetKind.ContainerRuntime;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ModuleImagePushPipeline.PushImageAsync(
                targetKind,
                resource,
                "registry.example.test/acme/api:test",
                (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                TimeSpan.FromTicks(1),
                TestContext.Current.CancellationToken));

        Assert.Contains("Image push for resource 'api'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_step_uses_image_manager_for_prepared_registry_image_without_a_runtime()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "team");
        var recipe = CreateRecipe(new ModuleImageCommandOptions("acme/api", "dotnet", "--version"));
        var cleanDetachedSource = CleanSource with { Branch = null };
        var executionPlan = ModuleImageExecutionPlan.Create(recipe, cleanDetachedSource);
        var prepared = new ModulePreparedImage(
            executionPlan.CanonicalImageReference,
            recipe.LocalImageReference,
            cleanDetachedSource,
            ModuleImagePreparationDisposition.Built);
        var preparationCount = 0;
        var publisher = CreatePublisher(
            recipe,
            (_, _, _, _) =>
            {
                preparationCount++;
                return Task.FromResult(prepared);
            });
        var container = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithContainerRegistry(registry)
            .WithAnnotation(publisher);
        ModuleImagePushPipeline.AddPushStep(container);
        var imageManager = new RecordingImageManager();
        builder.Services.AddSingleton<IResourceContainerImageManager>(imageManager);

        await using var application = builder.Build();
        var step = Assert.Single(await CreatePipelineStepsAsync(
            container.Resource,
            WellKnownPipelineTags.PushContainerImage));
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });

        Assert.Same(container.Resource, Assert.Single(imageManager.PushedResources));
        Assert.Equal(1, preparationCount);
        Assert.True(publisher.TryGetPreparedImage(out _));
    }

    [Fact]
    public async Task Push_step_rejects_dirty_source_before_contacting_the_registry()
    {
        var builder = DistributedApplication.CreateBuilder();
        var registry = builder.AddContainerRegistry(
            "registry",
            "registry.example.test",
            "team");
        var recipe = CreateRecipe(new ModuleImageCommandOptions("acme/api", "dotnet", "--version"));
        var dirtySource = CleanSource with { IsDirty = true, StatusFingerprint = "DIRTY" };
        var executionPlan = ModuleImageExecutionPlan.Create(recipe, dirtySource);
        var publisher = CreatePublisher(
            recipe,
            (_, _, _, _) => Task.FromResult(new ModulePreparedImage(
                executionPlan.CanonicalImageReference,
                recipe.LocalImageReference,
                dirtySource,
                ModuleImagePreparationDisposition.Built)));
        var container = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithContainerRegistry(registry)
            .WithAnnotation(publisher);
        ModuleImagePushPipeline.AddPushStep(container);
        var imageManager = new RecordingImageManager();
        builder.Services.AddSingleton<IResourceContainerImageManager>(imageManager);

        await using var application = builder.Build();
        var step = Assert.Single(await CreatePipelineStepsAsync(
            container.Resource,
            WellKnownPipelineTags.PushContainerImage));
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => step.Action(
            new PipelineStepContext
            {
                PipelineContext = pipelineContext,
                ReportingStep = reportingStep
            }));

        Assert.Contains("dirty repository", exception.Message, StringComparison.Ordinal);
        Assert.Empty(imageManager.PushedResources);
    }

    [Fact]
    public async Task Explicit_registry_push_uses_aspires_container_runtime_instead_of_the_registry_manager()
    {
        var options = CreateOptions();
        options.ImageTag = ModuleImageTag.FromBranch(CleanSource.Branch);
        var recipe = CreateRecipe(options);
        var (resource, publisher) = CreatePublishedContainer(recipe);
        var imageManager = new RecordingImageManager();
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var runtimePushed = new List<string>();

        await ModuleImagePushPipeline.PushImageAsync(
            ModuleImagePushTargetKind.ContainerRuntime,
            resource,
            "registry.example.test/acme/api:feature-coverage-0123456789ab",
            (reference, _) =>
            {
                runtimePushed.Add(reference);
                return Task.CompletedTask;
            },
            imageManager.PushImageAsync,
            TimeSpan.FromMinutes(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "registry.example.test/acme/api:feature-coverage-0123456789ab",
            Assert.Single(runtimePushed));
        Assert.Empty(imageManager.PushedResources);
    }

    [Fact]
    public async Task Push_step_reports_missing_remote_target_after_step_creation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var options = new ModuleImageCommandOptions("acme/api", "dotnet", "--version");
        var recipe = CreateRecipe(options);
        var resource = builder
            .AddContainer("api", options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry("registry.example.test")
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        var container = builder.CreateResourceBuilder(resource);
        ModuleImagePushPipeline.AddPushStep(container);
        var step = Assert.Single(await CreatePipelineStepsAsync(
            resource,
            WellKnownPipelineTags.PushContainerImage));
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        resource.Annotations.Remove(image);
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Image = options.ImageName,
            Tag = ModuleImageBuildRecipe.LocalRunTag
        });
        resource.Annotations.Add(new ModuleImagePullMappingAnnotation(
            "registry.example.test/acme/api:remote"));
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        await using var application = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecutePipelineStepAsync(application, step));

        Assert.Contains("remote image push target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_pipeline_step_writes_and_logs_the_prepared_document()
    {
        using var output = TemporaryDirectory.Create();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true
        });
        var recipe = CreateRecipe();
        var resource = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(recipe.Options.ImageRegistry)
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        resource.Annotations.Add(new DistributedApplicationModuleResourceAnnotation(
            "coverage",
            "api",
            recipe.RepositoryPath,
            imported: true));
        await publisher.PrepareAsync(
            NullLogger.Instance,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        builder.Services.AddSingleton<IPipelineOutputService>(new FixedPipelineOutputService(output.Path));
        var pipeline = new CapturingPipeline();
        ModuleImageWorkflowPipeline.Configure(new PipelineCapturingBuilder(builder, pipeline));
        var step = Assert.Single(pipeline.Steps);

        await using var application = builder.Build();
        await ExecutePipelineStepAsync(application, step);

        var path = Path.Combine(output.Path, ModuleImageWorkflowDocument.DefaultFileName);
        var document = await ModuleImageWorkflowDocument.LoadAsync(
            path,
            TestContext.Current.CancellationToken);
        var image = Assert.Single(document.Images);
        Assert.Equal("coverage", image.Module);
        Assert.Equal("feature-coverage-0123456789ab", image.Tag);
    }

    [Theory]
    [InlineData(FakeRuntimeMode.Success, true, true)]
    [InlineData(FakeRuntimeMode.Failure, false, false)]
    [InlineData(FakeRuntimeMode.Missing, false, false)]
    public async Task Container_inspector_handles_runtime_success_failure_and_missing_executable(
        FakeRuntimeMode mode,
        bool expectedExists,
        bool expectedPull)
    {
        using var runtime = new FakeContainerRuntimeEnvironment(mode, configured: true);

        var exists = await ContainerImageInspector.ExistsAsync(
            "docker",
            "acme/api:test",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        var pulled = await ContainerImageInspector.PullAsync(
            "docker",
            "acme/api:test",
            TimeSpan.FromSeconds(30),
            static _ => { },
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedExists, exists);
        Assert.Equal(expectedPull, pulled);
    }

    [Fact]
    public async Task Pull_runtime_output_is_unchanged_in_the_resource_log_and_stderr_is_informational()
    {
        using var runtime = new FakeContainerRuntimeEnvironment(FakeRuntimeMode.Success, configured: true);
        var builder = DistributedApplication.CreateBuilder();
        await using var application = builder.Build();
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            "pull-api",
            TestContext.Current.CancellationToken);
        var pipelineLogger = new RecordingLogger();
        var resourceLogger = new RecordingLogger();
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            pipelineLogger,
            TestContext.Current.CancellationToken);
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };

        await ModuleImagePullPipeline.ExecuteRuntimeAsync(
            "docker",
            ["pull", "acme/api:test"],
            stepContext,
            resourceLogger,
            TestContext.Current.CancellationToken);

        Assert.Empty(pipelineLogger.Entries);
        Assert.NotEmpty(resourceLogger.Entries);
        Assert.All(resourceLogger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Contains(
            resourceLogger.Entries,
            entry => entry.Message.Contains("runtime stdout", StringComparison.Ordinal));
        Assert.Contains(
            resourceLogger.Entries,
            entry => entry.Message.Contains("runtime stderr", StringComparison.Ordinal));
        Assert.Contains(
            resourceLogger.Entries,
            entry => entry.Message.Contains("user:secret", StringComparison.Ordinal));
        Assert.Contains(
            resourceLogger.Entries,
            entry => entry.Message.Contains("token=secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Container_inspector_rejects_empty_references_before_resolving_runtime()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ContainerImageInspector.ExistsAsync(
                "docker",
                " ",
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ContainerImageInspector.PullAsync(
                "docker",
                string.Empty,
                TimeSpan.FromSeconds(30),
                static _ => { },
                TestContext.Current.CancellationToken));
    }

    private static ModuleImageCommandOptions CreateOptions() =>
        new(
            "acme/api",
            "dotnet",
            "publish",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            ModuleImageCommandOptions.ImageRepositoryPlaceholder,
            ModuleImageCommandOptions.ImageTagPlaceholder)
        {
            ImageRegistry = "registry.example.test"
        };

    private static ModuleImageBuildRecipe CreateRecipe(
        ModuleImageCommandOptions? options = null,
        bool refreshCleanCheckout = false,
        string repositoryPath = "/work/coverage",
        string workingDirectory = "/work/coverage",
        string? repository = "https://example.test/acme/coverage.git") =>
        new(
            new ModuleImageRecipeIdentity("coverage", "api"),
            new ModuleImageRepositorySettings(
                repositoryPath,
                workingDirectory,
                repository,
                Revision: null,
                refreshCleanCheckout,
                "git",
                "gh",
                TimeSpan.FromMinutes(2)),
            new ModuleImageCommandSettings(
                options ?? CreateOptions(),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10)));

    private static ModuleImagePublisherAnnotation CreatePublisher(
        ModuleImageBuildRecipe recipe,
        Func<
            ModuleImageBuildRecipe,
            ILogger,
            ILogger,
            CancellationToken,
            Task<ModulePreparedImage>>? prepareAsync = null,
        Func<ModuleImageBuildRecipe, CancellationToken, Task<ModuleImageSourceState?>>? inspectAsync = null) =>
        new(
            ModuleResourceKind.Container,
            recipe,
            prepareAsync ?? ((_, _, _, _) => Task.FromResult(CreatePreparedImage(recipe))),
            inspectAsync ?? ((_, _) => Task.FromResult<ModuleImageSourceState?>(CleanSource)));

    private static (ContainerResource Resource, ModuleImagePublisherAnnotation Publisher)
        CreatePublishedContainer(ModuleImageBuildRecipe recipe)
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder
            .AddContainer("api", recipe.Options.ImageName, ModuleImageBuildRecipe.LocalRunTag)
            .WithImageRegistry(recipe.Options.ImageRegistry)
            .Resource;
        var publisher = CreatePublisher(recipe);
        resource.Annotations.Add(publisher);
        return (resource, publisher);
    }

    private static ModulePreparedImage CreatePreparedImage(ModuleImageBuildRecipe recipe)
    {
        var plan = ModuleImageExecutionPlan.Create(recipe, CleanSource);
        return new ModulePreparedImage(
            plan.CanonicalImageReference,
            recipe.LocalImageReference,
            CleanSource,
            ModuleImagePreparationDisposition.Built);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ModuleCliRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            TimeSpan.FromMinutes(1),
            "prepare coverage repository",
            TestContext.Current.CancellationToken,
            static _ => { });
        Assert.True(result.IsSuccess, result.StandardError);
        return result.StandardOutput;
    }

    private static async Task<IReadOnlyList<PipelineStep>> CreatePipelineStepsAsync(
        IResource resource,
        string tag)
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

        return steps.Where(step => step.Tags.Contains(tag)).ToArray();
    }

    private static async Task ExecutePipelineStepAsync(
        DistributedApplication application,
        PipelineStep step)
    {
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync(
            step.Name,
            TestContext.Current.CancellationToken);
        var pipelineContext = new PipelineContext(
            application.Services.GetRequiredService<DistributedApplicationModel>(),
            application.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            application.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        await step.Action(new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        });
    }

    private sealed class RecordingOperations(ModuleImageSourceState sourceState)
        : IModuleImageRecipeOperations
    {
        public bool ImageExists { get; init; }

        public bool HasUpstream { get; init; }

        public int ResolveRuntimeCount { get; private set; }

        public int CaptureCount { get; private set; }

        public int HasUpstreamCount { get; private set; }

        public int RefreshCount { get; private set; }

        public int ImageExistsCount { get; private set; }

        public List<(string Source, string Target)> Tags { get; } = [];

        public Task<string> ResolveContainerRuntimeAsync(CancellationToken cancellationToken)
        {
            ResolveRuntimeCount++;
            return Task.FromResult("container-runtime");
        }

        public Task<ModuleImageSourceState?> TryCaptureSourceStateAsync(
            ModuleImageBuildRecipe recipe,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult<ModuleImageSourceState?>(sourceState);
        }

        public Task<bool> HasUpstreamAsync(
            ModuleImageBuildRecipe recipe,
            CancellationToken cancellationToken)
        {
            HasUpstreamCount++;
            return Task.FromResult(HasUpstream);
        }

        public Task RefreshRepositoryAsync(
            ModuleImageBuildRecipe recipe,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ImageExistsAsync(
            string containerRuntime,
            string imageReference,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ImageExistsCount++;
            return Task.FromResult(ImageExists);
        }

        public Task<bool> PullImageAsync(
            string containerRuntime,
            string imageReference,
            TimeSpan timeout,
            Action<string> progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task BuildImageAsync(
            ModuleImageBuildRecipe recipe,
            ModuleImageExecutionPlan plan,
            string containerRuntime,
            Action<string> progress,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task TagImageAsync(
            ModuleImageBuildRecipe recipe,
            string containerRuntime,
            string sourceImageReference,
            string targetImageReference,
            Action<string> progress,
            CancellationToken cancellationToken)
        {
            Tags.Add((sourceImageReference, targetImageReference));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingImageManager : IResourceContainerImageManager
    {
        public List<IResource> PushedResources { get; } = [];

        public Task BuildImageAsync(IResource resource, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BuildImagesAsync(
            IEnumerable<IResource> resources,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
        {
            PushedResources.Add(resource);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly object _lock = new();

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    public enum FakeRuntimeMode
    {
        Success,
        Failure,
        Missing
    }

    private sealed class FakeContainerRuntimeEnvironment : IDisposable
    {
        private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _originalRuntime = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME");
        private readonly string? _originalLegacyRuntime = Environment.GetEnvironmentVariable(
            "DOTNET_ASPIRE_CONTAINER_RUNTIME");
        private readonly string _invocationPath;

        public FakeContainerRuntimeEnvironment(FakeRuntimeMode mode, bool configured)
        {
            _invocationPath = Path.Combine(_directory.Path, "invocations.txt");
            if (mode is not FakeRuntimeMode.Missing)
            {
                CreateExecutable("docker", mode);
                CreateExecutable("podman", mode);
            }

            Environment.SetEnvironmentVariable(
                "PATH",
                string.IsNullOrEmpty(_originalPath)
                    ? _directory.Path
                    : $"{_directory.Path}{Path.PathSeparator}{_originalPath}");
            Environment.SetEnvironmentVariable(
                "ASPIRE_CONTAINER_RUNTIME",
                configured ? "docker" : null);
            Environment.SetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME", null);
        }

        public IReadOnlyList<string> Invocations => File.Exists(_invocationPath)
            ? File.ReadAllLines(_invocationPath)
            : [];

        public int InvocationCount => Invocations.Count;

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            Environment.SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", _originalRuntime);
            Environment.SetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME", _originalLegacyRuntime);
            _directory.Dispose();
        }

        private void CreateExecutable(string name, FakeRuntimeMode mode)
        {
            var exitCode = mode is FakeRuntimeMode.Success ? 0 : 7;
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(_directory.Path, $"{name}.cmd");
                File.WriteAllText(
                    path,
                    $"@echo off{Environment.NewLine}" +
                    $"echo %*>>\"{_invocationPath}\"{Environment.NewLine}" +
                    $"echo runtime stdout token=secret{Environment.NewLine}" +
                    $"echo runtime stderr https://user:secret@example.test/path?token=secret 1>&2{Environment.NewLine}" +
                    $"exit /b {exitCode}{Environment.NewLine}");
                return;
            }

            var executablePath = Path.Combine(_directory.Path, name);
            File.WriteAllText(
                executablePath,
                "#!/bin/sh\n" +
                $"printf '%s\\n' \"$*\" >> \"{_invocationPath}\"\n" +
                "printf '%s\\n' 'runtime stdout token=secret'\n" +
                "printf '%s\\n' 'runtime stderr https://user:secret@example.test/path?token=secret' >&2\n" +
                $"exit {exitCode}\n");
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
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

        public void AddPipelineConfiguration(Func<PipelineConfigurationContext, Task> callback)
        {
        }

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

[CollectionDefinition("Module image runtime adapter coverage", DisableParallelization = true)]
public sealed class ModuleImageRuntimeAdapterCoverageCollection;
