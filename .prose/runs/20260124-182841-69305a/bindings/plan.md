# plan

kind: let

source:
```prose
let plan = session "Read PLAN.md"
```

---

# Implementation Plan: Automatic Container Building via Aspire Eventing

## Overview

This plan describes an eventing-based approach to automatically build container images for services from external repositories. The system will use annotations to declare service metadata and an eventing subscriber to ensure images are built before the AppHost starts.

---

## Tech Stack

- .NET 10
- Aspire 13.1.0
- TUnit 1.12.43 - testing framework
- CliWrap 3.10.0 - CLI process execution (git, docker commands)

### Key Decisions

- **Git operations**: Use CliWrap to shell out to git CLI for commit SHA retrieval and working tree status
- **Docker operations**: Use CliWrap to shell out to docker CLI for image inspection and builds
- **Dirty working tree**: Log warning but use same SHA tag (no `-dirty` suffix) - simplifies caching and avoids tag proliferation

### Alternatives Considered

| Component | Alternative | Decision | Rationale |
|-----------|-------------|----------|-----------|
| Git operations | LibGit2Sharp | CliWrap + git CLI | Consistency with docker operations, no native dependencies, simpler deployment |
| Docker operations | Docker.DotNet | CliWrap + docker CLI | Simpler implementation, no additional SDK dependency, consistent with git approach |
| Process execution | MedallionShell | CliWrap | Active maintenance, streaming support, better async/cancellation support |

## Expectations

- Library with CI/CD and ability to publish nuget package locally
- Sample AppHost demonstrating usage
- Comprehensive documentation in the /docs folder and README.md

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           AppHost Startup                                │
├─────────────────────────────────────────────────────────────────────────┤
│  1. Register resources with SharedResourceAnnotation                     │
│  2. BeforeStartEvent fires                                               │
│  3. SharedResourceBuildSubscriber iterates annotated resources           │
│  4. For each resource:                                                   │
│     a. Resolve repository path (user-secrets or prompt)                  │
│     b. Get current commit SHA                                            │
│     c. Check if image:sha exists locally                                 │
│     d. If not, execute imageBuildCommand                                 │
│  5. AppHost continues startup with all images available                  │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## API Contracts

### 1. SharedResourceAnnotation

```csharp
namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Annotation that marks a container resource as coming from an external repository.
/// Contains metadata needed to locate source code and build container images.
/// </summary>
public class SharedResourceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// GitHub repository in format "orgname/reponame" (e.g., "myorg/api-service").
    /// Used as a unique identifier and for potential git operations.
    /// </summary>
    public required string GitHubRepository { get; init; }

    /// <summary>
    /// Logical service name used for logging and identification.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Default branch name (e.g., "main", "master").
    /// Used when cloning or fetching latest changes.
    /// </summary>
    public string DefaultBranch { get; init; } = "main";

    /// <summary>
    /// Command template to build the container image.
    /// Supports placeholders: {ProjectPath}, {ImageName}, {ImageTag}, {RepoPath}
    /// Example: "dotnet publish {ProjectPath} --os linux --arch x64 /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}"
    /// </summary>
    public required string ImageBuildCommand { get; init; }

    /// <summary>
    /// Path to the project file relative to repository root.
    /// Example: "src/Api/Api.csproj"
    /// If null, the build command must not use {ProjectPath} placeholder.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Container image name (without tag). Defaults to ServiceName if not specified.
    /// </summary>
    public string? ImageName { get; init; }
}
```

### 2. Extension Methods for Resource Builder

```csharp
namespace Aspire.Hosting.SharedResources;

public static class SharedResourceExtensions
{
    /// <summary>
    /// Marks a container resource as a shared resource from an external repository.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithSharedResourceMetadata(
        this IResourceBuilder<ContainerResource> builder,
        string gitHubRepository,
        string serviceName,
        string imageBuildCommand,
        Action<SharedResourceOptions>? configure = null);

    /// <summary>
    /// Marks a container resource as a shared resource using a preconfigured annotation.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithSharedResourceMetadata(
        this IResourceBuilder<ContainerResource> builder,
        SharedResourceAnnotation annotation);
}

/// <summary>
/// Options for configuring shared resource metadata.
/// </summary>
public class SharedResourceOptions
{
    public string DefaultBranch { get; set; } = "main";
    public string? ProjectPath { get; set; }
    public string ImageName { get; set; }
}
```

