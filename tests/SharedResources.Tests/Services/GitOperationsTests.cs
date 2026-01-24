using Aspire.Hosting.SharedResources;
using Microsoft.Extensions.Logging;

namespace SharedResources.Tests.Services;

/// <summary>
/// Integration tests for <see cref="GitOperations"/>.
/// These tests run against the actual git repository at /home/danielreis/code/aspire-extensions.
/// </summary>
public class GitOperationsTests
{
    private const string TestRepositoryPath = "/home/danielreis/code/aspire-extensions";
    private readonly GitOperations _gitOperations;

    public GitOperationsTests()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<GitOperations>();
        _gitOperations = new GitOperations(logger);
    }

    [Test]
    public async Task GetCurrentCommitShaAsync_ReturnsSevenCharacterSha()
    {
        // Act
        var sha = await _gitOperations.GetCurrentCommitShaAsync(TestRepositoryPath);

        // Assert
        await Assert.That(sha).IsNotNull();
        await Assert.That(sha.Length).IsEqualTo(7);
        await Assert.That(sha).Matches("^[a-f0-9]{7}$");
    }

    [Test]
    public async Task GetCurrentBranchAsync_ReturnsBranchName()
    {
        // Act
        var branch = await _gitOperations.GetCurrentBranchAsync(TestRepositoryPath);

        // Assert
        await Assert.That(branch).IsNotNull();
        await Assert.That(branch.Length).IsGreaterThan(0);
        // Branch name should not contain whitespace
        await Assert.That(branch.Trim()).IsEqualTo(branch);
    }

    [Test]
    public async Task HasUncommittedChangesAsync_ReturnsCorrectState()
    {
        // Act
        var hasChanges = await _gitOperations.HasUncommittedChangesAsync(TestRepositoryPath);

        // Assert - we just verify it returns a boolean without throwing
        await Assert.That(hasChanges).IsTypeOf<bool>();
    }

    [Test]
    public async Task IsGitRepositoryAsync_ReturnsTrue_ForGitRepository()
    {
        // Act
        var isRepo = await _gitOperations.IsGitRepositoryAsync(TestRepositoryPath);

        // Assert
        await Assert.That(isRepo).IsTrue();
    }

    [Test]
    public async Task IsGitRepositoryAsync_ReturnsFalse_ForNonGitDirectory()
    {
        // Arrange
        var tempPath = "/tmp";

        // Act
        var isRepo = await _gitOperations.IsGitRepositoryAsync(tempPath);

        // Assert
        await Assert.That(isRepo).IsFalse();
    }

    [Test]
    public async Task IsGitRepositoryAsync_ReturnsFalse_ForNonExistentPath()
    {
        // Arrange
        var nonExistentPath = "/path/that/does/not/exist/at/all";

        // Act
        var isRepo = await _gitOperations.IsGitRepositoryAsync(nonExistentPath);

        // Assert
        await Assert.That(isRepo).IsFalse();
    }

    [Test]
    public async Task GetCurrentCommitShaAsync_ThrowsForInvalidPath()
    {
        // Arrange
        var invalidPath = "/path/that/does/not/exist";

        // Act & Assert
        await Assert.ThrowsAsync<GitOperationException>(
            async () => await _gitOperations.GetCurrentCommitShaAsync(invalidPath));
    }

    [Test]
    public async Task GetCurrentCommitShaAsync_ThrowsForNullPath()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _gitOperations.GetCurrentCommitShaAsync(null!));
    }

    [Test]
    public async Task GetCurrentCommitShaAsync_ThrowsForEmptyPath()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _gitOperations.GetCurrentCommitShaAsync(string.Empty));
    }

    [Test]
    public async Task GetCurrentCommitShaAsync_ThrowsForWhitespacePath()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _gitOperations.GetCurrentCommitShaAsync("   "));
    }

    [Test]
    public async Task GetCurrentBranchAsync_ThrowsForNullPath()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _gitOperations.GetCurrentBranchAsync(null!));
    }

    [Test]
    public async Task HasUncommittedChangesAsync_ThrowsForNullPath()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _gitOperations.HasUncommittedChangesAsync(null!));
    }

    [Test]
    public async Task IsGitRepositoryAsync_ThrowsForNullPath()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _gitOperations.IsGitRepositoryAsync(null!));
    }

    [Test]
    public async Task Constructor_ThrowsForNullLogger()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Task.FromResult(new GitOperations(null!)));
    }
}
