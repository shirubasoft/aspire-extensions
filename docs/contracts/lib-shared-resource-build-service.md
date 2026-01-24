# Contract: SharedResourceBuildService

## Overview

This contract defines the event subscriber service that ensures all shared resource container images are built before the AppHost starts.

## Namespace

```csharp
namespace Aspire.Hosting.SharedResources;
```

## Dependencies

- `Aspire.Hosting` (13.1.0+)
  - `IDistributedApplicationBuilder`
  - `BeforeStartEvent`
  - `IResource`
  - `IResourceAnnotation`
- `Microsoft.Extensions.Logging`
- `IRepositoryPathResolver`
- `IGitOperations`
- `IContainerImageService`

---

## SharedResourceBuildService

### Description

Service that ensures all shared resource containers are built before the AppHost starts. Subscribes to the `BeforeStartEvent` to process all container resources annotated with `SharedResourceAnnotation`.

### Class Definition

```csharp
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

    private List<(ContainerResource Resource, SharedResourceAnnotation Annotation)> GetSharedResources(
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

    private async Task ProcessSharedResourceAsync(
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

    private string SubstitutePlaceholders(
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

    private void UpdateContainerImageTag(
        ContainerResource resource,
        string imageName,
        string imageTag)
    {
        // Update the container resource to use the built image
        // This modifies the resource's image reference to include the commit SHA tag

        // Implementation note: This uses reflection or internal Aspire APIs
        // to update the container image reference. The exact implementation
        // depends on Aspire internals.

        _logger.LogDebug(
            "Updated container resource {Name} to use image {Image}:{Tag}",
            resource.Name, imageName, imageTag);
    }
}
```

---

## SharedResourceError

### Description

Represents an error that occurred while processing a shared resource.

### Class Definition

```csharp
/// <summary>
/// Represents an error that occurred while processing a shared resource.
/// </summary>
public class SharedResourceError
{
    /// <summary>
    /// The service name that failed.
    /// </summary>
    public string ServiceName { get; }

    /// <summary>
    /// The GitHub repository associated with the failure.
    /// </summary>
    public string GitHubRepository { get; }

    /// <summary>
    /// The exception that caused the failure.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Initializes a new instance of the SharedResourceError class.
    /// </summary>
    public SharedResourceError(
        string serviceName,
        string gitHubRepository,
        Exception exception)
    {
        ServiceName = serviceName;
        GitHubRepository = gitHubRepository;
        Exception = exception;
    }
}
```

---

## SharedResourceBuildException

### Description

Exception thrown when one or more shared resources fail to build.

### Class Definition

```csharp
/// <summary>
/// Exception thrown when one or more shared resources fail to build.
/// </summary>
[Serializable]
public class SharedResourceBuildException : Exception
{
    /// <summary>
    /// The list of errors that occurred during processing.
    /// </summary>
    public IReadOnlyList<SharedResourceError> Errors { get; }

    /// <summary>
    /// Initializes a new instance with a single error message.
    /// </summary>
    public SharedResourceBuildException(string message)
        : base(message)
    {
        Errors = Array.Empty<SharedResourceError>();
    }

    /// <summary>
    /// Initializes a new instance with a list of errors.
    /// </summary>
    public SharedResourceBuildException(IEnumerable<SharedResourceError> errors)
        : base(FormatMessage(errors))
    {
        Errors = errors.ToList().AsReadOnly();
    }

    private static string FormatMessage(IEnumerable<SharedResourceError> errors)
    {
        var errorList = errors.ToList();

        if (errorList.Count == 1)
        {
            var error = errorList[0];
            return $"Failed to build shared resource '{error.ServiceName}': {error.Exception.Message}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Failed to build {errorList.Count} shared resources:");

        foreach (var error in errorList)
        {
            builder.AppendLine($"  - {error.ServiceName}: {error.Exception.Message}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gets a detailed message with all errors and their details.
    /// </summary>
    public string GetDetailedMessage()
    {
        if (Errors.Count == 0)
        {
            return Message;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Shared Resource Build Failures:");
        builder.AppendLine();

        foreach (var error in Errors)
        {
            builder.AppendLine($"Service: {error.ServiceName}");
            builder.AppendLine($"Repository: {error.GitHubRepository}");
            builder.AppendLine($"Error: {error.Exception.Message}");

            if (error.Exception is ContainerBuildException buildEx)
            {
                builder.AppendLine("Build Output:");
                builder.AppendLine(buildEx.BuildOutput);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
```

---

## Registration

### Description

The `SharedResourceBuildService` must be registered with the DI container and subscribed to the `BeforeStartEvent`.

### Extension Method

```csharp
/// <summary>
/// Adds shared resource support to the distributed application.
/// </summary>
/// <remarks>
/// This method registers all required services and subscribes to
/// the BeforeStartEvent for automatic image building.
/// </remarks>
/// <param name="builder">The distributed application builder.</param>
/// <returns>The builder for chaining.</returns>
public static IDistributedApplicationBuilder AddSharedResourceSupport(
    this IDistributedApplicationBuilder builder)
{
    ArgumentNullException.ThrowIfNull(builder);

    // Bind configuration
    builder.Services.Configure<SharedResourceConfiguration>(
        builder.Configuration.GetSection(SharedResourceConfiguration.SectionName));

    // Register core services
    builder.Services.TryAddSingleton<IRepositoryPathResolver, RepositoryPathResolver>();
    builder.Services.TryAddSingleton<IGitOperations, GitOperations>();
    builder.Services.TryAddSingleton<IContainerImageService, ContainerImageService>();
    builder.Services.TryAddSingleton<SharedResourceBuildService>();

    // Subscribe to BeforeStartEvent
    builder.Eventing.Subscribe<BeforeStartEvent>(
        async (@event, cancellationToken) =>
        {
            var service = @event.Services.GetRequiredService<SharedResourceBuildService>();
            await service.OnBeforeStartAsync(@event, cancellationToken);
        });

    return builder;
}
```

