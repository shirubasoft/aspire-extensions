namespace Aspire.Hosting.SharedResources;

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
