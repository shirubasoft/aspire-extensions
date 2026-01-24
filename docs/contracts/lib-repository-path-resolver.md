# Contract: IRepositoryPathResolver

## Overview

This contract defines the interface and implementation for resolving local filesystem paths to external repositories based on configuration, environment variables, or user interaction.

## Namespace

```csharp
namespace Aspire.Hosting.SharedResources;
```

## Dependencies

- `Microsoft.Extensions.Options` (for IOptions<SharedResourceConfiguration>)
- `Microsoft.Extensions.Configuration` (for IConfiguration)
- `Microsoft.Extensions.Logging` (for ILogger)
- `Aspire.Hosting` (for IInteractionService)

---

## IRepositoryPathResolver Interface

### Description

Service responsible for resolving repository paths from configuration, environment, or user interaction.

### Interface Definition

```csharp
/// <summary>
/// Service responsible for resolving repository paths from configuration,
/// environment, or user interaction.
/// </summary>
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
    /// Thrown when gitHubRepository or serviceName is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when gitHubRepository is not in valid format.
    /// </exception>
    /// <exception cref="RepositoryNotFoundException">
    /// Thrown when the repository path cannot be resolved or does not exist.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled.
    /// </exception>
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
    /// <exception cref="InvalidOperationException">
    /// Thrown when user secrets cannot be accessed or modified.
    /// </exception>
    Task SaveRepositoryPathAsync(
        string serviceName,
        string repositoryPath,
        CancellationToken cancellationToken = default);
}
```

---

## RepositoryNotFoundException Exception

### Description

Exception thrown when a repository path cannot be resolved.

### Class Definition

```csharp
/// <summary>
/// Exception thrown when a repository path cannot be resolved.
/// </summary>
[Serializable]
public class RepositoryNotFoundException : Exception
{
    /// <summary>
    /// The GitHub repository identifier that could not be found.
    /// </summary>
    public string GitHubRepository { get; }

    /// <summary>
    /// The service name used for configuration lookup.
    /// </summary>
    public string ServiceName { get; }

    /// <summary>
    /// Initializes a new instance of the RepositoryNotFoundException class.
    /// </summary>
    /// <param name="gitHubRepository">The repository identifier.</param>
    /// <param name="serviceName">The service name.</param>
    /// <param name="message">The error message.</param>
    public RepositoryNotFoundException(
        string gitHubRepository,
        string serviceName,
        string message)
        : base(message)
    {
        GitHubRepository = gitHubRepository;
        ServiceName = serviceName;
    }

    /// <summary>
    /// Initializes a new instance with an inner exception.
    /// </summary>
    public RepositoryNotFoundException(
        string gitHubRepository,
        string serviceName,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        GitHubRepository = gitHubRepository;
        ServiceName = serviceName;
    }

    /// <summary>
    /// Gets a detailed message with configuration instructions.
    /// </summary>
    public string GetDetailedMessage()
    {
        return $"""
            Cannot resolve path for service '{ServiceName}' (repository: {GitHubRepository})

            Please configure the repository path using one of these methods:

            1. User secrets:
               dotnet user-secrets set "SharedResources:RepositoryPaths:{ServiceName}" "/path/to/repo"

            2. Environment variable:
               export SHAREDRESOURCES__REPOSITORYPATHS__{ServiceName.ToUpperInvariant()}=/path/to/repo

            3. Base path (for all repos):
               dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"

            Or clone the repository:
               git clone https://github.com/{GitHubRepository}.git /path/to/repo
            """;
    }
}
```

---

## RepositoryPathResolver Implementation

### Description

Default implementation of `IRepositoryPathResolver` that resolves paths using a priority-based strategy.

### Class Definition

