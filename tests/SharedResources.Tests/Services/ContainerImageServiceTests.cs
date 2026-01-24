namespace SharedResources.Tests.Services;

using Aspire.Hosting.SharedResources;

public class ContainerImageServiceTests
{
    #region ImageExistsLocallyAsync Argument Validation

    [Test]
    public async Task ImageExistsLocallyAsync_WithNullImageName_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.ImageExistsLocallyAsync(null!, "tag"))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "imageName");
    }

    [Test]
    public async Task ImageExistsLocallyAsync_WithNullTag_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.ImageExistsLocallyAsync("my-image", null!))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "tag");
    }

    #endregion

    #region BuildImageAsync Argument Validation

    [Test]
    public async Task BuildImageAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.BuildImageAsync(null!, "/path/to/dir"))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "command");
    }

    [Test]
    public async Task BuildImageAsync_WithNullWorkingDirectory_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.BuildImageAsync("dotnet publish", null!))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "workingDirectory");
    }

    #endregion

    #region TagImageAsync Argument Validation

    [Test]
    public async Task TagImageAsync_WithNullSourceImage_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.TagImageAsync(null!, "target:tag"))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "sourceImage");
    }

    [Test]
    public async Task TagImageAsync_WithNullTargetImage_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.TagImageAsync("source:tag", null!))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "targetImage");
    }

    #endregion

    #region GetImageInfoAsync Argument Validation

    [Test]
    public async Task GetImageInfoAsync_WithNullImageName_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = new TestLogger<ContainerImageService>();
        var service = new ContainerImageService(logger);

        // Act & Assert
        await Assert.That(async () => await service.GetImageInfoAsync(null!))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "imageName");
    }

    #endregion

    #region ParseCommand Tests

    [Test]
    public async Task ParseCommand_WithDotnetPublishCommand_SplitsCorrectly()
    {
        // Arrange
        var command = "dotnet publish /path/to/project.csproj --os linux /t:PublishContainer";

        // Act
        var (executable, arguments) = ContainerImageService.ParseCommand(command);

        // Assert
        await Assert.That(executable).IsEqualTo("dotnet");
        await Assert.That(arguments).IsEqualTo("publish /path/to/project.csproj --os linux /t:PublishContainer");
    }

    [Test]
    public async Task ParseCommand_WithDockerBuildCommand_SplitsCorrectly()
    {
        // Arrange
        var command = "docker build -t my-image:latest .";

        // Act
        var (executable, arguments) = ContainerImageService.ParseCommand(command);

        // Assert
        await Assert.That(executable).IsEqualTo("docker");
        await Assert.That(arguments).IsEqualTo("build -t my-image:latest .");
    }

    [Test]
    public async Task ParseCommand_WithSingleWordCommand_ReturnsEmptyArguments()
    {
        // Arrange
        var command = "docker";

        // Act
        var (executable, arguments) = ContainerImageService.ParseCommand(command);

        // Assert
        await Assert.That(executable).IsEqualTo("docker");
        await Assert.That(arguments).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ParseCommand_WithLeadingWhitespace_TrimsCorrectly()
    {
        // Arrange
        var command = "   dotnet build";

        // Act
        var (executable, arguments) = ContainerImageService.ParseCommand(command);

        // Assert
        await Assert.That(executable).IsEqualTo("dotnet");
        await Assert.That(arguments).IsEqualTo("build");
    }

    [Test]
    public async Task ParseCommand_WithTrailingWhitespace_TrimsCorrectly()
    {
        // Arrange
        var command = "dotnet build   ";

        // Act
        var (executable, arguments) = ContainerImageService.ParseCommand(command);

        // Assert
        // The implementation trims the entire command first, so trailing whitespace is removed
        await Assert.That(executable).IsEqualTo("dotnet");
        await Assert.That(arguments).IsEqualTo("build");
    }

    [Test]
    public async Task ParseCommand_WithComplexDotnetPublishCommand_SplitsCorrectly()
    {
        // Arrange
        var command = "dotnet publish /home/user/repo/src/Api/Api.csproj --os linux /t:PublishContainer -p:ContainerRepository=my-api -p:ContainerImageTag=abc1234";

        // Act
        var (executable, arguments) = ContainerImageService.ParseCommand(command);

        // Assert
        await Assert.That(executable).IsEqualTo("dotnet");
        await Assert.That(arguments).IsEqualTo("publish /home/user/repo/src/Api/Api.csproj --os linux /t:PublishContainer -p:ContainerRepository=my-api -p:ContainerImageTag=abc1234");
    }

    #endregion

    #region Constructor Validation

    [Test]
    public async Task Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.That(() => new ContainerImageService(null!))
            .ThrowsExactly<ArgumentNullException>()
            .And.Satisfies(ex => ex.ParamName == "logger");
    }

    #endregion

    /// <summary>
    /// Simple test logger implementation for unit testing.
    /// </summary>
    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // No-op for testing
        }
    }
}
