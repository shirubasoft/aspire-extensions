using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service that ensures all shared resource containers are built
/// before the AppHost starts.
/// </summary>
/// <remarks>
/// This service:
/// <list type="bullet">
///   <item>Subscribes to BeforeStartEvent via Aspire eventing</item>
///   <item>Discovers all ContainerResources with SharedResourceAnnotation</item>
///   <item>Resolves repository paths for each service</item>
///   <item>Determines the current commit SHA for image tagging</item>
///   <item>Checks for existing images to avoid unnecessary builds</item>
///   <item>Builds missing images using the configured build command</item>
///   <item>Updates container resources to use the correct image tag</item>
/// </list>
/// </remarks>
public class SharedResourceBuildService
{
    private readonly IRepositoryPathResolver _pathResolver;
    private readonly IGitOperations _gitOperations;
    private readonly IContainerImageService _containerService;
    private readonly ILogger<SharedResourceBuildService> _logger;

    /// <summary>
    /// Initializes a new instance of the SharedResourceBuildService class.
    /// </summary>
    /// <param name="pathResolver">Service for resolving repository paths.</param>
    /// <param name="gitOperations">Service for git operations.</param>
    /// <param name="containerService">Service for container operations.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    public SharedResourceBuildService(
        IRepositoryPathResolver pathResolver,
        IGitOperations gitOperations,
        IContainerImageService containerService,
        ILogger<SharedResourceBuildService> logger)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _gitOperations = gitOperations ?? throw new ArgumentNullException(nameof(gitOperations));
        _containerService = containerService ?? throw new ArgumentNullException(nameof(containerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Handles the BeforeStartEvent to build any missing container images.
    /// </summary>
    /// <param name="event">The before start event containing the application model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="SharedResourceBuildException">
    /// Thrown when one or more shared resources fail to build.
    /// </exception>
    public async Task OnBeforeStartAsync(
        BeforeStartEvent @event,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        _logger.LogInformation("Checking shared resources...");

        // Validate Docker is available before processing
        if (!await _containerService.IsDockerAvailableAsync(cancellationToken))
        {
            throw new SharedResourceBuildException(
                "Docker is not available. Please ensure Docker is running.");
        }

        // Find all container resources with SharedResourceAnnotation
        var sharedResources = GetSharedResources(@event.Model);

        if (!sharedResources.Any())
        {
            _logger.LogDebug("No shared resources found");
            return;
        }

        _logger.LogInformation("Found {Count} shared resource(s)", sharedResources.Count);

        var errors = new List<SharedResourceError>();

        foreach (var (resource, annotation) in sharedResources)
        {
            try
            {
                await ProcessSharedResourceAsync(resource, annotation, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process shared resource: {ServiceName}",
                    annotation.ServiceName);

                errors.Add(new SharedResourceError(
                    annotation.ServiceName,
                    annotation.GitHubRepository,
                    ex));
            }
        }

        if (errors.Count > 0)
        {
            throw new SharedResourceBuildException(errors);
        }

        _logger.LogInformation("All shared resources ready");
    }

    /// <summary>
    /// Finds all container resources that have a SharedResourceAnnotation.
    /// </summary>
    /// <param name="model">The distributed application model.</param>
    /// <returns>A list of tuples containing the container resource and its annotation.</returns>
    internal List<(ContainerResource Resource, SharedResourceAnnotation Annotation)> GetSharedResources(
        DistributedApplicationModel model)
    {
        var results = new List<(ContainerResource, SharedResourceAnnotation)>();

        foreach (var resource in model.Resources)
        {
            if (resource is ContainerResource containerResource)
            {
                var annotation = resource.Annotations
                    .OfType<SharedResourceAnnotation>()
                    .FirstOrDefault();

                if (annotation is not null)
                {
                    results.Add((containerResource, annotation));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Processes a single shared resource, building the image if necessary.
    /// </summary>
    /// <param name="resource">The container resource.</param>
    /// <param name="annotation">The shared resource annotation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ProcessSharedResourceAsync(
        ContainerResource resource,
        SharedResourceAnnotation annotation,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing shared resource: {ServiceName}", annotation.ServiceName);

        // Step 1: Resolve repository path
        var repoPath = await _pathResolver.ResolveRepositoryPathAsync(
            annotation.GitHubRepository,
            annotation.ServiceName,
            cancellationToken);

        _logger.LogDebug("Repository path: {Path}", repoPath);

        // Step 2: Validate it's a git repository
        if (!await _gitOperations.IsGitRepositoryAsync(repoPath, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Path is not a git repository: {repoPath}");
        }

        // Step 3: Get current commit SHA
        var commitSha = await _gitOperations.GetCurrentCommitShaAsync(repoPath, cancellationToken);
        _logger.LogDebug("Current commit: {Sha}", commitSha);

        // Step 4: Check for uncommitted changes (warning only)
        var hasChanges = await _gitOperations.HasUncommittedChangesAsync(repoPath, cancellationToken);
        if (hasChanges)
        {
            _logger.LogWarning(
                "Repository {ServiceName} has uncommitted changes. " +
                "Image will be tagged with current HEAD SHA ({Sha})",
                annotation.ServiceName, commitSha);
        }

        // Step 5: Determine image name and tag
        var imageName = annotation.GetEffectiveImageName();
        var imageTag = commitSha;

        _logger.LogInformation(
            "{ServiceName}: repo at {Path} (commit: {Sha})",
            annotation.ServiceName, repoPath, commitSha);

        // Step 6: Check if image already exists
        var imageExists = await _containerService.ImageExistsLocallyAsync(
            imageName, imageTag, cancellationToken);

        if (imageExists)
        {
            _logger.LogInformation("  Image {Image}:{Tag} exists", imageName, imageTag);
        }
        else
        {
            // Step 7: Build the image
            _logger.LogInformation("  Building {Image}:{Tag}...", imageName, imageTag);

            var buildCommand = SubstitutePlaceholders(
                annotation.ImageBuildCommand,
                repoPath,
                annotation.ProjectPath,
                imageName,
                imageTag);

            await _containerService.BuildImageAsync(buildCommand, repoPath, cancellationToken);

            _logger.LogInformation("  Image {Image}:{Tag} built successfully", imageName, imageTag);
        }

        // Step 8: Update the container resource to use the correct tag
        UpdateContainerImageTag(resource, imageName, imageTag);
    }

    /// <summary>
    /// Substitutes placeholders in the build command template with actual values.
    /// </summary>
    /// <param name="commandTemplate">The command template with placeholders.</param>
    /// <param name="repoPath">The repository path.</param>
    /// <param name="projectPath">The relative project path (optional).</param>
    /// <param name="imageName">The image name.</param>
    /// <param name="imageTag">The image tag.</param>
    /// <returns>The command with placeholders substituted.</returns>
    internal string SubstitutePlaceholders(
        string commandTemplate,
        string repoPath,
        string? projectPath,
        string imageName,
        string imageTag)
    {
        var result = commandTemplate
            .Replace("{RepoPath}", repoPath)
            .Replace("{ImageName}", imageName)
            .Replace("{ImageTag}", imageTag);

        if (projectPath is not null)
        {
            var fullProjectPath = Path.Combine(repoPath, projectPath);
            result = result.Replace("{ProjectPath}", fullProjectPath);
        }

        return result;
    }

    /// <summary>
    /// Updates the container resource to use the specified image name and tag.
    /// </summary>
    /// <param name="resource">The container resource to update.</param>
    /// <param name="imageName">The image name.</param>
    /// <param name="imageTag">The image tag.</param>
    internal void UpdateContainerImageTag(
        ContainerResource resource,
        string imageName,
        string imageTag)
    {
        // Update the container resource to use the built image
        // This modifies the resource's image reference to include the commit SHA tag

        // Find any existing ContainerImageAnnotation
        var existingAnnotation = resource.Annotations
            .OfType<ContainerImageAnnotation>()
            .LastOrDefault();

        if (existingAnnotation is not null)
        {
            // ContainerImageAnnotation properties have setters, so we can update directly
            // This matches the pattern used by Aspire's WithImageTag method
            existingAnnotation.Image = imageName;
            existingAnnotation.Tag = imageTag;
        }
        else
        {
            // Add new annotation
            resource.Annotations.Add(new ContainerImageAnnotation
            {
                Image = imageName,
                Tag = imageTag
            });
        }

        _logger.LogDebug(
            "Updated container resource {Name} to use image {Image}:{Tag}",
            resource.Name, imageName, imageTag);
    }
}
