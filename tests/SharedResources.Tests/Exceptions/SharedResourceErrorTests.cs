namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class SharedResourceErrorTests
{
    [Test]
    public async Task CreatingError_WithRequiredProperties_Succeeds()
    {
        // Arrange
        var exception = new InvalidOperationException("Build failed");

        // Act
        var error = new SharedResourceError(
            "api-service",
            "myorg/api-service",
            exception);

        // Assert
        await Assert.That(error.ServiceName).IsEqualTo("api-service");
        await Assert.That(error.GitHubRepository).IsEqualTo("myorg/api-service");
        await Assert.That(error.Exception).IsSameReferenceAs(exception);
    }

    [Test]
    public async Task CreatingError_PreservesExceptionDetails()
    {
        // Arrange
        var innerException = new ArgumentException("Inner error");
        var exception = new InvalidOperationException("Original error", innerException);

        // Act
        var error = new SharedResourceError(
            "api-service",
            "myorg/api-service",
            exception);

        // Assert
        await Assert.That(error.Exception.Message).IsEqualTo("Original error");
        await Assert.That(error.Exception.InnerException).IsSameReferenceAs(innerException);
    }

    [Test]
    public async Task SharedResourceError_IsNotAnException()
    {
        // Act & Assert - SharedResourceError should NOT inherit from Exception
        var inheritsFromException = typeof(SharedResourceError).IsSubclassOf(typeof(Exception));
        await Assert.That(inheritsFromException).IsFalse();
    }

    [Test]
    public async Task CreatingError_WithDifferentRepositoryFormats_StoresCorrectly()
    {
        // Arrange
        var exception = new InvalidOperationException("Error");

        // Act - test different repo formats
        var error1 = new SharedResourceError("service1", "org/repo", exception);
        var error2 = new SharedResourceError("service2", "my-org/my-repo-name", exception);

        // Assert
        await Assert.That(error1.GitHubRepository).IsEqualTo("org/repo");
        await Assert.That(error2.GitHubRepository).IsEqualTo("my-org/my-repo-name");
    }
}
