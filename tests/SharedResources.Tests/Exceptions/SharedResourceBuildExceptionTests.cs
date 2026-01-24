namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class SharedResourceBuildExceptionTests
{
    [Test]
    public async Task Constructor_WithSingleError_SetsPropertiesCorrectly()
    {
        // Arrange
        var innerException = new InvalidOperationException("Build failed");
        var errors = new List<SharedResourceError>
        {
            new("api-service", "myorg/api-service", innerException)
        };

        // Act
        var exception = new SharedResourceBuildException(errors);

        // Assert
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
        await Assert.That(exception.Errors[0].ServiceName).IsEqualTo("api-service");
        await Assert.That(exception.Errors[0].GitHubRepository).IsEqualTo("myorg/api-service");
        await Assert.That(exception.Message).Contains("api-service");
        await Assert.That(exception.Message).Contains("Build failed");
    }

    [Test]
    public async Task Constructor_WithMultipleErrors_AggregatesCorrectly()
    {
        // Arrange
        var errors = new List<SharedResourceError>
        {
            new("api-service", "myorg/api-service", new InvalidOperationException("Build failed")),
            new("web-service", "myorg/web-service", new InvalidOperationException("Repository not found")),
            new("worker-service", "myorg/worker-service", new InvalidOperationException("Docker not available"))
        };

        // Act
        var exception = new SharedResourceBuildException(errors);

        // Assert
        await Assert.That(exception.Errors).Count().IsEqualTo(3);
        await Assert.That(exception.Message).Contains("3 shared resources");
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
            new("api-service", "myorg/api-service", innerException)
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
        var errorException = new InvalidOperationException("Build failed");
        var errors = new List<SharedResourceError>
        {
            new("api-service", "myorg/api-service", errorException)
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
        Assert.Throws<ArgumentException>(() => new SharedResourceBuildException((IEnumerable<SharedResourceError>)null!));
    }

    [Test]
    public async Task Constructor_WithMessage_SetsMessageCorrectly()
    {
        // Arrange
        var message = "Docker is not available. Please ensure Docker is running.";

        // Act
        var exception = new SharedResourceBuildException(message);

        // Assert
        await Assert.That(exception.Message).IsEqualTo(message);
        await Assert.That(exception.Errors).IsEmpty();
    }

    [Test]
    public async Task IsSerializable()
    {
        // Arrange
        var errorException = new InvalidOperationException("Build failed");
        var errors = new List<SharedResourceError>
        {
            new("api-service", "myorg/api-service", errorException)
        };
        var exception = new SharedResourceBuildException(errors);

        // Act & Assert - check that it has the Serializable attribute
        var hasAttribute = typeof(SharedResourceBuildException)
            .GetCustomAttributes(typeof(SerializableAttribute), false)
            .Length > 0;

        await Assert.That(hasAttribute).IsTrue();
    }

    [Test]
    public async Task GetDetailedMessage_ReturnsDetailedInfo()
    {
        // Arrange
        var errors = new List<SharedResourceError>
        {
            new("api-service", "myorg/api-service", new InvalidOperationException("Build failed")),
            new("web-service", "myorg/web-service", new InvalidOperationException("Repository not found"))
        };
        var exception = new SharedResourceBuildException(errors);

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        await Assert.That(detailedMessage).Contains("Service: api-service");
        await Assert.That(detailedMessage).Contains("Repository: myorg/api-service");
        await Assert.That(detailedMessage).Contains("Error: Build failed");
        await Assert.That(detailedMessage).Contains("Service: web-service");
        await Assert.That(detailedMessage).Contains("Repository: myorg/web-service");
        await Assert.That(detailedMessage).Contains("Error: Repository not found");
    }

    [Test]
    public async Task GetDetailedMessage_WithNoErrors_ReturnsMessage()
    {
        // Arrange
        var exception = new SharedResourceBuildException("Docker not available");

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        await Assert.That(detailedMessage).IsEqualTo("Docker not available");
    }

    [Test]
    public async Task GetDetailedMessage_WithContainerBuildException_IncludesBuildOutput()
    {
        // Arrange
        var buildException = new ContainerBuildException(
            "Build failed",
            "docker build -t test:latest .",
            1,
            "Error output",
            "/working/dir",
            "Build output here\nWith multiple lines");
        var errors = new List<SharedResourceError>
        {
            new("api-service", "myorg/api-service", buildException)
        };
        var exception = new SharedResourceBuildException(errors);

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        await Assert.That(detailedMessage).Contains("Build Output:");
        await Assert.That(detailedMessage).Contains("Build output here");
        await Assert.That(detailedMessage).Contains("With multiple lines");
    }
}
