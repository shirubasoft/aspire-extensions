namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class ContainerBuildExceptionTests
{
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var exception = new ContainerBuildException(
            "Build failed",
            "docker build -t my-image .",
            1,
            "Error: Dockerfile not found",
            "/path/to/repo",
            "Step 1/5: FROM mcr.microsoft.com/dotnet/sdk:8.0");

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Build failed");
        await Assert.That(exception.Command).IsEqualTo("docker build -t my-image .");
        await Assert.That(exception.ExitCode).IsEqualTo(1);
        await Assert.That(exception.StandardError).IsEqualTo("Error: Dockerfile not found");
        await Assert.That(exception.WorkingDirectory).IsEqualTo("/path/to/repo");
        await Assert.That(exception.BuildOutput).IsEqualTo("Step 1/5: FROM mcr.microsoft.com/dotnet/sdk:8.0");
    }

    [Test]
    public async Task ConstructorWithInnerException_SetsPropertiesCorrectly()
    {
        // Arrange
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var exception = new ContainerBuildException(
            "Build failed",
            "docker build -t my-image .",
            1,
            "Error: Dockerfile not found",
            "/path/to/repo",
            "Step 1/5: FROM mcr.microsoft.com/dotnet/sdk:8.0",
            innerException);

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Build failed");
        await Assert.That(exception.Command).IsEqualTo("docker build -t my-image .");
        await Assert.That(exception.ExitCode).IsEqualTo(1);
        await Assert.That(exception.StandardError).IsEqualTo("Error: Dockerfile not found");
        await Assert.That(exception.WorkingDirectory).IsEqualTo("/path/to/repo");
        await Assert.That(exception.BuildOutput).IsEqualTo("Step 1/5: FROM mcr.microsoft.com/dotnet/sdk:8.0");
        await Assert.That(exception.InnerException).IsSameReferenceAs(innerException);
    }

    [Test]
    public async Task InheritsFromContainerOperationException()
    {
        // Arrange
        var exception = new ContainerBuildException(
            "Build failed",
            "docker build -t my-image .",
            1,
            "Error",
            "/path/to/repo",
            "Build output");

        // Act & Assert
        await Assert.That(exception is ContainerOperationException).IsTrue();
    }

    [Test]
    public async Task GetDetailedMessage_IncludesBuildOutputAndTroubleshooting()
    {
        // Arrange
        var exception = new ContainerBuildException(
            "Build failed",
            "docker build -t my-image .",
            1,
            "Error: Dockerfile not found",
            "/path/to/repo",
            "Step 1/5: FROM mcr.microsoft.com/dotnet/sdk:8.0\nStep 2/5: COPY . .");

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        await Assert.That(detailedMessage).Contains("Build failed");
        await Assert.That(detailedMessage).Contains("docker build -t my-image .");
        await Assert.That(detailedMessage).Contains("/path/to/repo");
        await Assert.That(detailedMessage).Contains("Exit Code: 1");
        await Assert.That(detailedMessage).Contains("Error: Dockerfile not found");
        await Assert.That(detailedMessage).Contains("Step 1/5: FROM mcr.microsoft.com/dotnet/sdk:8.0");
        await Assert.That(detailedMessage).Contains("Troubleshooting");
        await Assert.That(detailedMessage).Contains("docker info");
    }

    [Test]
    public async Task GetDetailedMessage_TruncatesLongBuildOutput()
    {
        // Arrange
        var longBuildOutput = new string('x', 3000); // More than 2000 characters
        var exception = new ContainerBuildException(
            "Build failed",
            "docker build -t my-image .",
            1,
            "Error",
            "/path/to/repo",
            longBuildOutput);

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        await Assert.That(detailedMessage).Contains("Truncated");
        await Assert.That(detailedMessage).Contains("2000 characters");
    }

    [Test]
    public async Task IsSerializable()
    {
        // Arrange
        var exception = new ContainerBuildException(
            "Build failed",
            "docker build -t my-image .",
            1,
            "Error",
            "/path/to/repo",
            "Build output");

        // Act & Assert - check that it has the Serializable attribute
        var hasAttribute = typeof(ContainerBuildException)
            .GetCustomAttributes(typeof(SerializableAttribute), false)
            .Length > 0;

        await Assert.That(hasAttribute).IsTrue();
    }
}
