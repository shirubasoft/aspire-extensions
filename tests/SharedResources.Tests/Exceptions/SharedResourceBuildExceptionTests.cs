namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class SharedResourceBuildExceptionTests
{
    [Test]
    public async Task Constructor_WithSingleError_SetsPropertiesCorrectly()
    {
        // Arrange
        var errors = new List<SharedResourceError>
        {
            new()
            {
                ServiceName = "api-service",
                ErrorMessage = "Build failed"
            }
        };

        // Act
        var exception = new SharedResourceBuildException(errors);

        // Assert
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
        await Assert.That(exception.Errors[0].ServiceName).IsEqualTo("api-service");
        await Assert.That(exception.Errors[0].ErrorMessage).IsEqualTo("Build failed");
        await Assert.That(exception.Message).Contains("api-service");
        await Assert.That(exception.Message).Contains("Build failed");
    }

    [Test]
    public async Task Constructor_WithMultipleErrors_AggregatesCorrectly()
    {
        // Arrange
        var errors = new List<SharedResourceError>
        {
            new()
            {
                ServiceName = "api-service",
                ErrorMessage = "Build failed"
            },
            new()
            {
                ServiceName = "web-service",
                ErrorMessage = "Repository not found"
            },
            new()
            {
                ServiceName = "worker-service",
                ErrorMessage = "Docker not available"
            }
        };

        // Act
        var exception = new SharedResourceBuildException(errors);

        // Assert
        await Assert.That(exception.Errors).Count().IsEqualTo(3);
        await Assert.That(exception.Message).Contains("3 services");
        await Assert.That(exception.Message).Contains("api-service");
        await Assert.That(exception.Message).Contains("web-service");
        await Assert.That(exception.Message).Contains("worker-service");
    }

    [Test]
    public async Task Constructor_WithErrorContainingException_PreservesException()
    {
        // Arrange
        var innerException = new InvalidOperationException("Original error");
        var errors = new List<SharedResourceError>
        {
            new()
            {
                ServiceName = "api-service",
                ErrorMessage = "Build failed",
                Exception = innerException
            }
        };

        // Act
        var exception = new SharedResourceBuildException(errors);

        // Assert
        await Assert.That(exception.Errors[0].Exception).IsSameReferenceAs(innerException);
    }

    [Test]
    public async Task ConstructorWithInnerException_SetsPropertiesCorrectly()
    {
        // Arrange
        var errors = new List<SharedResourceError>
        {
            new()
            {
                ServiceName = "api-service",
                ErrorMessage = "Build failed"
            }
        };
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var exception = new SharedResourceBuildException(errors, innerException);

        // Assert
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
        await Assert.That(exception.InnerException).IsSameReferenceAs(innerException);
    }

    [Test]
    public void Constructor_WithEmptyErrors_ThrowsArgumentException()
    {
        // Arrange
        var errors = new List<SharedResourceError>();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new SharedResourceBuildException(errors));
    }

    [Test]
    public void Constructor_WithNullErrors_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new SharedResourceBuildException(null!));
    }

    [Test]
    public async Task IsSerializable()
    {
        // Arrange
        var errors = new List<SharedResourceError>
        {
            new()
            {
                ServiceName = "api-service",
                ErrorMessage = "Build failed"
            }
        };
        var exception = new SharedResourceBuildException(errors);

        // Act & Assert - check that it has the Serializable attribute
        var hasAttribute = typeof(SharedResourceBuildException)
            .GetCustomAttributes(typeof(SerializableAttribute), false)
            .Length > 0;

        await Assert.That(hasAttribute).IsTrue();
    }
}