### 3. Repository Path Configuration

```csharp
namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Configuration for repository paths. Can be set via user-secrets or environment variables.
/// </summary>
public class SharedResourceConfiguration
{
    /// <summary>
    /// Base path where repositories are cloned/located.
    /// Example: "/home/user/repos" or "C:\Users\user\repos"
    /// </summary>
    public string? RepositoriesBasePath { get; set; }

    /// <summary>
    /// Whether to prompt user for missing repository paths.
    /// If false, throws exception when path cannot be resolved.
    /// Default: true
    /// </summary>
    public bool PromptForMissingPaths { get; set; } = true;
}
```

### 4. Repository Path Resolver Service

```csharp
namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service responsible for resolving repository paths from configuration,
/// environment, or user interaction.
/// </summary>
public interface IRepositoryPathResolver
{
    /// <summary>
    /// Resolves the local filesystem path for a repository.
    /// </summary>
    /// <param name="gitHubRepository">Repository in "orgname/reponame" format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Absolute path to the repository root</returns>
    /// <exception cref="RepositoryNotFoundException">If path cannot be resolved</exception>
    Task<string> ResolveRepositoryPathAsync(
        string gitHubRepository,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a repository path is configured or discoverable.
    /// </summary>
    Task<bool> IsRepositoryAvailableAsync(
        string gitHubRepository,
        CancellationToken cancellationToken = default);
}

public class RepositoryNotFoundException : Exception
{
    public string GitHubRepository { get; }
    public RepositoryNotFoundException(string gitHubRepository, string message)
        : base(message)
    {
        GitHubRepository = gitHubRepository;
    }
}
```

### 5. Git Operations Service

```csharp
namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service for git operations needed to determine image tags.
/// </summary>
public interface IGitOperations
{
    /// <summary>
    /// Gets the current commit SHA (short form, 7 characters) for a repository.
    /// </summary>
    Task<string> GetCurrentCommitShaAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current branch name.
    /// </summary>
    Task<string> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
```

### 6. Container Image Service

```csharp
namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service for container image operations.
/// </summary>
public interface IContainerImageService
{
    /// <summary>
    /// Checks if an image with the specified name and tag exists locally.
    /// </summary>
    Task<bool> ImageExistsLocallyAsync(
        string imageName,
        string tag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a container image using the specified command.
    /// </summary>
    /// <param name="command">Full build command to execute</param>
    /// <param name="workingDirectory">Working directory for the command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task BuildImageAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tags an existing image with a new tag.
    /// </summary>
    Task TagImageAsync(
        string sourceImage,
        string targetImage,
        CancellationToken cancellationToken = default);
}
```

### 7. Shared Resource Build Service (Event Subscriber)

```csharp
namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service that ensures all shared resource containers are built
/// before the AppHost starts. Registered as event subscriber.
/// </summary>
public class SharedResourceBuildService
{
    public SharedResourceBuildService(
        IRepositoryPathResolver pathResolver,
        IGitOperations gitOperations,
        IContainerImageService containerService,
        ILogger<SharedResourceBuildService> logger);

    /// <summary>
    /// Handles the BeforeStartEvent to build any missing container images.
    /// </summary>
    public Task OnBeforeStartAsync(
        BeforeStartEvent @event,
        CancellationToken cancellationToken);
}

// Registration in extension method:
public static IDistributedApplicationBuilder AddSharedResourceSupport(
    this IDistributedApplicationBuilder builder)
{
    builder.Services.AddSingleton<SharedResourceBuildService>();
    builder.Eventing.Subscribe<BeforeStartEvent>(
        async (@event, ct) =>
        {
            var service = @event.Services.GetRequiredService<SharedResourceBuildService>();
            await service.OnBeforeStartAsync(@event, ct);
        });
    return builder;
}
```

### 8. Updated Api1Resource / Api2Resource Usage

