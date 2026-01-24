using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.SharedResources;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace SharedResources.Tests.Extensions;

public class SharedResourceExtensionsTests
{
    #region WithSharedResourceMetadata (with parameters) Tests

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_CreatesCorrectAnnotation()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();
        SharedResourceAnnotation? capturedAnnotation = null;

        builder.WithAnnotation(Arg.Do<SharedResourceAnnotation>(a => capturedAnnotation = a))
            .Returns(builder);

        // Act
        var result = builder.WithSharedResourceMetadata(
            gitHubRepository: "myorg/my-repo",
            serviceName: "my-service",
            imageBuildCommand: "docker build -t {ImageName}:{ImageTag} .");

        // Assert
        await Assert.That(capturedAnnotation).IsNotNull();
        await Assert.That(capturedAnnotation!.GitHubRepository).IsEqualTo("myorg/my-repo");
        await Assert.That(capturedAnnotation.ServiceName).IsEqualTo("my-service");
        await Assert.That(capturedAnnotation.ImageBuildCommand).IsEqualTo("docker build -t {ImageName}:{ImageTag} .");
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ReturnsBuilderForChaining()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();
        builder.WithAnnotation(Arg.Any<SharedResourceAnnotation>()).Returns(builder);

        // Act
        var result = builder.WithSharedResourceMetadata(
            gitHubRepository: "myorg/my-repo",
            serviceName: "my-service",
            imageBuildCommand: "docker build -t test .");

        // Assert
        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithConfigureAction_InvokesConfigureAndAppliesOptions()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();
        SharedResourceAnnotation? capturedAnnotation = null;

        builder.WithAnnotation(Arg.Do<SharedResourceAnnotation>(a => capturedAnnotation = a))
            .Returns(builder);

        // Act
        builder.WithSharedResourceMetadata(
            gitHubRepository: "myorg/my-repo",
            serviceName: "my-service",
            imageBuildCommand: "dotnet publish {ProjectPath}",
            configure: options =>
            {
                options.ProjectPath = "src/Api/Api.csproj";
                options.DefaultBranch = "develop";
                options.ImageName = "custom-image";
            });

        // Assert
        await Assert.That(capturedAnnotation).IsNotNull();
        await Assert.That(capturedAnnotation!.ProjectPath).IsEqualTo("src/Api/Api.csproj");
        await Assert.That(capturedAnnotation.DefaultBranch).IsEqualTo("develop");
        await Assert.That(capturedAnnotation.ImageName).IsEqualTo("custom-image");
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentNullException_WhenBuilderIsNull()
    {
        // Arrange
        IResourceBuilder<ContainerResource>? builder = null;

        // Act & Assert
        await Assert.That(() =>
            builder!.WithSharedResourceMetadata(
                gitHubRepository: "myorg/my-repo",
                serviceName: "my-service",
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentNullException_WhenGitHubRepositoryIsNull()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: null!,
                serviceName: "my-service",
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentNullException_WhenServiceNameIsNull()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: "myorg/my-repo",
                serviceName: null!,
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentNullException_WhenImageBuildCommandIsNull()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: "myorg/my-repo",
                serviceName: "my-service",
                imageBuildCommand: null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentException_WhenGitHubRepositoryIsEmpty()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        var exception = await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: "",
                serviceName: "my-service",
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("gitHubRepository");
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentException_WhenGitHubRepositoryHasNoSlash()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        var exception = await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: "invalid-format",
                serviceName: "my-service",
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("gitHubRepository");
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentException_WhenGitHubRepositoryHasEmptyOrg()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        var exception = await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: "/my-repo",
                serviceName: "my-service",
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("gitHubRepository");
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithParameters_ThrowsArgumentException_WhenGitHubRepositoryHasEmptyRepo()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        var exception = await Assert.That(() =>
            builder.WithSharedResourceMetadata(
                gitHubRepository: "myorg/",
                serviceName: "my-service",
                imageBuildCommand: "docker build ."))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("gitHubRepository");
    }

    #endregion

    #region WithSharedResourceMetadata (with annotation) Tests

    [Test]
    public async Task WithSharedResourceMetadata_WithAnnotation_AttachesAnnotationCorrectly()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();
        SharedResourceAnnotation? capturedAnnotation = null;

        builder.WithAnnotation(Arg.Do<SharedResourceAnnotation>(a => capturedAnnotation = a))
            .Returns(builder);

        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "myorg/my-repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build .",
            ProjectPath = "src/Api/Api.csproj"
        };

        // Act
        builder.WithSharedResourceMetadata(annotation);

        // Assert
        await Assert.That(capturedAnnotation).IsSameReferenceAs(annotation);
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithAnnotation_ReturnsBuilderForChaining()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();
        builder.WithAnnotation(Arg.Any<SharedResourceAnnotation>()).Returns(builder);

        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "myorg/my-repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build ."
        };

        // Act
        var result = builder.WithSharedResourceMetadata(annotation);

        // Assert
        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithAnnotation_ThrowsArgumentNullException_WhenBuilderIsNull()
    {
        // Arrange
        IResourceBuilder<ContainerResource>? builder = null;
        var annotation = new SharedResourceAnnotation
        {
            GitHubRepository = "myorg/my-repo",
            ServiceName = "my-service",
            ImageBuildCommand = "docker build ."
        };

        // Act & Assert
        await Assert.That(() =>
            builder!.WithSharedResourceMetadata(annotation))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithSharedResourceMetadata_WithAnnotation_ThrowsArgumentNullException_WhenAnnotationIsNull()
    {
        // Arrange
        var builder = CreateMockResourceBuilder();

        // Act & Assert
        await Assert.That(() =>
            builder.WithSharedResourceMetadata((SharedResourceAnnotation)null!))
            .Throws<ArgumentNullException>();
    }

    #endregion

    #region AddSharedResourceSupport Tests

    [Test]
    public async Task AddSharedResourceSupport_ThrowsArgumentNullException_WhenBuilderIsNull()
    {
        // Arrange
        IDistributedApplicationBuilder? builder = null;

        // Act & Assert
        await Assert.That(() =>
            builder!.AddSharedResourceSupport())
            .Throws<ArgumentNullException>();
    }

    #endregion

    #region Helper Methods

    private static IResourceBuilder<ContainerResource> CreateMockResourceBuilder()
    {
        return Substitute.For<IResourceBuilder<ContainerResource>>();
    }

    #endregion
}
