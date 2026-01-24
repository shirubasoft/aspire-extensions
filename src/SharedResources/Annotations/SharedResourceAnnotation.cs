using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.SharedResources;

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