---

## Processing Flow

### Sequence Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           AppHost Startup                                │
├─────────────────────────────────────────────────────────────────────────┤
│  1. Register resources with SharedResourceAnnotation                     │
│  2. BeforeStartEvent fires                                               │
│  3. SharedResourceBuildService.OnBeforeStartAsync called                 │
│  4. For each resource with SharedResourceAnnotation:                     │
│     a. ResolveRepositoryPathAsync (user-secrets/env/prompt)             │
│     b. GetCurrentCommitShaAsync (7-char SHA)                            │
│     c. HasUncommittedChangesAsync (log warning if dirty)                │
│     d. ImageExistsLocallyAsync (docker inspect)                         │
│     e. If not exists: BuildImageAsync (execute build command)           │
│     f. UpdateContainerImageTag (set resource to use built image)        │
│  5. AppHost continues startup with all images available                  │
└─────────────────────────────────────────────────────────────────────────┘
```

### State Transitions

| Step | State | Action | Next State |
|------|-------|--------|------------|
| 1 | Start | Validate Docker | Ready / Error |
| 2 | Ready | Find shared resources | Processing |
| 3 | Processing | Resolve path | Path resolved |
| 4 | Path resolved | Get commit SHA | SHA obtained |
| 5 | SHA obtained | Check image exists | Exists / Missing |
| 6 | Missing | Build image | Built / Error |
| 7 | Built | Update resource | Complete |
| 8 | Exists | Update resource | Complete |

---

## Usage Example

### Complete AppHost Setup

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Enable shared resource support (must come first)
builder.AddSharedResourceSupport();

// Add containers with shared resource metadata
builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-1",
        serviceName: "api-1",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
        })
    .WithHttpEndpoint(5001, name: "http");

builder.AddContainer("api-2", "api-2")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-2",
        serviceName: "api-2",
        imageBuildCommand: "docker build -t {ImageName}:{ImageTag} {RepoPath}")
    .WithHttpEndpoint(5002, name: "http");

// When Build() is called, BeforeStartEvent fires and images are built
builder.Build().Run();
```

### Expected Console Output

#### First Run (No Images)

```
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Checking shared resources...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Found 2 shared resource(s)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-1: repo at /home/user/repos/repo-1 (commit: abc1234)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Building api-1:abc1234...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-1:abc1234 built successfully
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-2: repo at /home/user/repos/repo-2 (commit: def5678)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Building api-2:def5678...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-2:def5678 built successfully
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      All shared resources ready
```

#### Subsequent Run (Images Exist)

```
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Checking shared resources...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Found 2 shared resource(s)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-1: repo at /home/user/repos/repo-1 (commit: abc1234)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-1:abc1234 exists
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-2: repo at /home/user/repos/repo-2 (commit: def5678)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-2:def5678 exists
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      All shared resources ready
```

#### With Uncommitted Changes

```
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-1: repo at /home/user/repos/repo-1 (commit: abc1234)
warn: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Repository api-1 has uncommitted changes. Image will be tagged with current HEAD SHA (abc1234)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-1:abc1234 exists
```

---

## Error Cases

| Scenario | Exception | Handling |
|----------|-----------|----------|
| Docker not running | `SharedResourceBuildException` | "Docker is not available..." |
| Repository path not found | `RepositoryNotFoundException` | Detailed setup instructions |
| Path is not git repo | `InvalidOperationException` | "Path is not a git repository" |
| Build command fails | `ContainerBuildException` | Full build output in exception |
| Git operation fails | `GitOperationException` | Command and stderr details |
| Multiple failures | `SharedResourceBuildException` | Aggregates all errors |
| User cancels | `OperationCanceledException` | Propagates cancellation |

---

## Placeholder Substitution

| Placeholder | Source | Example |
|-------------|--------|---------|
| `{RepoPath}` | Resolved repository path | `/home/user/repos/repo-1` |
| `{ProjectPath}` | `RepoPath` + `annotation.ProjectPath` | `/home/user/repos/repo-1/src/Api/Api.csproj` |
| `{ImageName}` | `annotation.ImageName` or `annotation.ServiceName` | `api-1` |
| `{ImageTag}` | Current commit SHA (7 chars) | `abc1234` |

---

## Thread Safety

- Service is registered as singleton
- Processing is sequential per event (no parallel builds by default)
- Individual service methods are thread-safe
- Lock mechanisms prevent concurrent builds of same repository (future enhancement)

---

## Performance Considerations

- Docker image existence check is fast (< 1 second)
- Build times depend on project complexity
- Sequential processing prevents resource contention
- Cached layers speed up rebuilds
- Consider parallel builds for independent repositories (future enhancement)
