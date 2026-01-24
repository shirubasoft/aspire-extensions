using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.SharedResources;

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

    /// <summary>
    /// Validates the GitHub repository format.
    /// </summary>
    /// <param name="gitHubRepository">The GitHub repository string to validate.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the repository format is invalid.
    /// </exception>
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
