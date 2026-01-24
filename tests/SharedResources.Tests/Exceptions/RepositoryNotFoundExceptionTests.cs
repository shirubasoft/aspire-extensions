namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class RepositoryNotFoundExceptionTests
{
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var exception = new RepositoryNotFoundException(
            "Repository not found",
            "myorg/api-service",
            "api-service");

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Repository not found");
        await Assert.That(exception.GitHubRepository).IsEqualTo("myorg/api-service");
        await Assert.That(exception.ServiceName).IsEqualTo("api-service");
    }

    [Test]
    public async Task ConstructorWithInnerException_SetsPropertiesCorrectly()
    {
        // Arrange
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var exception = new RepositoryNotFoundException(
            "Repository not found",
            "myorg/api-service",
            "api-service",
            innerException);

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Repository not found");
        await Assert.That(exception.GitHubRepository).IsEqualTo("myorg/api-service");
        await Assert.That(exception.ServiceName).IsEqualTo("api-service");
        await Assert.That(exception.InnerException).IsSameReferenceAs(innerException);
    }

    [Test]
    public async Task GetDetailedMessage_IncludesSetupInstructions()
    {
        // Arrange
        var exception = new RepositoryNotFoundException(
            "Repository not found",
            "myorg/api-service",
            "api-service");

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        await Assert.That(detailedMessage).Contains("Repository not found");
        await Assert.That(detailedMessage).Contains("myorg/api-service");
        await Assert.That(detailedMessage).Contains("api-service");
        await Assert.That(detailedMessage).Contains("SharedResources");
        await Assert.That(detailedMessage).Contains("RepositoryPaths");
        await Assert.That(detailedMessage).Contains("RepositoriesBasePath");
        await Assert.That(detailedMessage).Contains("appsettings.json");
    }

    [Test]
    public async Task GetDetailedMessage_IncludesRepositoryName()
    {
        // Arrange
        var exception = new RepositoryNotFoundException(
            "Repository not found",
            "myorg/api-service",
            "api-service");

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert - should include the repo name (after the slash) for clone instructions
        await Assert.That(detailedMessage).Contains("api-service");
    }

    [Test]
    public async Task IsSerializable()
    {
        // Arrange
        var exception = new RepositoryNotFoundException(
            "Repository not found",
            "myorg/api-service",
            "api-service");

        // Act & Assert - check that it has the Serializable attribute
        var hasAttribute = typeof(RepositoryNotFoundException)
            .GetCustomAttributes(typeof(SerializableAttribute), false)
            .Length > 0;

        await Assert.That(hasAttribute).IsTrue();
    }
}
