# Contract: SharedResourceExtensions

## Overview

This contract defines extension methods for `IResourceBuilder<ContainerResource>` that allow marking container resources with shared resource metadata for automatic image building.

## Namespace

```csharp
namespace Aspire.Hosting.SharedResources;
```

## Dependencies

- `Aspire.Hosting` (13.1.0+)
- `SharedResourceAnnotation`
- `SharedResourceOptions`
- `SharedResourceBuildService`

---

## SharedResourceExtensions

### Description

Static class providing extension methods to configure container resources as shared resources from external repositories.

### Class Definition

```csharp
/// <summary>
/// Extension methods for configuring shared resources in Aspire applications.
/// </summary>
public static class SharedResourceExtensions
{
    /// <summary>
    /// Marks a container resource as a shared resource from an external repository.
    /// </summary>
    /// <param name="builder">The resource builder for the container.</param>
    /// <param name="gitHubRepository">
    /// GitHub repository in format "orgname/reponame" (e.g., "myorg/api-service").
    /// </param>
    /// <param name="serviceName">
    /// Logical service name used for logging and configuration lookup.
    /// </param>
    /// <param name="imageBuildCommand">
    /// Command template to build the container image.
    /// Supports placeholders: {ProjectPath}, {ImageName}, {ImageTag}, {RepoPath}
    /// </param>
    /// <param name="configure">
    /// Optional action to configure additional options like ProjectPath, DefaultBranch, and ImageName.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when builder, gitHubRepository, serviceName, or imageBuildCommand is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when gitHubRepository is not in valid "org/repo" format.
    /// </exception>
    /// <example>
    /// <code>
    /// builder.AddContainer("api-1", "api-1")
    ///     .WithSharedResourceMetadata(
    ///         gitHubRepository: "myorg/repo-1",
    ///         serviceName: "api-1",
    ///         imageBuildCommand: "dotnet publish {ProjectPath} /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
    ///         options =>
    ///         {
    ///             options.ProjectPath = "src/Api/Api.csproj";
    ///             options.DefaultBranch = "main";
    ///         });
    /// </code>
    /// </example>
    public static IResourceBuilder<ContainerResource> WithSharedResourceMetadata(
        this IResourceBuilder<ContainerResource> builder,
        string gitHubRepository,
        string serviceName,
        string imageBuildCommand,
        Action<SharedResourceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(gitHubRepository);
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(imageBuildCommand);

        ValidateGitHubRepository(gitHubRepository);

        var options = new SharedResourceOptions();
        configure?.Invoke(options);

        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = gitHubRepository,
            ServiceName = serviceName,
            ImageBuildCommand = imageBuildCommand,
            DefaultBranch = options.DefaultBranch,
            ProjectPath = options.ProjectPath,
            ImageName = options.ImageName
        };

        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Marks a container resource as a shared resource using a preconfigured annotation.
    /// </summary>
    /// <param name="builder">The resource builder for the container.</param>
    /// <param name="annotation">
    /// A preconfigured SharedResourceAnnotation with all required metadata.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when builder or annotation is null.
    /// </exception>
    /// <example>
    /// <code>
    /// var annotation = new SharedResourceAnnotation
    /// {
    ///     GitHubRepository = "myorg/repo-1",
    ///     ServiceName = "api-1",
    ///     ImageBuildCommand = "docker build -t {ImageName}:{ImageTag} .",
    ///     ProjectPath = "src/Api"
    /// };
    ///
    /// builder.AddContainer("api-1", "api-1")
    ///     .WithSharedResourceMetadata(annotation);
    /// </code>
    /// </example>
    public static IResourceBuilder<ContainerResource> WithSharedResourceMetadata(
        this IResourceBuilder<ContainerResource> builder,
        SharedResourceAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(annotation);

        return builder.WithAnnotation(annotation);
    }

    private static void ValidateGitHubRepository(string gitHubRepository)
    {
        if (string.IsNullOrWhiteSpace(gitHubRepository))
        {
            throw new ArgumentException(
                "GitHubRepository is required and must be in 'org/repo' format",
                nameof(gitHubRepository));
        }

        var parts = gitHubRepository.Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException(
                "GitHubRepository must be in 'org/repo' format",
                nameof(gitHubRepository));
        }
    }
}
```

---

## AddSharedResourceSupport Extension

### Description

Extension method on `IDistributedApplicationBuilder` to register the shared resource build service and event subscriber.

### Method Definition

