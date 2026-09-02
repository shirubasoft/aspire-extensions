using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class SafeMaterializationDefaultsTests
{
    [Fact]
    public void Defaults_keep_repository_mutation_out_of_run()
    {
        var options = new ModularAppHostsOptions();

        Assert.Equal(ModuleProjectMode.Auto, options.ProjectMode);
        Assert.True(options.UpdateRepositoriesOnInitialize);
        Assert.False(options.RefreshBuildRepositoriesOnRun);
        Assert.Equal("git", options.GitExecutablePath);
        Assert.Equal("gh", options.GitHubCliPath);
        Assert.Equal(TimeSpan.FromMinutes(2), options.RepositoryCommandTimeout);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ImageBuildTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ImageTransferTimeout);
    }

    [Fact]
    public void Auto_mode_runs_local_projects_directly()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path);
        var module = ExportProject(builder, projectPath);

        builder.AddModule(module);

        Assert.Single(builder.Resources.OfType<ProjectResource>());
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
    }

    [Fact]
    public void Auto_mode_runs_imported_projects_as_containers_with_deferred_preparation()
    {
        using var source = TemporaryDirectory.Create();
        using var appHost = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(appHost.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(source.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainerWithCommand(
                    new ModuleImageCommandOptions("example/orders-api", "dotnet", "publish"));
        });

        builder.ImportModule(module.Name);

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
    }

    [Fact]
    public void Native_exports_remain_project_resources_and_use_Aspire_container_publishing()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path);
        var module = builder.ExportModule("orders", definition =>
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("example/orders-api"));

        builder.AddModule(module);

        var project = Assert.Single(builder.Resources.OfType<ProjectResource>());
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
        Assert.Single(project.Annotations.OfType<ModuleNativeImagePublisherAnnotation>());
        Assert.Empty(project.Annotations.OfType<ModuleImagePublisherAnnotation>());
    }

    [Fact]
    public void Native_exports_materialize_as_containers_when_container_mode_is_selected()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path).UseModuleContainers();
        ModuleResourceImage? callbackImage = null;
        var module = builder.ExportModule("orders", definition =>
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer(
                    "example/orders-api",
                    (context, container) =>
                    {
                        callbackImage = context.Image;
                        container.WithArgs("--container-mode");
                    }));

        builder.AddModule(module);

        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("example/orders-api", image.Image);
        Assert.Equal("latest", image.Tag);
        Assert.Equal("example/orders-api:latest", callbackImage?.Reference);
        Assert.Empty(container.Annotations.OfType<ModuleNativeImagePublisherAnnotation>());
        Assert.Contains(
            container.Annotations.OfType<CommandLineArgsCallbackAnnotation>(),
            annotation => annotation.Callback is not null);
    }

    [Fact]
    public void Auto_mode_materializes_imported_native_exports_as_containers()
    {
        using var source = TemporaryDirectory.Create();
        using var appHost = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(appHost.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(source.Path);
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainer("example/orders-api");
        });

        builder.ImportModule(module.Name);

        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        Assert.Single(builder.Resources.OfType<ContainerResource>());
    }

    [Fact]
    public void Fluent_container_mode_selects_containers_with_deferred_preparation()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path)
            .UseModuleContainers();
        var module = ExportProject(builder, projectPath);

        builder.AddModule(module);

        Assert.Single(builder.Resources.OfType<ContainerResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Single(container.Annotations.OfType<ModuleImagePublisherAnnotation>());
    }

    [Fact]
    public void Cli_and_mode_options_bind_from_configuration()
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);
        var section = ModularAppHostsOptions.ConfigurationSectionName;
        builder.Configuration[$"{section}:GitExecutablePath"] = "custom-git";
        builder.Configuration[$"{section}:RepositoryCommandTimeout"] = "00:00:15";
        builder.Configuration[$"{section}:ImageBuildTimeout"] = "00:20:00";
        builder.Configuration[$"{section}:ImageTransferTimeout"] = "00:05:00";
        builder.Configuration[$"{section}:ProjectMode"] = nameof(ModuleProjectMode.Project);
        builder.Configuration[$"{section}:Modules:orders:ProjectMode"] = nameof(ModuleProjectMode.Container);
        builder.Configuration[$"{section}:Modules:orders:Projects:orders-api:ProjectMode"] =
            nameof(ModuleProjectMode.Project);

        var options = ModularAppHostsOptions.FromConfiguration(builder.Configuration);

        Assert.Equal("custom-git", options.GitExecutablePath);
        Assert.Equal(TimeSpan.FromSeconds(15), options.RepositoryCommandTimeout);
        Assert.Equal(TimeSpan.FromMinutes(20), options.ImageBuildTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ImageTransferTimeout);
        Assert.Equal(ModuleProjectMode.Project, options.ProjectMode);
        Assert.Equal(ModuleProjectMode.Container, options.Modules["orders"].ProjectMode);
        Assert.Equal(
            ModuleProjectMode.Project,
            options.Modules["orders"].Projects["orders-api"].ProjectMode);
    }

    [Theory]
    [InlineData(nameof(ModularAppHostsOptions.ImageBuildTimeout))]
    [InlineData(nameof(ModularAppHostsOptions.ImageTransferTimeout))]
    public void Non_positive_image_timeouts_are_rejected(string optionName)
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ConfigureModularAppHosts(options =>
            {
                if (optionName == nameof(ModularAppHostsOptions.ImageBuildTimeout))
                {
                    options.ImageBuildTimeout = TimeSpan.Zero;
                }
                else
                {
                    options.ImageTransferTimeout = TimeSpan.Zero;
                }
            }));

        Assert.Contains(optionName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_positive_repository_timeout_is_rejected_from_configuration_or_code()
    {
        using var source = TemporaryDirectory.Create();
        var section = ModularAppHostsOptions.ConfigurationSectionName;
        var configuredBuilder = CreateBuilder(source.Path);
        configuredBuilder.Configuration[$"{section}:RepositoryCommandTimeout"] = "00:00:00";

        var configuredException = Assert.Throws<InvalidOperationException>(() =>
            configuredBuilder.ExportModule("cache", module =>
                module.AddContainer("redis", "redis")));
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), configuredException.Message);
        configuredBuilder.Configuration[$"{section}:RepositoryCommandTimeout"] = "00:00:30";
        configuredBuilder.ExportModule("cache", module =>
            module.AddContainer("redis", "redis"));

        var programmaticBuilder = CreateBuilder(source.Path);
        var programmaticException = Assert.Throws<InvalidOperationException>(() =>
            programmaticBuilder.ConfigureModularAppHosts(options =>
                options.RepositoryCommandTimeout = TimeSpan.Zero));
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), programmaticException.Message);

        programmaticBuilder.ConfigureModularAppHosts(options =>
            options.RepositoryCommandTimeout = TimeSpan.FromSeconds(30));
        programmaticBuilder.ExportModule("cache", module =>
            module.AddContainer("redis", "redis"));
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory)
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });
    }

    private static IDistributedApplicationModule ExportProject(
        IDistributedApplicationBuilder builder,
        string projectPath)
    {
        return builder.ExportModule("orders", definition =>
            definition.AddProject("orders-api", projectPath)
                .ExportAsContainerWithCommand(
                    new ModuleImageCommandOptions(
                        $"module-defaults-{Guid.NewGuid():N}",
                        "dotnet",
                        "publish")));
    }

    private static string CreateProject(string directory)
    {
        var projectPath = Path.Combine(directory, "Orders.Api.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return projectPath;
    }

}