```csharp
// In repo-1/apphost-resource/Api1Resource.cs
namespace AppHost.Resources;

public static class Api1Resource
{
    private const string GitHubRepo = "myorg/repo-1";
    private const string ServiceName = "api-1";
    private const string ProjectPath = "api/Api.csproj";
    private const string ImageBuildCommand =
        "dotnet publish {ProjectPath} --os linux --arch x64 /t:PublishContainer " +
        "-p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}";

    public static IResourceBuilder<ProjectResource> AddApi1<TProject>(
        this IDistributedApplicationBuilder builder)
        where TProject : IProjectMetadata, new()
    {
        return builder.AddProject<TProject>("api-1")
            .Configure();
    }

    public static IResourceBuilder<ContainerResource> AddApi1AsContainer(
        this IDistributedApplicationBuilder builder)
    {
        return builder.AddContainer("api-1", "api-1")
            .WithSharedResourceMetadata(
                gitHubRepository: GitHubRepo,
                serviceName: ServiceName,
                imageBuildCommand: ImageBuildCommand,
                options =>
                {
                    options.ProjectPath = ProjectPath;
                    options.DefaultBranch = "main";
                })
            .Configure();
    }

    public static IResourceBuilder<TResource> Configure<TResource>(
        this IResourceBuilder<TResource> builder)
        where TResource : IResource, IResourceWithEndpoints, IResourceWithEnvironment
    {
        return builder
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithHttpHealthCheck("/health");
    }
}
```

---

## Expected Behaviors

### Startup Flow

1. **AppHost registers container resources** with `SharedResourceAnnotation`
2. **BeforeStartEvent fires** before any containers are started
3. **SharedResourceBuildSubscriber processes each annotated resource:**

   ```
   For each resource with SharedResourceAnnotation:
   │
   ├─► Resolve repository path
   │   ├─ Check IConfiguration for "SharedResources:RepositoryPaths:{servicename}"
   │   └─ Prompt user via IInteractionService if not found
   │
   ├─► Get current commit SHA from repository
   │   └─ git rev-parse --short HEAD
   │
   ├─► Determine expected image tag
   │   └─ Log warning if working tree is dirty
   │
   ├─► Check if image exists locally
   │   └─ docker image inspect {imageName}:{tag}
   │
   └─► If image doesn't exist:
       ├─ Substitute placeholders in imageBuildCommand
       ├─ Execute build command
       └─ Verify image was created
   ```

### Image Tagging Strategy

| Repository State          | Image Tag Example |
| ------------------------- | ----------------- |
| Clean (on commit abc1234) | `api-1:abc1234`   |
| Detached HEAD             | `api-1:abc1234`   |

### Configuration Resolution Priority

1. **Explicit path in user-secrets** (highest priority)
   ```json
   {
     "SharedResources:RepositoryPaths:servicename": "/custom/path/repo-1"
   }
   ```

2. **Environment variable**
   ```bash
   SHAREDRESOURCES__REPOSITORYPATHS__SERVICENAME=/custom/path/repo-1
   ```

3. **Base path + repository name**
   ```json
   {
     "SharedResources:RepositoriesBasePath": "/home/user/repos"
   }
   ```
   Resolves to: `/home/user/repos/repo-1`

4. **User prompt via IInteractionService** (lowest priority, interactive)

---

## User Interaction Requirements

### First Run (No Configuration)

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Starting AppHost...                                                      │
│                                                                          │
│ ⚠️  Repository path not configured for 'myorg/repo-1'                    │
│                                                                          │
│ Please provide the local path where 'myorg/repo-1' is cloned:            │
│ > /home/user/code/repo-1                                                 │
│                                                                          │
│ 💾 Save this path for future runs? [Y/n]: Y                              │
│                                                                          │
│ ✅ Saved to user-secrets                                                  │
│                                                                          │
│ 🔨 Building api-1:abc1234...                                             │
│    dotnet publish /home/user/code/repo-1/api/Api.csproj ...              │
│                                                                          │
│ ✅ Image api-1:abc1234 built successfully                                 │
│                                                                          │
│ Starting services...                                                     │
└─────────────────────────────────────────────────────────────────────────┘
```

### Subsequent Runs (Configured)

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Starting AppHost...                                                      │
│                                                                          │
│ 📦 Checking shared resources...                                          │
│    ├─ api-1: repo at /home/user/code/repo-1 (commit: abc1234)           │
│    │   └─ ✅ Image api-1:abc1234 exists                                  │
│    └─ api-2: repo at /home/user/code/repo-2 (commit: def5678)           │
│        └─ 🔨 Building api-2:def5678...                                   │
│                                                                          │
│ ✅ All shared resources ready                                             │
│                                                                          │
│ Starting services...                                                     │
└─────────────────────────────────────────────────────────────────────────┘
```

