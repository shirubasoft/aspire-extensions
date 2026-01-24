namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Service responsible for resolving repository paths from configuration,
/// environment variables, or user interaction.
/// </summary>
/// <remarks>
/// <para>
/// The repository path resolver uses a priority-based resolution strategy:
/// </para>
/// <list type="number">
///   <item>Explicit path in <c>RepositoryPaths[ServiceName]</c> configuration</item>
///   <item>Environment variable <c>SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}</c></item>
///   <item><c>RepositoriesBasePath</c> + repository name (derived from GitHubRepository)</item>
///   <item>User prompt via <see cref="Aspire.Hosting.IInteractionService"/> (if <see cref="SharedResourceConfiguration.PromptForMissingPaths"/> is true)</item>
/// </list>
/// </remarks>
public interface IRepositoryPathResolver
{
    /// <summary>
    /// Resolves the local filesystem path for a repository.
    /// </summary>
    /// <param name="gitHubRepository">Repository in "orgname/reponame" format.</param>
    /// <param name="serviceName">Service name for configuration lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Absolute path to the repository root directory.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="gitHubRepository"/> or <paramref name="serviceName"/> is null.
    /// </exception>
    /// <exception cref="RepositoryNotFoundException">
    /// Thrown when the repository path cannot be resolved or does not exist.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled.
    /// </exception>
    /// <example>
    /// <code>
    /// var resolver = serviceProvider.GetRequiredService&lt;IRepositoryPathResolver&gt;();
    /// var path = await resolver.ResolveRepositoryPathAsync(
    ///     "myorg/api-service",
    ///     "api-service",
    ///     cancellationToken);
    /// </code>
    /// </example>
    Task<string> ResolveRepositoryPathAsync(
        string gitHubRepository,
        string serviceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a repository path is configured or discoverable without user interaction.
    /// </summary>
    /// <param name="gitHubRepository">Repository in "orgname/reponame" format.</param>
    /// <param name="serviceName">Service name for configuration lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the repository path can be resolved without prompting; otherwise, false.</returns>
    /// <remarks>
    /// This method does not prompt the user for input, even if
    /// <see cref="SharedResourceConfiguration.PromptForMissingPaths"/> is true.
    /// It only checks configuration sources and filesystem existence.
    /// </remarks>
    /// <example>
    /// <code>
    /// var isAvailable = await resolver.IsRepositoryAvailableAsync(
    ///     "myorg/api-service",
    ///     "api-service",
    ///     cancellationToken);
    /// if (!isAvailable)
    /// {
    ///     logger.LogWarning("Repository not configured, will prompt user during startup");
    /// }
    /// </code>
    /// </example>
    Task<bool> IsRepositoryAvailableAsync(
        string gitHubRepository,
        string serviceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a repository path to user secrets for future use.
    /// </summary>
    /// <param name="serviceName">Service name as the configuration key.</param>
    /// <param name="repositoryPath">Absolute path to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="serviceName"/> or <paramref name="repositoryPath"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when user secrets cannot be accessed or modified.
    /// </exception>
    /// <remarks>
    /// This method uses the <c>dotnet user-secrets set</c> command to persist the path.
    /// The path is saved under the key <c>SharedResources:RepositoryPaths:{serviceName}</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// await resolver.SaveRepositoryPathAsync(
    ///     "api-service",
    ///     "/home/user/repos/api-service",
    ///     cancellationToken);
    /// </code>
    /// </example>
    Task SaveRepositoryPathAsync(
        string serviceName,
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