```csharp
/// <summary>
/// Default implementation of IRepositoryPathResolver.
/// </summary>
/// <remarks>
/// Resolution priority:
/// <list type="number">
///   <item>Explicit path in RepositoryPaths[ServiceName] configuration</item>
///   <item>Environment variable SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}</item>
///   <item>RepositoriesBasePath + repository name (from GitHubRepository)</item>
///   <item>User prompt via IInteractionService (if PromptForMissingPaths is true)</item>
/// </list>
/// </remarks>
public class RepositoryPathResolver : IRepositoryPathResolver
{
    private readonly IOptions<SharedResourceConfiguration> _options;
    private readonly IConfiguration _configuration;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<RepositoryPathResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the RepositoryPathResolver class.
    /// </summary>
    /// <param name="options">Shared resource configuration options.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="interactionService">Service for user interaction.</param>
    /// <param name="logger">Logger instance.</param>
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
                return derivedPath;
            }
        }

        // Priority 4: User prompt
        if (_options.Value.PromptForMissingPaths)
        {
            return await PromptForPathAsync(gitHubRepository, serviceName, cancellationToken);
        }

        throw new RepositoryNotFoundException(
            gitHubRepository,
            serviceName,
            $"Cannot resolve path for service '{serviceName}'");
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

        // Implementation uses dotnet user-secrets CLI
        var key = $"SharedResources:RepositoryPaths:{serviceName}";

        // Execute: dotnet user-secrets set "key" "value"
        // This is implemented via CliWrap in the actual implementation
        await SaveToUserSecretsAsync(key, repositoryPath, cancellationToken);

        _logger.LogInformation("Saved repository path for {ServiceName} to user secrets", serviceName);
    }

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

    private string? TryGetEnvironmentPath(string serviceName)
    {
        var envKey = $"SHAREDRESOURCES__REPOSITORYPATHS__{serviceName.ToUpperInvariant()}";
        return Environment.GetEnvironmentVariable(envKey);
    }

    private string ValidateAndReturnPath(string path, string gitHubRepository, string serviceName)
    {
        if (!Directory.Exists(path))
        {
            throw new RepositoryNotFoundException(
                gitHubRepository,
                serviceName,
                $"Configured path does not exist: {path}");
        }

        _logger.LogDebug("Resolved repository path: {Path}", path);
        return Path.GetFullPath(path);
    }

    private static string ExtractRepositoryName(string gitHubRepository)
    {
        var parts = gitHubRepository.Split('/');
        return parts.Length == 2 ? parts[1] : gitHubRepository;
    }

    private async Task<string> PromptForPathAsync(
        string gitHubRepository,
        string serviceName,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Repository path not configured for '{ServiceName}'", serviceName);

        var prompt = $"Please provide the local path where '{gitHubRepository}' is cloned:";
        var path = await _interactionService.PromptAsync(prompt, cancellationToken);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new RepositoryNotFoundException(
                gitHubRepository,
                serviceName,
                "No path provided by user");
        }

        path = path.Trim();
        if (!Directory.Exists(path))
        {
            throw new RepositoryNotFoundException(
                gitHubRepository,
                serviceName,
                $"Provided path does not exist: {path}");
        }

        // Offer to save for future runs
        var savePrompt = "Save this path for future runs? [Y/n]:";
        var saveResponse = await _interactionService.PromptAsync(savePrompt, cancellationToken);

        if (string.IsNullOrWhiteSpace(saveResponse) ||
            saveResponse.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) ||
            saveResponse.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            await SaveRepositoryPathAsync(serviceName, path, cancellationToken);
        }

        return Path.GetFullPath(path);
    }

    private Task SaveToUserSecretsAsync(string key, string value, CancellationToken cancellationToken)
    {
        // Implementation detail: uses CliWrap to execute dotnet user-secrets
        // This is a placeholder for the actual implementation
        throw new NotImplementedException("Implement using CliWrap");
    }
}
```

---

## Method Signatures Summary

### IRepositoryPathResolver

| Method | Parameters | Returns | Description |
|--------|------------|---------|-------------|
| `ResolveRepositoryPathAsync` | `gitHubRepository`, `serviceName`, `cancellationToken` | `Task<string>` | Resolves and validates repository path |
| `IsRepositoryAvailableAsync` | `gitHubRepository`, `serviceName`, `cancellationToken` | `Task<bool>` | Checks if path is resolvable without prompting |
| `SaveRepositoryPathAsync` | `serviceName`, `repositoryPath`, `cancellationToken` | `Task` | Persists path to user secrets |

---

## Configuration Resolution Priority

| Priority | Source | Configuration Key | Example |
|----------|--------|-------------------|---------|
| 1 | Explicit Configuration | `SharedResources:RepositoryPaths:{ServiceName}` | `SharedResources:RepositoryPaths:api-1` |
| 2 | Environment Variable | `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}` | `SHAREDRESOURCES__REPOSITORYPATHS__API_1` |
| 3 | Base Path Derivation | `SharedResources:RepositoriesBasePath` + repo name | `/home/user/repos` + `repo-1` |
| 4 | User Prompt | Interactive via IInteractionService | User enters path when prompted |

---

## Usage Examples

### Resolving a Repository Path

```csharp
var resolver = serviceProvider.GetRequiredService<IRepositoryPathResolver>();

try
{
    var path = await resolver.ResolveRepositoryPathAsync(
        "myorg/repo-1",
        "api-1",
        cancellationToken);

    Console.WriteLine($"Repository located at: {path}");
}
catch (RepositoryNotFoundException ex)
{
    Console.WriteLine(ex.GetDetailedMessage());
}
```

### Checking Availability Before Build

```csharp
var resolver = serviceProvider.GetRequiredService<IRepositoryPathResolver>();

var isAvailable = await resolver.IsRepositoryAvailableAsync(
    "myorg/repo-1",
    "api-1",
    cancellationToken);

if (!isAvailable)
{
    logger.LogWarning("Repository not configured, will prompt user during startup");
}
```

### Configuration Examples

**appsettings.json:**
```json
{
  "SharedResources": {
    "RepositoriesBasePath": "/home/user/repos",
    "PromptForMissingPaths": true,
    "RepositoryPaths": {
      "api-1": "/custom/path/to/repo-1",
      "api-2": "/another/path/to/repo-2"
    }
  }
}
```

**User Secrets:**
```bash
dotnet user-secrets set "SharedResources:RepositoryPaths:api-1" "/home/user/code/repo-1"
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"
```

**Environment Variables:**
```bash
export SHAREDRESOURCES__REPOSITORYPATHS__API_1=/home/user/code/repo-1
export SHAREDRESOURCES__REPOSITORIESBASEPATH=/home/user/repos
```

---

## Error Cases

| Scenario | Exception | Behavior |
|----------|-----------|----------|
| Path configured but doesn't exist | `RepositoryNotFoundException` | Includes path in message |
| No configuration and prompting disabled | `RepositoryNotFoundException` | Includes setup instructions |
| User cancels prompt | `OperationCanceledException` | Propagates cancellation |
| User provides empty path | `RepositoryNotFoundException` | "No path provided by user" |
| User provides non-existent path | `RepositoryNotFoundException` | "Provided path does not exist: {path}" |

---

## Thread Safety

The `RepositoryPathResolver` is thread-safe and registered as a singleton. All methods are async and use immutable state from configuration.
