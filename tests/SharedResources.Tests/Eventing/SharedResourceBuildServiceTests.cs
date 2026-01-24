using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.SharedResources;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace SharedResources.Tests.Eventing;

/// <summary>
/// Unit tests for <see cref="SharedResourceBuildService"/>.
/// </summary>
public class SharedResourceBuildServiceTests
{
    private readonly IRepositoryPathResolver _pathResolver;
    private readonly IGitOperations _gitOperations;
    private readonly IContainerImageService _containerService;
    private readonly ILogger<SharedResourceBuildService> _logger;
    private readonly SharedResourceBuildService _service;

    public SharedResourceBuildServiceTests()
    {
        _pathResolver = Substitute.For<IRepositoryPathResolver>();
        _gitOperations = Substitute.For<IGitOperations>();
        _containerService = Substitute.For<IContainerImageService>();
        _logger = Substitute.For<ILogger<SharedResourceBuildService>>();

        _service = new SharedResourceBuildService(
            _pathResolver,
            _gitOperations,
            _containerService,
            _logger);
    }

    #region Constructor Tests

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenPathResolverIsNull()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => new SharedResourceBuildService(
                null!,
                _gitOperations,
                _containerService,
                _logger));

        await Assert.That(exception.ParamName).IsEqualTo("pathResolver");
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenGitOperationsIsNull()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => new SharedResourceBuildService(
                _pathResolver,
                null!,
                _containerService,
                _logger));

        await Assert.That(exception.ParamName).IsEqualTo("gitOperations");
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenContainerServiceIsNull()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => new SharedResourceBuildService(
                _pathResolver,
                _gitOperations,
                null!,
                _logger));

        await Assert.That(exception.ParamName).IsEqualTo("containerService");
    }

    [Test]
    public async Task Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(
            () => new SharedResourceBuildService(
                _pathResolver,
                _gitOperations,
                _containerService,
                null!));

        await Assert.That(exception.ParamName).IsEqualTo("logger");
    }

    #endregion

    #region OnBeforeStartAsync Tests - No Shared Resources

    [Test]
    public async Task OnBeforeStartAsync_CompletesWithoutError_WhenNoSharedResources()
    {
        // Arrange
        var model = CreateDistributedApplicationModel();
        var @event = CreateBeforeStartEvent(model);

        _containerService.IsDockerAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        // Act - should complete without throwing
        await _service.OnBeforeStartAsync(@event, CancellationToken.None);

        // Assert
        await _containerService.DidNotReceive().BuildImageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region OnBeforeStartAsync Tests - Docker Not Available

    [Test]
    public async Task OnBeforeStartAsync_ThrowsSharedResourceBuildException_WhenDockerNotAvailable()
    {
        // Arrange
        var model = CreateDistributedApplicationModel();
        var @event = CreateBeforeStartEvent(model);

        _containerService.IsDockerAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SharedResourceBuildException>(
            async () => await _service.OnBeforeStartAsync(@event, CancellationToken.None));

        await Assert.That(exception.Message).Contains("Docker is not available");
    }

    #endregion

    #region OnBeforeStartAsync Tests - Image Exists

    [Test]
    public async Task OnBeforeStartAsync_SkipsBuild_WhenImageExists()
    {
        // Arrange
        var annotation = CreateSharedResourceAnnotation();
        var resource = CreateContainerResource("test-resource", annotation);
        var model = CreateDistributedApplicationModel(resource);
        var @event = CreateBeforeStartEvent(model);

        SetupSuccessfulMocks(annotation);
        _containerService.ImageExistsLocallyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await _service.OnBeforeStartAsync(@event, CancellationToken.None);

        // Assert
        await _containerService.DidNotReceive().BuildImageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region OnBeforeStartAsync Tests - Image Missing - Build Successfully

    [Test]
    public async Task OnBeforeStartAsync_BuildsImage_WhenImageMissing()
    {
        // Arrange
        var annotation = CreateSharedResourceAnnotation();
        var resource = CreateContainerResource("test-resource", annotation);
        var model = CreateDistributedApplicationModel(resource);
        var @event = CreateBeforeStartEvent(model);

        SetupSuccessfulMocks(annotation);
        _containerService.ImageExistsLocallyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await _service.OnBeforeStartAsync(@event, CancellationToken.None);

        // Assert
        await _containerService.Received(1).BuildImageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region OnBeforeStartAsync Tests - Dirty Working Tree Warning

    [Test]
    public async Task OnBeforeStartAsync_LogsWarning_WhenWorkingTreeDirty()
    {
        // Arrange
        var annotation = CreateSharedResourceAnnotation();
        var resource = CreateContainerResource("test-resource", annotation);
        var model = CreateDistributedApplicationModel(resource);
        var @event = CreateBeforeStartEvent(model);

        SetupSuccessfulMocks(annotation);
        _gitOperations.HasUncommittedChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _containerService.ImageExistsLocallyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await _service.OnBeforeStartAsync(@event, CancellationToken.None);

        // Assert - verify warning was logged
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("uncommitted changes")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    #endregion

    #region OnBeforeStartAsync Tests - Repository Path Not Found

    [Test]
    public async Task OnBeforeStartAsync_AggregatesError_WhenRepositoryPathNotFound()
    {
        // Arrange
        var annotation = CreateSharedResourceAnnotation();
        var resource = CreateContainerResource("test-resource", annotation);
        var model = CreateDistributedApplicationModel(resource);
        var @event = CreateBeforeStartEvent(model);

        _containerService.IsDockerAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _pathResolver.ResolveRepositoryPathAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new RepositoryNotFoundException("Repository not found", "myorg/test-repo", "test-service"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SharedResourceBuildException>(
            async () => await _service.OnBeforeStartAsync(@event, CancellationToken.None));

        await Assert.That(exception.Errors.Count).IsEqualTo(1);
        await Assert.That(exception.Errors[0].ServiceName).IsEqualTo("test-service");
        await Assert.That(exception.Errors[0].GitHubRepository).IsEqualTo("myorg/test-repo");
    }

    #endregion

    #region OnBeforeStartAsync Tests - Multiple Failures Aggregated

    [Test]
    public async Task OnBeforeStartAsync_AggregatesMultipleErrors()
    {
        // Arrange
        var annotation1 = CreateSharedResourceAnnotation("service1", "myorg/repo1");
        var annotation2 = CreateSharedResourceAnnotation("service2", "myorg/repo2");
        var resource1 = CreateContainerResource("resource1", annotation1);
        var resource2 = CreateContainerResource("resource2", annotation2);
        var model = CreateDistributedApplicationModel(resource1, resource2);
        var @event = CreateBeforeStartEvent(model);

        _containerService.IsDockerAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        // Both repositories fail
        _pathResolver.ResolveRepositoryPathAsync("myorg/repo1", "service1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new RepositoryNotFoundException("Repository not found", "myorg/repo1", "service1"));
        _pathResolver.ResolveRepositoryPathAsync("myorg/repo2", "service2", Arg.Any<CancellationToken>())
            .ThrowsAsync(new RepositoryNotFoundException("Repository not found", "myorg/repo2", "service2"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SharedResourceBuildException>(
            async () => await _service.OnBeforeStartAsync(@event, CancellationToken.None));

        await Assert.That(exception.Errors.Count).IsEqualTo(2);
        await Assert.That(exception.Errors[0].ServiceName).IsEqualTo("service1");
        await Assert.That(exception.Errors[1].ServiceName).IsEqualTo("service2");
    }

    #endregion

    #region SubstitutePlaceholders Tests

    [Test]
    public async Task SubstitutePlaceholders_ReplacesAllPlaceholders()
    {
        // Arrange
        var template = "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag} -p:Context={RepoPath}";
        var repoPath = "/home/user/repos/my-repo";
        var projectPath = "src/Api/Api.csproj";
        var imageName = "my-api";
        var imageTag = "abc1234";

        // Act
        var result = _service.SubstitutePlaceholders(template, repoPath, projectPath, imageName, imageTag);

        // Assert
        await Assert.That(result).Contains("/home/user/repos/my-repo/src/Api/Api.csproj");
        await Assert.That(result).Contains("ContainerRepository=my-api");
        await Assert.That(result).Contains("ContainerImageTag=abc1234");
        await Assert.That(result).Contains("Context=/home/user/repos/my-repo");
        await Assert.That(result).DoesNotContain("{ProjectPath}");
        await Assert.That(result).DoesNotContain("{ImageName}");
        await Assert.That(result).DoesNotContain("{ImageTag}");
        await Assert.That(result).DoesNotContain("{RepoPath}");
    }

    [Test]
    public async Task SubstitutePlaceholders_HandlesNullProjectPath()
    {
        // Arrange
        var template = "docker build -t {ImageName}:{ImageTag} {RepoPath}";
        var repoPath = "/home/user/repos/my-repo";
        var imageName = "my-api";
        var imageTag = "abc1234";

        // Act
        var result = _service.SubstitutePlaceholders(template, repoPath, null, imageName, imageTag);

        // Assert
        await Assert.That(result).IsEqualTo("docker build -t my-api:abc1234 /home/user/repos/my-repo");
    }

    [Test]
    public async Task SubstitutePlaceholders_PreservesUnknownPlaceholders()
    {
        // Arrange
        var template = "command {UnknownPlaceholder} {ImageName}";
        var repoPath = "/repo";
        var imageName = "my-api";
        var imageTag = "abc1234";

        // Act
        var result = _service.SubstitutePlaceholders(template, repoPath, null, imageName, imageTag);

        // Assert
        await Assert.That(result).Contains("{UnknownPlaceholder}");
        await Assert.That(result).Contains("my-api");
    }

    #endregion

    #region GetSharedResources Tests

    [Test]
    public async Task GetSharedResources_ReturnsEmptyList_WhenNoResources()
    {
        // Arrange
        var model = CreateDistributedApplicationModel();

        // Act
        var result = _service.GetSharedResources(model);

        // Assert
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetSharedResources_ReturnsEmptyList_WhenNoContainerResources()
    {
        // Arrange
        var model = CreateDistributedApplicationModel();

        // Act
        var result = _service.GetSharedResources(model);

        // Assert
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetSharedResources_ReturnsResources_WithSharedResourceAnnotation()
    {
        // Arrange
        var annotation = CreateSharedResourceAnnotation();
        var resource = CreateContainerResource("test-resource", annotation);
        var model = CreateDistributedApplicationModel(resource);

        // Act
        var result = _service.GetSharedResources(model);

        // Assert
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Resource.Name).IsEqualTo("test-resource");
        await Assert.That(result[0].Annotation.ServiceName).IsEqualTo("test-service");
    }

    [Test]
    public async Task GetSharedResources_IgnoresContainerResources_WithoutSharedResourceAnnotation()
    {
        // Arrange
        var annotatedResource = CreateContainerResource("annotated", CreateSharedResourceAnnotation());
        var unannotatedResource = CreateContainerResource("unannotated", null);
        var model = CreateDistributedApplicationModel(annotatedResource, unannotatedResource);

        // Act
        var result = _service.GetSharedResources(model);

        // Assert
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Resource.Name).IsEqualTo("annotated");
    }

    #endregion

    #region UpdateContainerImageTag Tests

    [Test]
    public async Task UpdateContainerImageTag_AddsAnnotation_WhenNoneExists()
    {
        // Arrange
        var resource = CreateContainerResource("test-resource", null);

        // Act
        _service.UpdateContainerImageTag(resource, "my-image", "abc1234");

        // Assert
        var imageAnnotation = resource.Annotations.OfType<ContainerImageAnnotation>().FirstOrDefault();
        await Assert.That(imageAnnotation).IsNotNull();
        await Assert.That(imageAnnotation!.Image).IsEqualTo("my-image");
        await Assert.That(imageAnnotation.Tag).IsEqualTo("abc1234");
    }

    [Test]
    public async Task UpdateContainerImageTag_UpdatesAnnotation_WhenExists()
    {
        // Arrange
        var resource = CreateContainerResource("test-resource", null);
        resource.Annotations.Add(new ContainerImageAnnotation
        {
            Image = "old-image",
            Tag = "old-tag",
            Registry = "docker.io"
        });

        // Act
        _service.UpdateContainerImageTag(resource, "new-image", "new-tag");

        // Assert
        var imageAnnotations = resource.Annotations.OfType<ContainerImageAnnotation>().ToList();
        await Assert.That(imageAnnotations.Count).IsEqualTo(1);
        await Assert.That(imageAnnotations[0].Image).IsEqualTo("new-image");
        await Assert.That(imageAnnotations[0].Tag).IsEqualTo("new-tag");
        // Registry should be preserved since we update in place
        await Assert.That(imageAnnotations[0].Registry).IsEqualTo("docker.io");
    }

    #endregion

    #region Helper Methods

    private static SharedResourceAnnotation CreateSharedResourceAnnotation(
        string serviceName = "test-service",
        string gitHubRepository = "myorg/test-repo")
    {
        return new SharedResourceAnnotation
        {
            ServiceName = serviceName,
            GitHubRepository = gitHubRepository,
            ImageBuildCommand = "docker build -t {ImageName}:{ImageTag} {RepoPath}"
        };
    }

    private static ContainerResource CreateContainerResource(
        string name,
        SharedResourceAnnotation? annotation)
    {
        var resource = new ContainerResource(name);

        if (annotation is not null)
        {
            resource.Annotations.Add(annotation);
        }

        return resource;
    }

    private static DistributedApplicationModel CreateDistributedApplicationModel(
        params ContainerResource[] resources)
    {
        var resourceCollection = new ResourceCollection();

        foreach (var resource in resources)
        {
            resourceCollection.Add(resource);
        }

        return new DistributedApplicationModel(resourceCollection);
    }

    private static BeforeStartEvent CreateBeforeStartEvent(DistributedApplicationModel model)
    {
        var services = Substitute.For<IServiceProvider>();
        return new BeforeStartEvent(services, model);
    }

    private void SetupSuccessfulMocks(SharedResourceAnnotation annotation)
    {
        _containerService.IsDockerAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _pathResolver.ResolveRepositoryPathAsync(
            annotation.GitHubRepository,
            annotation.ServiceName,
            Arg.Any<CancellationToken>())
            .Returns("/home/user/repos/test-repo");
        _gitOperations.IsGitRepositoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _gitOperations.GetCurrentCommitShaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("abc1234");
        _gitOperations.HasUncommittedChangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    #endregion
}

/// <summary>
/// Helper class to create a ResourceCollection for testing.
/// </summary>
internal class ResourceCollection : IResourceCollection
{
    private readonly List<IResource> _resources = new();

    public int Count => _resources.Count;

    public bool IsReadOnly => false;

    public IResource this[int index]
    {
        get => _resources[index];
        set => _resources[index] = value;
    }

    public void Add(IResource resource) => _resources.Add(resource);

    public void Clear() => _resources.Clear();

    public bool Contains(IResource item) => _resources.Contains(item);

    public void CopyTo(IResource[] array, int arrayIndex) => _resources.CopyTo(array, arrayIndex);

    public IEnumerator<IResource> GetEnumerator() => _resources.GetEnumerator();

    public int IndexOf(IResource item) => _resources.IndexOf(item);

    public void Insert(int index, IResource item) => _resources.Insert(index, item);

    public bool Remove(IResource item) => _resources.Remove(item);

    public void RemoveAt(int index) => _resources.RemoveAt(index);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
