namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Exception thrown when a repository path cannot be resolved for a shared resource.
/// </summary>
/// <remarks>
/// This exception is thrown by the repository path resolver when it cannot locate
/// the local path for a shared resource's repository. The <see cref="GetDetailedMessage"/>
/// method provides setup instructions to help users configure the repository path.
/// </remarks>
[Serializable]
public class RepositoryNotFoundException : Exception
{
    /// <summary>
    /// Gets the GitHub repository identifier in "owner/repo" format.
    /// </summary>
    /// <example>
    /// <code>
    /// GitHubRepository = "myorg/api-service"
    /// </code>
    /// </example>
    public string GitHubRepository { get; }

    /// <summary>
    /// Gets the logical service name used for configuration lookup.
    /// </summary>
    /// <remarks>
    /// This is the key used to look up the repository path in configuration:
    /// <c>SharedResources:RepositoryPaths:{ServiceName}</c>
    /// </remarks>
    public string ServiceName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="gitHubRepository">The GitHub repository identifier in "owner/repo" format.</param>
    /// <param name="serviceName">The logical service name used for configuration lookup.</param>
    public RepositoryNotFoundException(string message, string gitHubRepository, string serviceName)
        : base(message)
    {
        GitHubRepository = gitHubRepository;
        ServiceName = serviceName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The error message that describes the issue.</param>
    /// <param name="gitHubRepository">The GitHub repository identifier in "owner/repo" format.</param>
    /// <param name="serviceName">The logical service name used for configuration lookup.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public RepositoryNotFoundException(string message, string gitHubRepository, string serviceName, Exception innerException)
        : base(message, innerException)
    {
        GitHubRepository = gitHubRepository;
        ServiceName = serviceName;
    }

    /// <summary>
    /// Gets a detailed message with setup instructions for resolving the repository path.
    /// </summary>
    /// <returns>A detailed message including configuration instructions.</returns>
    /// <remarks>
    /// The detailed message includes:
    /// <list type="bullet">
    ///   <item>The original error message</item>
    ///   <item>Instructions for setting up the repository path via configuration</item>
    ///   <item>Example configuration JSON</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// try
    /// {
    ///     // Resolve repository path
    /// }
    /// catch (RepositoryNotFoundException ex)
    /// {
    ///     Console.WriteLine(ex.GetDetailedMessage());
    /// }
    /// </code>
    /// </example>
    public string GetDetailedMessage()
    {
        var repoName = GetRepositoryName();
        return $$"""
            {{Message}}

            Repository: {{GitHubRepository}}
            Service: {{ServiceName}}

            To resolve this issue, configure the repository path using one of these methods:

            1. Set the path explicitly in appsettings.json:
               {
                 "SharedResources": {
                   "RepositoryPaths": {
                     "{{ServiceName}}": "/path/to/your/local/clone"
                   }
                 }
               }

            2. Set the base path for all repositories:
               {
                 "SharedResources": {
                   "RepositoriesBasePath": "/path/to/repos"
                 }
               }
               Then clone the repository to: /path/to/repos/{{repoName}}

            3. Clone the repository adjacent to this AppHost project with the name: {{repoName}}
            """;
    }

    private string GetRepositoryName()
    {
        var slashIndex = GitHubRepository.IndexOf('/');
        return slashIndex >= 0 ? GitHubRepository[(slashIndex + 1)..] : GitHubRepository;
    }
}
