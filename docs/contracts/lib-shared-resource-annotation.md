# Contract: SharedResourceAnnotation and Options

## Overview

This contract defines the annotation and configuration classes used to mark container resources as shared resources from external repositories.

## Namespace

```csharp
namespace Aspire.Hosting.SharedResources;
```

## Dependencies

- `Aspire.Hosting` (13.1.0+)

---

## SharedResourceAnnotation

### Description

Annotation that marks a container resource as coming from an external repository. Contains metadata needed to locate source code and build container images.

### Interface Implementation

Implements `IResourceAnnotation` from Aspire.Hosting.

### Class Definition

```csharp
/// <summary>
/// Annotation that marks a container resource as coming from an external repository.
/// Contains metadata needed to locate source code and build container images.
/// </summary>
/// <remarks>
/// This annotation is processed by <see cref="SharedResourceBuildService"/> during
/// the BeforeStartEvent to ensure container images are built before the AppHost starts.
/// </remarks>
public class SharedResourceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// GitHub repository in format "orgname/reponame" (e.g., "myorg/api-service").
    /// </summary>
    /// <remarks>
    /// Used as a unique identifier for path resolution and potential git operations.
    /// The repository name (after the slash) is used for path discovery when using
    /// RepositoriesBasePath configuration.
    /// </remarks>
    /// <example>
    /// <code>
    /// GitHubRepository = "myorg/api-service"
    /// </code>
    /// </example>
    public required string GitHubRepository { get; init; }

    /// <summary>
    /// Logical service name used for logging, identification, and configuration lookup.
    /// </summary>
    /// <remarks>
    /// This name is used as the key when looking up repository paths in configuration:
    /// <c>SharedResources:RepositoryPaths:{ServiceName}</c>
    /// </remarks>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Default branch name (e.g., "main", "master").
    /// </summary>
    /// <remarks>
    /// Reserved for future use when cloning or fetching latest changes.
    /// Currently not used in build operations.
    /// </remarks>
    /// <value>Defaults to "main".</value>
    public string DefaultBranch { get; init; } = "main";

    /// <summary>
    /// Command template to build the container image.
    /// </summary>
    /// <remarks>
    /// Supports the following placeholders that are substituted at build time:
    /// <list type="table">
    ///   <listheader>
    ///     <term>Placeholder</term>
    ///     <description>Substitution</description>
    ///   </listheader>
    ///   <item>
    ///     <term>{ProjectPath}</term>
    ///     <description>Full path to the project file (RepoPath + ProjectPath property)</description>
    ///   </item>
    ///   <item>
    ///     <term>{ImageName}</term>
    ///     <description>Container image name (ImageName property or ServiceName)</description>
    ///   </item>
    ///   <item>
    ///     <term>{ImageTag}</term>
    ///     <description>The commit SHA (7 characters) of the repository HEAD</description>
    ///   </item>
    ///   <item>
    ///     <term>{RepoPath}</term>
    ///     <description>Absolute path to the repository root directory</description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// ImageBuildCommand = "dotnet publish {ProjectPath} --os linux --arch x64 /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}"
    /// </code>
    /// </example>
    public required string ImageBuildCommand { get; init; }

    /// <summary>
    /// Path to the project file relative to repository root.
    /// </summary>
    /// <remarks>
    /// If null, the build command must not use the {ProjectPath} placeholder.
    /// Path separators should use forward slashes for cross-platform compatibility.
    /// </remarks>
    /// <example>
    /// <code>
    /// ProjectPath = "src/Api/Api.csproj"
    /// </code>
    /// </example>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Container image name (without tag).
    /// </summary>
    /// <remarks>
    /// If not specified, defaults to <see cref="ServiceName"/>.
    /// Should not include a registry prefix or tag suffix.
    /// </remarks>
    /// <example>
    /// <code>
    /// ImageName = "my-api-service"
    /// </code>
    /// </example>
    public string? ImageName { get; init; }

    /// <summary>
    /// Gets the effective image name to use for container operations.
    /// </summary>
    /// <returns>The ImageName if specified, otherwise the ServiceName.</returns>
    public string GetEffectiveImageName() => ImageName ?? ServiceName;
}
```

### Properties Summary

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `GitHubRepository` | `string` | Yes | - | Repository identifier in "org/repo" format |
| `ServiceName` | `string` | Yes | - | Logical name for logging and config lookup |
| `DefaultBranch` | `string` | No | `"main"` | Default branch name |
| `ImageBuildCommand` | `string` | Yes | - | Command template with placeholders |
| `ProjectPath` | `string?` | No | `null` | Relative path to project file |
| `ImageName` | `string?` | No | `null` | Container image name (defaults to ServiceName) |

---

## SharedResourceOptions

### Description

Configuration options for the `WithSharedResourceMetadata` extension method. Provides a fluent configuration experience.

### Class Definition

