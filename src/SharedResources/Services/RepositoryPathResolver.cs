using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CliWrapLib = CliWrap;

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is for evaluation purposes

namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Default implementation of <see cref="IRepositoryPathResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Resolution priority:
/// </para>
/// <list type="number">
///   <item>Explicit path in <c>RepositoryPaths[ServiceName]</c> configuration</item>
///   <item>Environment variable <c>SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}</c></item>
///   <item><c>RepositoriesBasePath</c> + repository name (from GitHubRepository)</item>
///   <item>User prompt via <see cref="IInteractionService"/> (if <see cref="SharedResourceConfiguration.PromptForMissingPaths"/> is true)</item>
/// </list>
/// <para>
/// This implementation is thread-safe and can be registered as a singleton.
/// </para>
/// </remarks>
public class RepositoryPathResolver : IRepositoryPathResolver
{
    private readonly IOptions<SharedResourceConfiguration> _options;
    private readonly IConfiguration _configuration;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<RepositoryPathResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepositoryPathResolver"/> class.
    /// </summary>
    /// <param name="options">Shared resource configuration options.</param>
    /// <param name="configuration">Application configuration for additional lookup.</param>
    /// <param name="interactionService">Service for user interaction when prompting is enabled.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    public RepositoryPathResolver(
        IOptions<SharedResourceConfiguration> options,
        IConfiguration configuration,
        IInteractionService interactionService,
        ILogger<RepositoryPathResolver> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> ResolveRepositoryPathAsync(
        string gitHubRepository,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitHubRepository);
        ArgumentNullException.ThrowIfNull(serviceName);

        _logger.LogDebug("Resolving repository path for {ServiceName} ({Repository})",
            serviceName, gitHubRepository);

        // Priority 1: Explicit path in configuration
        var explicitPath = TryGetExplicitPath(serviceName);
        if (explicitPath is not null)
        {
            return ValidateAndReturnPath(explicitPath, gitHubRepository, serviceName);
        }

        // Priority 2: Environment variable
        var envPath = TryGetEnvironmentPath(serviceName);
        if (envPath is not null)
        {
            return ValidateAndReturnPath(envPath, gitHubRepository, serviceName);
        }

        // Priority 3: Base path + repo name
        var basePath = _options.Value.RepositoriesBasePath;
        if (!string.IsNullOrEmpty(basePath))
        {
            var repoName = ExtractRepositoryName(gitHubRepository);
            var derivedPath = Path.Combine(basePath, repoName);
            if (Directory.Exists(derivedPath))
            {
                _logger.LogDebug("Found repository at derived path: {Path}", derivedPath);
                return Path.GetFullPath(derivedPath);
            }
        }

        // Priority 4: User prompt
        if (_options.Value.PromptForMissingPaths)
        {
            return await PromptForPathAsync(gitHubRepository, serviceName, cancellationToken);
        }

