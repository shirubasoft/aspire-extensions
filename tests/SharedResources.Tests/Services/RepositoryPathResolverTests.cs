namespace SharedResources.Tests.Services;

using Aspire.Hosting;
using Aspire.Hosting.SharedResources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is for evaluation purposes

public class RepositoryPathResolverTests
{
    private readonly IOptions<SharedResourceConfiguration> _options;
    private readonly IConfiguration _configuration;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<RepositoryPathResolver> _logger;
    private readonly string _testBasePath;

    public RepositoryPathResolverTests()
    {
        _options = Substitute.For<IOptions<SharedResourceConfiguration>>();
        _configuration = Substitute.For<IConfiguration>();
        _interactionService = Substitute.For<IInteractionService>();
        _logger = Substitute.For<ILogger<RepositoryPathResolver>>();

        // Use temp directory as test base path
        _testBasePath = Path.Combine(Path.GetTempPath(), "repo-resolver-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testBasePath);
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ResolvesFromExplicitRepositoryPathsConfig()
    {
        // Arrange
        var testRepoPath = Path.Combine(_testBasePath, "explicit-repo");
        Directory.CreateDirectory(testRepoPath);

        var config = new SharedResourceConfiguration
        {
            RepositoryPaths = new Dictionary<string, string>
            {
                ["api-service"] = testRepoPath
            },
            PromptForMissingPaths = false
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act
        var result = await resolver.ResolveRepositoryPathAsync("myorg/api-service", "api-service");

        // Assert
        await Assert.That(result).IsEqualTo(Path.GetFullPath(testRepoPath));
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ResolvesFromEnvironmentVariable()
    {
        // Arrange
        var testRepoPath = Path.Combine(_testBasePath, "env-repo");
        Directory.CreateDirectory(testRepoPath);

        var envKey = "SHAREDRESOURCES__REPOSITORYPATHS__ENVSERVICE";
        Environment.SetEnvironmentVariable(envKey, testRepoPath);

        try
        {
            var config = new SharedResourceConfiguration
            {
                RepositoryPaths = new Dictionary<string, string>(),
                PromptForMissingPaths = false
            };
            _options.Value.Returns(config);

            // Configure IConfiguration mock to return null for the service lookup
            _configuration["SharedResources:RepositoryPaths:envservice"].Returns((string?)null);

            var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

            // Act
            var result = await resolver.ResolveRepositoryPathAsync("myorg/env-service", "envservice");

            // Assert
            await Assert.That(result).IsEqualTo(Path.GetFullPath(testRepoPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ResolvesFromRepositoriesBasePath()
    {
        // Arrange
        var repoName = "derived-repo";
        var testRepoPath = Path.Combine(_testBasePath, repoName);
        Directory.CreateDirectory(testRepoPath);

        var config = new SharedResourceConfiguration
        {
            RepositoriesBasePath = _testBasePath,
            RepositoryPaths = new Dictionary<string, string>(),
            PromptForMissingPaths = false
        };
        _options.Value.Returns(config);

        // Configure IConfiguration mock to return null for the service lookup
        _configuration["SharedResources:RepositoryPaths:my-service"].Returns((string?)null);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act
        var result = await resolver.ResolveRepositoryPathAsync($"myorg/{repoName}", "my-service");

        // Assert
        await Assert.That(result).IsEqualTo(Path.GetFullPath(testRepoPath));
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ThrowsRepositoryNotFoundException_WhenNotFoundAndPromptingDisabled()
    {
        // Arrange
        var config = new SharedResourceConfiguration
        {
            RepositoryPaths = new Dictionary<string, string>(),
            PromptForMissingPaths = false
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.ResolveRepositoryPathAsync("myorg/nonexistent", "nonexistent"))
            .Throws<RepositoryNotFoundException>();
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ThrowsRepositoryNotFoundException_WhenConfiguredPathDoesNotExist()
    {
        // Arrange
        var config = new SharedResourceConfiguration
        {
            RepositoryPaths = new Dictionary<string, string>
            {
                ["missing-service"] = "/nonexistent/path/that/does/not/exist"
            },
            PromptForMissingPaths = false
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        var exception = await Assert.That(async () =>
            await resolver.ResolveRepositoryPathAsync("myorg/missing-repo", "missing-service"))
            .Throws<RepositoryNotFoundException>();

        await Assert.That(exception!.Message).Contains("/nonexistent/path/that/does/not/exist");
    }

    [Test]
    public async Task IsRepositoryAvailableAsync_ReturnsTrue_WhenPathConfiguredAndExists()
    {
        // Arrange
        var testRepoPath = Path.Combine(_testBasePath, "available-repo");
        Directory.CreateDirectory(testRepoPath);

        var config = new SharedResourceConfiguration
        {
            RepositoryPaths = new Dictionary<string, string>
            {
                ["available-service"] = testRepoPath
            }
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act
        var result = await resolver.IsRepositoryAvailableAsync("myorg/available-repo", "available-service");

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsRepositoryAvailableAsync_ReturnsFalse_WhenNotConfigured()
    {
        // Arrange
        var config = new SharedResourceConfiguration
        {
            RepositoryPaths = new Dictionary<string, string>()
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act
        var result = await resolver.IsRepositoryAvailableAsync("myorg/unavailable-repo", "unavailable-service");

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsRepositoryAvailableAsync_ReturnsFalse_WhenConfiguredPathDoesNotExist()
    {
        // Arrange
        var config = new SharedResourceConfiguration
        {
            RepositoryPaths = new Dictionary<string, string>
            {
                ["ghost-service"] = "/path/that/does/not/exist"
            }
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act
        var result = await resolver.IsRepositoryAvailableAsync("myorg/ghost-repo", "ghost-service");

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsRepositoryAvailableAsync_ReturnsTrue_WhenDerivedPathExists()
    {
        // Arrange
        var repoName = "base-derived";
        var testRepoPath = Path.Combine(_testBasePath, repoName);
        Directory.CreateDirectory(testRepoPath);

        var config = new SharedResourceConfiguration
        {
            RepositoriesBasePath = _testBasePath,
            RepositoryPaths = new Dictionary<string, string>()
        };
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act
        var result = await resolver.IsRepositoryAvailableAsync($"myorg/{repoName}", "any-service");

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ExtractRepositoryName_ExtractsRepoNameFromOrgRepo()
    {
        // Arrange & Act
        var result = RepositoryPathResolver.ExtractRepositoryName("myorg/my-awesome-repo");

        // Assert
        await Assert.That(result).IsEqualTo("my-awesome-repo");
    }

    [Test]
    public async Task ExtractRepositoryName_ReturnsInputWhenNoSlash()
    {
        // Arrange & Act
        var result = RepositoryPathResolver.ExtractRepositoryName("standalone-repo");

        // Assert
        await Assert.That(result).IsEqualTo("standalone-repo");
    }

    [Test]
    public async Task ExtractRepositoryName_HandlesMultipleSlashes()
    {
        // Arrange & Act
        var result = RepositoryPathResolver.ExtractRepositoryName("org/sub/repo");

        // Assert
        await Assert.That(result).IsEqualTo("repo");
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ThrowsArgumentNullException_WhenGitHubRepositoryIsNull()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.ResolveRepositoryPathAsync(null!, "service"))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_ThrowsArgumentNullException_WhenServiceNameIsNull()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.ResolveRepositoryPathAsync("org/repo", null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task IsRepositoryAvailableAsync_ThrowsArgumentNullException_WhenGitHubRepositoryIsNull()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.IsRepositoryAvailableAsync(null!, "service"))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task IsRepositoryAvailableAsync_ThrowsArgumentNullException_WhenServiceNameIsNull()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.IsRepositoryAvailableAsync("org/repo", null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SaveRepositoryPathAsync_ThrowsArgumentNullException_WhenServiceNameIsNull()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.SaveRepositoryPathAsync(null!, "/some/path"))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SaveRepositoryPathAsync_ThrowsArgumentNullException_WhenRepositoryPathIsNull()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        _options.Value.Returns(config);

        var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

        // Act & Assert
        await Assert.That(async () =>
            await resolver.SaveRepositoryPathAsync("service", null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenOptionsIsNull()
    {
        // Act & Assert
        await Assert.That(() =>
            new RepositoryPathResolver(null!, _configuration, _interactionService, _logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenConfigurationIsNull()
    {
        // Act & Assert
        await Assert.That(() =>
            new RepositoryPathResolver(_options, null!, _interactionService, _logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenInteractionServiceIsNull()
    {
        // Act & Assert
        await Assert.That(() =>
            new RepositoryPathResolver(_options, _configuration, null!, _logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        // Act & Assert
        await Assert.That(() =>
            new RepositoryPathResolver(_options, _configuration, _interactionService, null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_PrioritizesExplicitConfigOverEnvironmentVariable()
    {
        // Arrange
        var explicitPath = Path.Combine(_testBasePath, "explicit-priority");
        var envPath = Path.Combine(_testBasePath, "env-priority");
        Directory.CreateDirectory(explicitPath);
        Directory.CreateDirectory(envPath);

        var envKey = "SHAREDRESOURCES__REPOSITORYPATHS__PRIORITYSERVICE";
        Environment.SetEnvironmentVariable(envKey, envPath);

        try
        {
            var config = new SharedResourceConfiguration
            {
                RepositoryPaths = new Dictionary<string, string>
                {
                    ["priorityservice"] = explicitPath
                },
                PromptForMissingPaths = false
            };
            _options.Value.Returns(config);

            var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

            // Act
            var result = await resolver.ResolveRepositoryPathAsync("myorg/priority-repo", "priorityservice");

            // Assert - should use explicit config, not env variable
            await Assert.That(result).IsEqualTo(Path.GetFullPath(explicitPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Test]
    public async Task ResolveRepositoryPathAsync_PrioritizesEnvironmentVariableOverBasePath()
    {
        // Arrange
        var envRepoPath = Path.Combine(_testBasePath, "env-priority-over-base");
        var baseRepoPath = Path.Combine(_testBasePath, "base-repo");
        Directory.CreateDirectory(envRepoPath);
        Directory.CreateDirectory(baseRepoPath);

        var envKey = "SHAREDRESOURCES__REPOSITORYPATHS__BASETEST";
        Environment.SetEnvironmentVariable(envKey, envRepoPath);

        try
        {
            var config = new SharedResourceConfiguration
            {
                RepositoriesBasePath = _testBasePath,
                RepositoryPaths = new Dictionary<string, string>(),
                PromptForMissingPaths = false
            };
            _options.Value.Returns(config);

            // Configure IConfiguration mock to return null for the service lookup
            _configuration["SharedResources:RepositoryPaths:basetest"].Returns((string?)null);

            var resolver = new RepositoryPathResolver(_options, _configuration, _interactionService, _logger);

            // Act
            var result = await resolver.ResolveRepositoryPathAsync("myorg/base-repo", "basetest");

            // Assert - should use env variable, not base path derivation
            await Assert.That(result).IsEqualTo(Path.GetFullPath(envRepoPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }
}