```csharp
/// <summary>
/// Options for configuring shared resource metadata.
/// </summary>
/// <remarks>
/// Used with the <see cref="SharedResourceExtensions.WithSharedResourceMetadata"/>
/// extension method to provide optional configuration.
/// </remarks>
public class SharedResourceOptions
{
    /// <summary>
    /// Default branch name (e.g., "main", "master").
    /// </summary>
    /// <value>Defaults to "main".</value>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Path to the project file relative to repository root.
    /// </summary>
    /// <remarks>
    /// Use forward slashes for cross-platform compatibility.
    /// </remarks>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// Container image name (without tag).
    /// </summary>
    /// <remarks>
    /// If not set, the service name from the extension method is used.
    /// </remarks>
    public string? ImageName { get; set; }
}
```

### Properties Summary

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DefaultBranch` | `string` | `"main"` | Default git branch |
| `ProjectPath` | `string?` | `null` | Relative path to project file |
| `ImageName` | `string?` | `null` | Override for container image name |

---

## SharedResourceConfiguration

### Description

Configuration class for binding shared resource settings from IConfiguration.

### Class Definition

```csharp
/// <summary>
/// Configuration for repository paths and shared resource behavior.
/// </summary>
/// <remarks>
/// Can be configured via:
/// <list type="bullet">
///   <item>User secrets: <c>dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/path"</c></item>
///   <item>Environment variables: <c>SHAREDRESOURCES__REPOSITORIESBASEPATH=/path</c></item>
///   <item>appsettings.json: Under the "SharedResources" section</item>
/// </list>
/// </remarks>
public class SharedResourceConfiguration
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "SharedResources";

    /// <summary>
    /// Base path where repositories are cloned/located.
    /// </summary>
    /// <remarks>
    /// When set, repository paths are resolved as: {RepositoriesBasePath}/{repo-name}
    /// where repo-name is extracted from GitHubRepository (the part after the slash).
    /// </remarks>
    /// <example>
    /// <code>
    /// RepositoriesBasePath = "/home/user/repos"
    /// // For GitHubRepository = "myorg/api-service"
    /// // Resolves to: /home/user/repos/api-service
    /// </code>
    /// </example>
    public string? RepositoriesBasePath { get; set; }

    /// <summary>
    /// Whether to prompt user for missing repository paths.
    /// </summary>
    /// <remarks>
    /// When true (default), uses IInteractionService to prompt for paths.
    /// When false, throws <see cref="RepositoryNotFoundException"/> immediately.
    /// Should be set to false in CI/CD environments.
    /// </remarks>
    /// <value>Defaults to true.</value>
    public bool PromptForMissingPaths { get; set; } = true;

    /// <summary>
    /// Dictionary of explicit repository paths keyed by service name.
    /// </summary>
    /// <remarks>
    /// Configured via: <c>SharedResources:RepositoryPaths:{ServiceName}</c>
    /// Takes precedence over RepositoriesBasePath resolution.
    /// </remarks>
    public Dictionary<string, string> RepositoryPaths { get; set; } = new();
}
```

### Configuration Priority

1. **Explicit path** in `RepositoryPaths[ServiceName]` (highest priority)
2. **Environment variable** `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}`
3. **Base path** `RepositoriesBasePath` + repository name
4. **User prompt** via IInteractionService (lowest priority, interactive only)

---

## Usage Examples

### Basic Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-1",
        serviceName: "api-1",
        imageBuildCommand: "dotnet publish {ProjectPath} /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
            options.DefaultBranch = "main";
        });
```

### Direct Annotation Usage

```csharp
var annotation = new SharedResourceAnnotation
{
    GitHubRepository = "myorg/repo-1",
    ServiceName = "api-1",
    ImageBuildCommand = "docker build -t {ImageName}:{ImageTag} {RepoPath}",
    ProjectPath = "src/Api/Api.csproj",
    DefaultBranch = "main",
    ImageName = "my-custom-image-name"
};

builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(annotation);
```

---

## Validation Rules

1. **GitHubRepository** must be in "org/repo" format (contain exactly one `/`)
2. **ServiceName** must be non-empty and contain only valid identifier characters
3. **ImageBuildCommand** must be non-empty
4. **ProjectPath** if specified, must use forward slashes and not start with `/`
5. **ImageName** if specified, must be a valid Docker image name (lowercase, alphanumeric, hyphens, underscores)

---

## Error Cases

| Scenario | Exception | Message |
|----------|-----------|---------|
| Empty GitHubRepository | `ArgumentException` | "GitHubRepository is required and must be in 'org/repo' format" |
| Invalid GitHubRepository format | `ArgumentException` | "GitHubRepository must be in 'org/repo' format" |
| Empty ServiceName | `ArgumentException` | "ServiceName is required" |
| Empty ImageBuildCommand | `ArgumentException` | "ImageBuildCommand is required" |
| Invalid ImageName | `ArgumentException` | "ImageName must be a valid Docker image name" |
