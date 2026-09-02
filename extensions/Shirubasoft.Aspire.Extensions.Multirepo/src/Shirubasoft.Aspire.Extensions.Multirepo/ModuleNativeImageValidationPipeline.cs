#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting;

internal static class ModuleNativeImageValidationPipeline
{
    internal const string StepTag = "module-native-image-source-validation";

    public static void AddValidationStep<TResource>(
        IResourceBuilder<TResource> resource,
        string repositoryPath,
        ModularAppHostsOptions options)
        where TResource : IResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(options);

        resource.WithRequiredCommand(options.GitExecutablePath);
        resource.WithPipelineStepFactory(context =>
        [
            new PipelineStep
            {
                Name = GetStepName(context.Resource),
                Description = $"Rejects a dirty source checkout before pushing {context.Resource.Name}.",
                Action = pipelineContext => ValidateCleanSourceAsync(
                    context.Resource,
                    repositoryPath,
                    options.GitExecutablePath,
                    options.RepositoryCommandTimeout,
                    pipelineContext.CancellationToken),
                Tags = [StepTag],
                Resource = context.Resource
            }
        ]);
    }

    internal static string GetStepName(IResource resource) =>
        $"validate-clean-source-{resource.Name}";

    internal static async Task ValidateCleanSourceAsync(
        IResource resource,
        string repositoryPath,
        string gitExecutablePath,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (await RepositoryInspector.IsDirtyAsync(
                repositoryPath,
                gitExecutablePath,
                commandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' cannot push an image built from a dirty repository. " +
                "Commit or stash the source changes before publishing the image.");
        }
    }
}
