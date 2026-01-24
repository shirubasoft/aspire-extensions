namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class SharedResourceErrorTests
{
    [Test]
    public async Task CreatingError_WithRequiredProperties_Succeeds()
    {
        // Arrange & Act
        var error = new SharedResourceError
        {
            ServiceName = "api-service",
            ErrorMessage = "Build failed"
        };

        // Assert
        await Assert.That(error.ServiceName).IsEqualTo("api-service");
        await Assert.That(error.ErrorMessage).IsEqualTo("Build failed");
        await Assert.That(error.Exception).IsNull();
    }

    [Test]
    public async Task CreatingError_WithException_SetsExceptionProperty()
    {
        // Arrange
        var exception = new InvalidOperationException("Original error");

        // Act
        var error = new SharedResourceError
        {
            ServiceName = "api-service",
            ErrorMessage = "Build failed",
            Exception = exception
        };

        // Assert
        await Assert.That(error.ServiceName).IsEqualTo("api-service");
        await Assert.That(error.ErrorMessage).IsEqualTo("Build failed");
        await Assert.That(error.Exception).IsSameReferenceAs(exception);
    }

    [Test]
    public async Task SharedResourceError_IsNotAnException()
    {
        // Act & Assert - SharedResourceError should NOT inherit from Exception
        var inheritsFromException = typeof(SharedResourceError).IsSubclassOf(typeof(Exception));
        await Assert.That(inheritsFromException).IsFalse();
    }
}
