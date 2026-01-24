namespace SharedResources.Tests.Configuration;

using Aspire.Hosting.SharedResources;

public class SharedResourceOptionsTests
{
    [Test]
    public async Task DefaultBranch_DefaultsToMain()
    {
        // Arrange & Act
        var options = new SharedResourceOptions();

        // Assert
        await Assert.That(options.DefaultBranch).IsEqualTo("main");
    }

    [Test]
    public async Task ProjectPath_DefaultsToNull()
    {
        // Arrange & Act
        var options = new SharedResourceOptions();

        // Assert
        await Assert.That(options.ProjectPath).IsNull();
    }

    [Test]
    public async Task ImageName_DefaultsToNull()
    {
        // Arrange & Act
        var options = new SharedResourceOptions();

        // Assert
        await Assert.That(options.ImageName).IsNull();
    }

    [Test]
    public async Task DefaultBranch_IsSettable()
    {
        // Arrange
        var options = new SharedResourceOptions();

        // Act
        options.DefaultBranch = "develop";

        // Assert
        await Assert.That(options.DefaultBranch).IsEqualTo("develop");
    }

    [Test]
    public async Task ProjectPath_IsSettable()
    {
        // Arrange
        var options = new SharedResourceOptions();

        // Act
        options.ProjectPath = "src/Api/Api.csproj";

        // Assert
        await Assert.That(options.ProjectPath).IsEqualTo("src/Api/Api.csproj");
    }

    [Test]
    public async Task ImageName_IsSettable()
    {
        // Arrange
        var options = new SharedResourceOptions();

        // Act
        options.ImageName = "my-custom-image";

        // Assert
        await Assert.That(options.ImageName).IsEqualTo("my-custom-image");
    }

    [Test]
    public async Task CanBeUsedWithActionPattern()
    {
        // Arrange
        var options = new SharedResourceOptions();
        Action<SharedResourceOptions> configureOptions = opt =>
        {
            opt.DefaultBranch = "feature/test";
            opt.ProjectPath = "src/Service/Service.csproj";
            opt.ImageName = "test-service";
        };

        // Act
        configureOptions(options);

        // Assert
        await Assert.That(options.DefaultBranch).IsEqualTo("feature/test");
        await Assert.That(options.ProjectPath).IsEqualTo("src/Service/Service.csproj");
        await Assert.That(options.ImageName).IsEqualTo("test-service");
    }
}