```csharp
/// <summary>
/// Adds shared resource support to the distributed application.
/// </summary>
/// <remarks>
/// This method registers:
/// <list type="bullet">
///   <item>Configuration binding for SharedResourceConfiguration</item>
///   <item>IRepositoryPathResolver singleton service</item>
///   <item>IGitOperations singleton service</item>
///   <item>IContainerImageService singleton service</item>
///   <item>SharedResourceBuildService singleton</item>
///   <item>BeforeStartEvent subscriber for automatic image building</item>
/// </list>
/// Must be called before any resources are added that use WithSharedResourceMetadata.
/// </remarks>
/// <param name="builder">The distributed application builder.</param>
/// <returns>The builder for chaining.</returns>
/// <exception cref="ArgumentNullException">Thrown when builder is null.</exception>
/// <example>
/// <code>
/// var builder = DistributedApplication.CreateBuilder(args);
///
/// // Enable shared resource support
/// builder.AddSharedResourceSupport();
///
/// // Now add containers with shared resource metadata
/// builder.AddContainer("api-1", "api-1")
///     .WithSharedResourceMetadata(...);
///
/// builder.Build().Run();
/// </code>
/// </example>
public static IDistributedApplicationBuilder AddSharedResourceSupport(
    this IDistributedApplicationBuilder builder)
{
    ArgumentNullException.ThrowIfNull(builder);

    // Bind configuration
    builder.Services.Configure<SharedResourceConfiguration>(
        builder.Configuration.GetSection(SharedResourceConfiguration.SectionName));

    // Register services
    builder.Services.AddSingleton<IRepositoryPathResolver, RepositoryPathResolver>();
    builder.Services.AddSingleton<IGitOperations, GitOperations>();
    builder.Services.AddSingleton<IContainerImageService, ContainerImageService>();
    builder.Services.AddSingleton<SharedResourceBuildService>();

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

## Method Signatures Summary

| Method | Parameters | Returns | Description |
|--------|------------|---------|-------------|
| `WithSharedResourceMetadata` | `builder`, `gitHubRepository`, `serviceName`, `imageBuildCommand`, `configure?` | `IResourceBuilder<ContainerResource>` | Configure with inline options |
| `WithSharedResourceMetadata` | `builder`, `annotation` | `IResourceBuilder<ContainerResource>` | Configure with pre-built annotation |
| `AddSharedResourceSupport` | `builder` | `IDistributedApplicationBuilder` | Register services and event subscriber |

---

## Usage Examples

### Complete AppHost Setup

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Step 1: Enable shared resource support (must come first)
builder.AddSharedResourceSupport();

// Step 2: Add containers with shared resource metadata
var api1 = builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-1",
        serviceName: "api-1",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux --arch x64 /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
        })
    .WithHttpEndpoint(port: 5001, name: "http");

var api2 = builder.AddContainer("api-2", "api-2")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-2",
        serviceName: "api-2",
        imageBuildCommand: "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/Dockerfile {RepoPath}",
        options =>
        {
            options.ImageName = "custom-api-2-image";
        })
    .WithHttpEndpoint(port: 5002, name: "http");

builder.Build().Run();
```

### Using Shared Resource Library Pattern

```csharp
// In a shared library (e.g., Api1Resource.cs)
public static class Api1Resource
{
    private const string GitHubRepo = "myorg/repo-1";
    private const string ServiceName = "api-1";
    private const string ProjectPath = "api/Api.csproj";
    private const string ImageBuildCommand =
        "dotnet publish {ProjectPath} --os linux --arch x64 /t:PublishContainer " +
        "-p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}";

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

    private static IResourceBuilder<ContainerResource> Configure(
        this IResourceBuilder<ContainerResource> builder)
    {
        return builder
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithHttpHealthCheck("/health");
    }
}
```

---

## Error Cases

| Scenario | Exception | Message |
|----------|-----------|---------|
| Null builder | `ArgumentNullException` | "Value cannot be null. (Parameter 'builder')" |
| Null gitHubRepository | `ArgumentNullException` | "Value cannot be null. (Parameter 'gitHubRepository')" |
| Empty gitHubRepository | `ArgumentException` | "GitHubRepository is required and must be in 'org/repo' format" |
| Invalid gitHubRepository format | `ArgumentException` | "GitHubRepository must be in 'org/repo' format" |
| Null serviceName | `ArgumentNullException` | "Value cannot be null. (Parameter 'serviceName')" |
| Null imageBuildCommand | `ArgumentNullException` | "Value cannot be null. (Parameter 'imageBuildCommand')" |
| Null annotation | `ArgumentNullException` | "Value cannot be null. (Parameter 'annotation')" |

---

## Thread Safety

All extension methods are thread-safe and can be called concurrently. The registered services are singletons and handle their own thread safety.

---

## Chaining Support

Both `WithSharedResourceMetadata` overloads return `IResourceBuilder<ContainerResource>`, enabling fluent chaining:

```csharp
builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(...)
    .WithEnvironment("KEY", "value")
    .WithHttpEndpoint(5001)
    .WithReference(database);
```
