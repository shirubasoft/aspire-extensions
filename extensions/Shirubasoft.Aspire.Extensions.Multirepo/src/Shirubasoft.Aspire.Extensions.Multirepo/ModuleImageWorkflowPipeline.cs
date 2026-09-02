#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static class ModuleImageWorkflowPipeline
{
    internal const string StepName = "workflow-images";

    private static readonly Action<ILogger, string, string, Exception?> LogImage =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogImage)),
            "Workflow image {Resource}: {Reference}.");

    private static readonly Action<ILogger, string, Exception?> LogOutput =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(LogOutput)),
            "Wrote the module image workflow document to {Path}.");

    public static void Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var workflow = ModuleImageWorkflowConfiguration.Read(builder.Configuration);
        var workflowStep = new PipelineStep
        {
            Name = StepName,
            Description = "Pushes selected module images and writes their resolved remote identities.",
            Action = context => WriteAsync(context, workflow.Selection)
        };
        builder.Pipeline.AddStep(workflowStep);
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            ConfigureSelectedDependencies(context.Steps, workflow, workflowStep);
            return Task.CompletedTask;
        });
    }

    internal static void ConfigureSelectedDependencies(
        IReadOnlyList<PipelineStep> steps,
        ModuleImageWorkflowOptions workflow,
        PipelineStep workflowStep)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(workflowStep);
        var pushSteps = steps
            .Where(IsModuleImagePushStep)
            .ToArray();
        AttachNativeValidationDependencies(steps, pushSteps);
        var selectedResources = workflow.Selection.ResolveResources(
            pushSteps.Select(step => step.Resource!),
            "workflow image push steps");
        workflow.ValidateSelectedResources(selectedResources);
        foreach (var step in pushSteps.Where(step => selectedResources.Contains(step.Resource!)))
        {
            workflowStep.DependsOnSteps.Add(step.Name);
        }
    }

    private static bool IsModuleImagePushStep(PipelineStep step)
    {
        if (step.Resource is null)
        {
            return false;
        }

        return IsModuleResource(step.Resource) && step.Tags.Contains(WellKnownPipelineTags.PushContainerImage);
    }

    private static bool IsModuleResource(IResource resource) =>
        resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>().Any();

    internal static void AttachNativeValidationDependencies(
        IReadOnlyList<PipelineStep> steps,
        IReadOnlyList<PipelineStep>? pushSteps = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var imagePushSteps = pushSteps ?? steps.Where(IsImagePushStep).ToArray();
        var nativeValidationSteps = steps.Where(IsNativeValidationStep).ToDictionary(step => step.Resource!);
        foreach (var pushStep in imagePushSteps.Where(IsNativeImagePushStep))
        {
            AttachValidationDependency(pushStep, nativeValidationSteps[pushStep.Resource!]);
        }
    }

    private static bool IsImagePushStep(PipelineStep step) =>
        step.Resource is not null && step.Tags.Contains(WellKnownPipelineTags.PushContainerImage);

    private static bool IsNativeValidationStep(PipelineStep step) =>
        step.Resource is not null && step.Tags.Contains(ModuleNativeImageValidationPipeline.StepTag);

    private static bool IsNativeImagePushStep(PipelineStep step) =>
        step.Resource!.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().Any();

    private static void AttachValidationDependency(PipelineStep pushStep, PipelineStep validationStep)
    {
        if (!pushStep.DependsOnSteps.Contains(validationStep.Name, StringComparer.Ordinal))
        {
            pushStep.DependsOnSteps.Add(validationStep.Name);
        }
    }

    internal static async Task<ModuleImageWorkflowDocument> CreateDocumentAsync(
        IEnumerable<IResource> resources,
        ModuleImageSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(selection);
        var images = resources
            .Select(CreateCandidate)
            .Where(IsPublishableCandidate)
            .OrderBy(item => item.Module!.ModuleName, StringComparer.Ordinal)
            .ThenBy(item => item.Module!.ResourceName, StringComparer.Ordinal)
            .ToArray();
        var selectedResources = selection.ResolveResources(
            images.Select(item => item.Resource),
            "workflow image publishers");

        var document = new ModuleImageWorkflowDocument();
        foreach (var item in images.Where(item => selectedResources.Contains(item.Resource)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            document.Images.Add(await CreateWorkflowEntryAsync(item, cancellationToken).ConfigureAwait(false));
        }

        document.Validate();
        return document;
    }

    private static WorkflowImageCandidate CreateCandidate(IResource resource) =>
        new(
            resource,
            resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>().LastOrDefault(),
            resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault(),
            resource.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().LastOrDefault());

    private static bool IsPublishableCandidate(WorkflowImageCandidate candidate)
    {
        if (candidate.Module is null)
        {
            return false;
        }

        return HasPublisher(candidate) && ModuleEffectiveImageResolver.HasPushTarget(candidate.Resource);
    }

    private static bool HasPublisher(WorkflowImageCandidate candidate) =>
        candidate.Publisher is not null || candidate.NativePublisher is not null;

    private static async Task<ModuleImageWorkflowEntry> CreateWorkflowEntryAsync(
        WorkflowImageCandidate candidate,
        CancellationToken cancellationToken)
    {
        var usePreparedPublisherImage = candidate.Publisher?.TryGetPreparedImage(out _) == true;
        var effective = await ModuleEffectiveImageResolver.ResolveAsync(
            candidate.Resource,
            cancellationToken,
            usePreparedPublisherImage: usePreparedPublisherImage).ConfigureAwait(false);
        var remote = effective.PushImage ?? throw new InvalidOperationException(
            $"Resource '{candidate.Resource.Name}' does not resolve to a complete remote image identity.");
        return new ModuleImageWorkflowEntry
        {
            Module = candidate.Module!.ModuleName,
            Resource = candidate.Module.ResourceName,
            ResourceKind = GetResourceKind(candidate),
            Registry = remote.Registry,
            Repository = remote.Repository,
            Tag = remote.Tag
        };
    }

    private static ModuleResourceKind GetResourceKind(WorkflowImageCandidate candidate) =>
        candidate.Publisher?.ResourceKind ?? candidate.NativePublisher!.ResourceKind;

    private static async Task WriteAsync(
        PipelineStepContext context,
        ModuleImageSelection selection)
    {
        var document = await CreateDocumentAsync(
            context.Model.Resources,
            selection,
            context.CancellationToken).ConfigureAwait(false);
        if (document.Images.Count == 0)
        {
            throw new InvalidOperationException(
                "The AppHost does not expose any publishable module images.");
        }

        var output = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var path = Path.Combine(output, ModuleImageWorkflowDocument.DefaultFileName);
        await document.SaveAsync(path, context.CancellationToken).ConfigureAwait(false);
        foreach (var image in document.Images)
        {
            LogImage(context.Logger, $"{image.Module}/{image.Resource}", image.Reference, null);
        }

        LogOutput(context.Logger, path, null);
    }

    private sealed record WorkflowImageCandidate(
        IResource Resource,
        DistributedApplicationModuleResourceAnnotation? Module,
        ModuleImagePublisherAnnotation? Publisher,
        ModuleNativeImagePublisherAnnotation? NativePublisher);
}
