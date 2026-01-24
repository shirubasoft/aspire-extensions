namespace SharedResources.Tests.Annotations;

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.SharedResources;

public class SharedResourceAnnotationTests
{
    [Test]
    public async Task CreatingAnnotationWithAllRequiredPropertiesSucceeds()
    {
        // Arrange & Act
        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "owner/repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build -t my-service ."
        };

        // Assert
        await Assert.That(annotation.GitHubRepository).IsEqualTo("owner/repo");
        await Assert.That(annotation.ServiceName).IsEqualTo("my-service");
        await Assert.That(annotation.ImageBuildCommand).IsEqualTo("docker build -t my-service .");
    }

    [Test]
    public async Task GetEffectiveImageName_ReturnsImageName_WhenImageNameIsSet()
    {
        // Arrange
        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "owner/repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build -t my-service .",
            ImageName = "custom-image-name"
        };

        // Act
        var effectiveImageName = annotation.GetEffectiveImageName();

        // Assert
        await Assert.That(effectiveImageName).IsEqualTo("custom-image-name");
    }

    [Test]
    public async Task GetEffectiveImageName_ReturnsServiceName_WhenImageNameIsNull()
    {
        // Arrange
        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "owner/repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build -t my-service .",
            ImageName = null
        };

        // Act
        var effectiveImageName = annotation.GetEffectiveImageName();

        // Assert
        await Assert.That(effectiveImageName).IsEqualTo("my-service");
    }

    [Test]
    public async Task DefaultBranch_DefaultsToMain()
    {
        // Arrange & Act
        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "owner/repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build -t my-service ."
        };

        // Assert
        await Assert.That(annotation.DefaultBranch).IsEqualTo("main");
    }

    [Test]
    public async Task AnnotationImplementsIResourceAnnotation()
    {
        // Arrange
        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "owner/repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build -t my-service ."
        };

        // Act
        var isResourceAnnotation = annotation is IResourceAnnotation;

        // Assert
        await Assert.That(isResourceAnnotation).IsTrue();
    }
}
