namespace SharedResources.Tests.Exceptions;

using Aspire.Hosting.SharedResources;

public class GitOperationExceptionTests
{
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var exception = new GitOperationException(
            "Git command failed",
            "git rev-parse HEAD",
            "/path/to/repo",
            128,
            "fatal: not a git repository");

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Git command failed");
        await Assert.That(exception.Command).IsEqualTo("git rev-parse HEAD");
        await Assert.That(exception.RepositoryPath).IsEqualTo("/path/to/repo");
        await Assert.That(exception.ExitCode).IsEqualTo(128);
        await Assert.That(exception.StandardError).IsEqualTo("fatal: not a git repository");
    }

    [Test]
    public async Task ConstructorWithInnerException_SetsPropertiesCorrectly()
    {
        // Arrange
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var exception = new GitOperationException(
            "Git command failed",
            "git rev-parse HEAD",
            "/path/to/repo",
            128,
            "fatal: not a git repository",
            innerException);

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Git command failed");
        await Assert.That(exception.Command).IsEqualTo("git rev-parse HEAD");
        await Assert.That(exception.RepositoryPath).IsEqualTo("/path/to/repo");
        await Assert.That(exception.ExitCode).IsEqualTo(128);
        await Assert.That(exception.StandardError).IsEqualTo("fatal: not a git repository");
        await Assert.That(exception.InnerException).IsSameReferenceAs(innerException);
    }

    [Test]
    public async Task GitNotInstalled_CreatesCorrectException()
    {
        // Act
        var exception = GitOperationException.GitNotInstalled();

        // Assert
        await Assert.That(exception.Message).Contains("Git is not installed");
        await Assert.That(exception.Message).Contains("PATH");
        await Assert.That(exception.Command).IsEqualTo("git");
        await Assert.That(exception.RepositoryPath).IsEqualTo(string.Empty);
        await Assert.That(exception.ExitCode).IsEqualTo(-1);
        await Assert.That(exception.StandardError).Contains("not found");
    }

    [Test]
    public async Task NotARepository_CreatesCorrectException()
    {
        // Arrange
        var repositoryPath = "/path/to/not-a-repo";

        // Act
        var exception = GitOperationException.NotARepository(repositoryPath);

        // Assert
        await Assert.That(exception.Message).Contains(repositoryPath);
        await Assert.That(exception.Message).Contains("not a Git repository");
        await Assert.That(exception.Command).Contains("git");
        await Assert.That(exception.RepositoryPath).IsEqualTo(repositoryPath);
        await Assert.That(exception.ExitCode).IsEqualTo(128);
        await Assert.That(exception.StandardError).Contains("not a git repository");
    }

    [Test]
    public async Task IsSerializable()
    {
        // Arrange
        var exception = new GitOperationException(
            "Git command failed",
            "git rev-parse HEAD",
            "/path/to/repo",
            128,
            "fatal: not a git repository");

        // Act & Assert - check that it has the Serializable attribute
        var hasAttribute = typeof(GitOperationException)
            .GetCustomAttributes(typeof(SerializableAttribute), false)
            .Length > 0;

        await Assert.That(hasAttribute).IsTrue();
    }
}