        throw new RepositoryNotFoundException(
            $"Cannot resolve path for service '{serviceName}'",
            gitHubRepository,
            serviceName);
    }

    /// <inheritdoc />
    public Task<bool> IsRepositoryAvailableAsync(
        string gitHubRepository,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitHubRepository);
        ArgumentNullException.ThrowIfNull(serviceName);

        // Check explicit path
        var explicitPath = TryGetExplicitPath(serviceName);
        if (explicitPath is not null && Directory.Exists(explicitPath))
        {
            return Task.FromResult(true);
        }

        // Check environment variable
        var envPath = TryGetEnvironmentPath(serviceName);
        if (envPath is not null && Directory.Exists(envPath))
        {
            return Task.FromResult(true);
        }

        // Check base path
        var basePath = _options.Value.RepositoriesBasePath;
        if (!string.IsNullOrEmpty(basePath))
        {
            var repoName = ExtractRepositoryName(gitHubRepository);
            var derivedPath = Path.Combine(basePath, repoName);
            if (Directory.Exists(derivedPath))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public async Task SaveRepositoryPathAsync(
        string serviceName,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(repositoryPath);

        var key = $"SharedResources:RepositoryPaths:{serviceName}";

        await SaveToUserSecretsAsync(key, repositoryPath, cancellationToken);

        _logger.LogInformation("Saved repository path for {ServiceName} to user secrets", serviceName);
    }

    /// <summary>
    /// Attempts to get the explicit path from configuration for a service.
    /// </summary>
    /// <param name="serviceName">The service name to look up.</param>
    /// <returns>The configured path if found; otherwise, null.</returns>
    private string? TryGetExplicitPath(string serviceName)
    {
        if (_options.Value.RepositoryPaths.TryGetValue(serviceName, out var path))
        {
            return path;
        }

        // Also check IConfiguration directly for environment variable override
        var configKey = $"SharedResources:RepositoryPaths:{serviceName}";
        return _configuration[configKey];
    }

    /// <summary>
    /// Attempts to get the path from environment variable.
    /// </summary>
    /// <param name="serviceName">The service name to look up.</param>
    /// <returns>The environment variable value if set; otherwise, null.</returns>
    private static string? TryGetEnvironmentPath(string serviceName)
    {
        var envKey = $"SHAREDRESOURCES__REPOSITORYPATHS__{serviceName.ToUpperInvariant()}";
        return Environment.GetEnvironmentVariable(envKey);
    }

    /// <summary>
    /// Validates that a path exists and returns its full path.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <param name="gitHubRepository">The GitHub repository identifier.</param>
    /// <param name="serviceName">The service name.</param>
    /// <returns>The full path if valid.</returns>
    /// <exception cref="RepositoryNotFoundException">Thrown when the path does not exist.</exception>
    private string ValidateAndReturnPath(string path, string gitHubRepository, string serviceName)
    {
        if (!Directory.Exists(path))
        {
            throw new RepositoryNotFoundException(
                $"Configured path does not exist: {path}",
                gitHubRepository,
                serviceName);
        }

        _logger.LogDebug("Resolved repository path: {Path}", path);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Extracts the repository name from a GitHub repository identifier.
    /// </summary>
    /// <param name="gitHubRepository">The repository identifier in "org/repo" format.</param>
    /// <returns>The repository name (the part after the slash).</returns>
    /// <example>
    /// <code>
    /// ExtractRepositoryName("myorg/api-service") // returns "api-service"
    /// </code>
    /// </example>
    internal static string ExtractRepositoryName(string gitHubRepository)
    {
        var parts = gitHubRepository.Split('/');
        return parts.Length >= 2 ? parts[^1] : gitHubRepository;
    }

    /// <summary>
    /// Prompts the user for the repository path.
    /// </summary>
    /// <param name="gitHubRepository">The GitHub repository identifier.</param>
    /// <param name="serviceName">The service name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validated path provided by the user.</returns>
    /// <exception cref="RepositoryNotFoundException">Thrown when the user provides an invalid or empty path.</exception>
    private async Task<string> PromptForPathAsync(
        string gitHubRepository,
        string serviceName,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Repository path not configured for '{ServiceName}'", serviceName);

        var input = new InteractionInput
        {
            Name = "repositoryPath",
            Label = "Repository Path",
            InputType = InputType.Text,
            Required = true,
            Placeholder = $"/path/to/{ExtractRepositoryName(gitHubRepository)}",
            Description = $"Please provide the local path where '{gitHubRepository}' is cloned."
        };

        var result = await _interactionService.PromptInputAsync(
            "Repository Path Required",
            $"The repository path for service '{serviceName}' is not configured.",
            input,
            cancellationToken: cancellationToken);

        if (result.Canceled || string.IsNullOrWhiteSpace(input.Value))
        {
            throw new RepositoryNotFoundException(
                "No path provided by user",
                gitHubRepository,
                serviceName);
        }

        var path = input.Value.Trim();
        if (!Directory.Exists(path))
        {
            throw new RepositoryNotFoundException(
                $"Provided path does not exist: {path}",
                gitHubRepository,
                serviceName);
        }

        // Offer to save for future runs
        var confirmResult = await _interactionService.PromptConfirmationAsync(
            "Save Path?",
            "Would you like to save this path for future runs?",
            cancellationToken: cancellationToken);

        if (!confirmResult.Canceled && confirmResult.Data)
        {
            await SaveRepositoryPathAsync(serviceName, path, cancellationToken);
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Saves a configuration value to user secrets using the dotnet CLI.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the dotnet user-secrets command fails.</exception>
    private async Task SaveToUserSecretsAsync(string key, string value, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CliWrapLib.Cli.Wrap("dotnet")
                .WithArguments(["user-secrets", "set", key, value])
                .WithValidation(CliWrapLib.CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                var errorMessage = !string.IsNullOrEmpty(result.StandardError)
                    ? result.StandardError
                    : result.StandardOutput;

                _logger.LogWarning(
                    "Failed to save to user secrets: {Error}. Exit code: {ExitCode}",
                    errorMessage,
                    result.ExitCode);

                throw new InvalidOperationException(
                    $"Failed to save to user secrets. Exit code: {result.ExitCode}. {errorMessage}");
            }

            _logger.LogDebug("Successfully saved {Key} to user secrets", key);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error executing dotnet user-secrets command");
            throw new InvalidOperationException("Failed to execute dotnet user-secrets command", ex);
        }
    }
}