### Repository Not Found (Non-Interactive Mode)

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Starting AppHost...                                                     │
│                                                                         │
│ ❌ Error: Cannot resolve path for service 'servicename'                 │
│                                                                          │
│ Please configure the repository path using one of these methods:         │
│                                                                          │
│ 1. User secrets:                                                         │
│    dotnet user-secrets set "SharedResources:RepositoryPaths:servicename" "/path/to/repo"
│                                                                          │
│ 2. Environment variable:                                                 │
│    export SHAREDRESOURCES__REPOSITORYPATHS__SERVICENAME=/path/to/repo    │
│                                                                          │
│ 3. Base path (for all repos):                                            │
│    dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"
│                                                                          │
│ Or clone the repository:                                                 │
│    git clone https://github.com/myorg/repo-1.git /path/to/repo           │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Manual User Interaction Summary

| Scenario              | User Action Required                             |
| --------------------- | ------------------------------------------------ |
| First run, no config  | Provide repository path when prompted (one-time) |
| Repository not cloned | Clone repository manually                        |
| Path pre-configured   | None (automatic)                                 |
| Image already exists  | None (automatic)                                 |
| New commit pushed     | None (automatic rebuild)                         |
| CI/CD environment     | Set environment variables in pipeline config     |

---

## Files to Create

```
modular-apphosts/
├── shared-resources/                          # New project
│   ├── SharedResources.csproj
│   ├── Annotations/
│   │   └── SharedResourceAnnotation.cs
│   ├── Configuration/
│   │   └── SharedResourceConfiguration.cs
│   ├── Extensions/
│   │   └── SharedResourceExtensions.cs
│   ├── Services/
│   │   ├── IRepositoryPathResolver.cs
│   │   ├── RepositoryPathResolver.cs
│   │   ├── IGitOperations.cs
│   │   ├── GitOperations.cs
│   │   ├── IContainerImageService.cs
│   │   └── ContainerImageService.cs
│   └── Eventing/
│       └── SharedResourceBuildSubscriber.cs
├── repo-1/
│   └── apphost-resource/
│       └── Api1Resource.cs                    # Updated to use annotation
├── repo-2/
│   └── apphost-resource/
│       └── Api2Resource.cs                    # Updated to use annotation
└── e2e/
    └── apphost/
        ├── AppHost.cs                         # Updated to register subscriber
        └── appsettings.Development.json       # Optional: default config
```

---

## Dependencies

```xml
<!-- SharedResources.csproj -->
<ItemGroup>
  <PackageReference Include="Aspire.Hosting" Version="13.1.0" />
</ItemGroup>
```

---

## Edge Cases and Error Handling

| Edge Case                     | Handling                                              |
| ----------------------------- | ----------------------------------------------------- |
| Repository path doesn't exist | Clear error message with setup instructions           |
| Git not installed             | Fallback to "latest" tag or fail with helpful message |
| Docker not running            | Fail with message to start Docker                     |
| Build command fails           | Show build output, suggest manual build               |
| Image name collision          | Use full image name with SHA tag                      |
| Network issues (git fetch)    | Continue with local HEAD, warn user                   |
| Concurrent builds             | Lock per-repository to prevent conflicts              |
| Uncommitted changes           | Log warning                                           |

---

## Future Enhancements (Out of Scope)

- Auto-clone repositories if not present
- Pull latest changes before building
- Integration with GitHub Actions for pre-built images
- Caching layer for faster subsequent builds
- Support for private repositories (SSH keys, tokens)
- Multi-architecture builds (arm64, amd64)
