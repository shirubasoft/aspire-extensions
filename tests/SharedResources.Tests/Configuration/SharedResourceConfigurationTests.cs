namespace SharedResources.Tests.Configuration;

using Aspire.Hosting.SharedResources;

public class SharedResourceConfigurationTests
{
    [Test]
    public async Task SectionName_IsSharedResources()
    {
        // Arrange
        var sectionName = SharedResourceConfiguration.SectionName;

        // Assert
        await Assert.That(sectionName).IsEqualTo("SharedResources");
    }

    [Test]
    public async Task DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new SharedResourceConfiguration();

        // Assert
        await Assert.That(config.PromptForMissingPaths).IsTrue();
        await Assert.That(config.RepositoryPaths).IsNotNull();
        await Assert.That(config.RepositoryPaths.Count).IsEqualTo(0);
        await Assert.That(config.RepositoriesBasePath).IsNull();
    }

    [Test]
    public async Task ConfigurationCanBeCreatedWithDefaultValues()
    {
        // Arrange & Act
        var config = new SharedResourceConfiguration();

        // Assert
        await Assert.That(config).IsNotNull();
        await Assert.That(config.RepositoryPaths).IsNotNull();
    }

    [Test]
    public async Task RepositoryPaths_StoresAndRetrievesValuesCorrectly()
    {
        // Arrange
        var config = new SharedResourceConfiguration();

        // Act
        config.RepositoryPaths["service-a"] = "/path/to/service-a";
        config.RepositoryPaths["service-b"] = "/path/to/service-b";

        // Assert
        await Assert.That(config.RepositoryPaths["service-a"]).IsEqualTo("/path/to/service-a");
        await Assert.That(config.RepositoryPaths["service-b"]).IsEqualTo("/path/to/service-b");
        await Assert.That(config.RepositoryPaths.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RepositoriesBasePath_IsSettable()
    {
        // Arrange
        var config = new SharedResourceConfiguration();

        // Act
        config.RepositoriesBasePath = "/home/user/repos";

        // Assert
        await Assert.That(config.RepositoriesBasePath).IsEqualTo("/home/user/repos");
    }

    [Test]
    public async Task PromptForMissingPaths_IsSettable()
    {
        // Arrange
        var config = new SharedResourceConfiguration();

        // Act
        config.PromptForMissingPaths = false;

        // Assert
        await Assert.That(config.PromptForMissingPaths).IsFalse();
    }

    [Test]
    public async Task RepositoryPaths_IsSettable()
    {
        // Arrange
        var config = new SharedResourceConfiguration();
        var newPaths = new Dictionary<string, string>
        {
            ["api"] = "/repos/api",
            ["web"] = "/repos/web"
        };

        // Act
        config.RepositoryPaths = newPaths;

        // Assert
        await Assert.That(config.RepositoryPaths).IsEqualTo(newPaths);
        await Assert.That(config.RepositoryPaths["api"]).IsEqualTo("/repos/api");
    }
}
