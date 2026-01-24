namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class ContainerOperationExceptionTests
{
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var exception = new ContainerOperationException(
            "Container command failed",
            "docker build -t my-image .",
            1,
            "Error response from daemon");

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Container command failed");
        await Assert.That(exception.Command).IsEqualTo("docker build -t my-image .");
        await Assert.That(exception.ExitCode).IsEqualTo(1);
        await Assert.That(exception.StandardError).IsEqualTo("Error response from daemon");
    }

    [Test]
    public async Task ConstructorWithInnerException_SetsPropertiesCorrectly()
    {
        // Arrange
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var exception = new ContainerOperationException(
            "Container command failed",
            "docker build -t my-image .",
            1,
            "Error response from daemon",
            innerException);

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Container command failed");
        await Assert.That(exception.Command).IsEqualTo("docker build -t my-image .");
        await Assert.That(exception.ExitCode).IsEqualTo(1);
        await Assert.That(exception.StandardError).IsEqualTo("Error response from daemon");
        await Assert.That(exception.InnerException).IsSameReferenceAs(innerException);
    }

    [Test]
    public async Task DockerNotAvailable_CreatesCorrectException()
    {
        // Act
        var exception = ContainerOperationException.DockerNotAvailable();

        // Assert
        await Assert.That(exception.Message).Contains("Docker is not available");
        await Assert.That(exception.Message).Contains("Docker daemon");
        await Assert.That(exception.Command).IsEqualTo("docker");
        await Assert.That(exception.ExitCode).IsEqualTo(-1);
        await Assert.That(exception.StandardError).Contains("Docker daemon");
    }

    [Test]
    public async Task IsSerializable()
    {
        // Arrange
        var exception = new ContainerOperationException(
            "Container command failed",
            "docker build -t my-image .",
            1,
            "Error response from daemon");

        // Act & Assert - check that it has the Serializable attribute
        var hasAttribute = typeof(ContainerOperationException)
            .GetCustomAttributes(typeof(SerializableAttribute), false)
            .Length > 0;

        await Assert.That(hasAttribute).IsTrue();
    }
}
