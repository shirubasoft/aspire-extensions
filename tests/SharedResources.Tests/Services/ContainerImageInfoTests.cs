namespace SharedResources.Tests.Services;

using Aspire.Hosting.SharedResources;

public class ContainerImageInfoTests
{
    [Test]
    public async Task AllRequiredPropertiesCanBeSetViaInit()
    {
        // Arrange
        var id = "sha256:abc123";
        var fullName = "my-api-service:latest";
        var created = DateTimeOffset.UtcNow;
        var size = 1024L;

        // Act
        var info = new ContainerImageInfo
        {
            Id = id,
            FullName = fullName,
            Created = created,
            Size = size
        };

        // Assert
        await Assert.That(info.Id).IsEqualTo(id);
        await Assert.That(info.FullName).IsEqualTo(fullName);
        await Assert.That(info.Created).IsEqualTo(created);
        await Assert.That(info.Size).IsEqualTo(size);
    }

    [Test]
    public async Task FormattedSize_ReturnsBytes_WhenSizeIsLessThan1024()
    {
        // Arrange
        var info = new ContainerImageInfo
        {
            Id = "test-id",
            FullName = "test-image:latest",
            Created = DateTimeOffset.UtcNow,
            Size = 500
        };

        // Act
        var formattedSize = info.FormattedSize;

        // Assert
        await Assert.That(formattedSize).IsEqualTo("500 B");
    }

    [Test]
    public async Task FormattedSize_ReturnsKilobytes_WhenSizeIsInKBRange()
    {
        // Arrange
        var info = new ContainerImageInfo
        {
            Id = "test-id",
            FullName = "test-image:latest",
            Created = DateTimeOffset.UtcNow,
            Size = 2048
        };

        // Act
        var formattedSize = info.FormattedSize;

        // Assert
        await Assert.That(formattedSize).IsEqualTo("2 KB");
    }

    [Test]
    public async Task FormattedSize_ReturnsMegabytes_WhenSizeIsInMBRange()
    {
        // Arrange
        var info = new ContainerImageInfo
        {
            Id = "test-id",
            FullName = "test-image:latest",
            Created = DateTimeOffset.UtcNow,
            Size = 2097152 // 2 * 1024 * 1024
        };

        // Act
        var formattedSize = info.FormattedSize;

        // Assert
        await Assert.That(formattedSize).IsEqualTo("2 MB");
    }

    [Test]
    public async Task FormattedSize_ReturnsGigabytes_WhenSizeIsInGBRange()
    {
        // Arrange
        var info = new ContainerImageInfo
        {
            Id = "test-id",
            FullName = "test-image:latest",
            Created = DateTimeOffset.UtcNow,
            Size = 2147483648 // 2 * 1024 * 1024 * 1024
        };

        // Act
        var formattedSize = info.FormattedSize;

        // Assert
        await Assert.That(formattedSize).IsEqualTo("2 GB");
    }

    [Test]
    public async Task FormattedSize_FormatsWithTwoDecimalPlaces_WhenSizeIsNotExact()
    {
        // Arrange
        var info = new ContainerImageInfo
        {
            Id = "test-id",
            FullName = "test-image:latest",
            Created = DateTimeOffset.UtcNow,
            Size = 1536 // 1.5 * 1024
        };

        // Act
        var formattedSize = info.FormattedSize;

        // Assert
        await Assert.That(formattedSize).IsEqualTo("1.5 KB");
    }
}
