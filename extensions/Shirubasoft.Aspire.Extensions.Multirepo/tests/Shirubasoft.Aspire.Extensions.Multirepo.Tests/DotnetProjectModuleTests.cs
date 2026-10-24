using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class DotnetProjectModuleTests
{
    private const string ProjectOptionsSection =
        $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Projects:orders-api";

    [Theory]
    [InlineData("Orders.Api.csproj", false)]
    [InlineData("orders.cs", false)]
    [InlineData("Orders.Api.csproj", true)]
    public void Local_dotnet_project_runs_through_AddDotnetProject_and_applies_its_callback(
        string fileName,
        bool exportsWithCommand)
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path, fileName);
        var builder = CreateBuilder(source.Path);
        builder.Configuration[$"{ProjectOptionsSection}:ExcludeLaunchProfile"] = "true";
        IResourceBuilder<DotnetProjectResource>? configured = null;
        IDistributedApplicationModuleProject? declared = null;
        var module = builder.ExportModule("orders", definition =>
        {
            var project = definition.AddDotnetProject("orders-api", projectPath)
                .ConfigureProject((_, project) => configured = project.WithEnvironment("MODE", "project"));
            declared = exportsWithCommand
                ? project.ExportAsContainerWithCommand(
                    new ModuleImageCommandOptions("example/orders-api", "dotnet", "publish")).Project
                : project.ExportAsContainer("example/orders-api").Project;
        });

        builder.AddModule(module);

        var project = Assert.Single(builder.Resources.OfType<DotnetProjectResource>());
        Assert.Empty(builder.Resources.OfType<ProjectResource>());
        Assert.Same(project, configured?.Resource);
        Assert.Same(project, module.GetResource<IResourceWithEndpoints>("orders-api").Resource);
        Assert.Equal(projectPath, declared?.ProjectPath);
        Assert.Single(project.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>());
        Assert.Single(project.Annotations.OfType<ExcludeLaunchProfileAnnotation>());
        Assert.Equal(
            !exportsWithCommand,
            project.Annotations.OfType<ModuleNativeImagePublisherAnnotation>().Any());
    }

    [Fact]
    public void Configured_launch_profile_and_Kestrel_options_apply_to_the_dotnet_project()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        Directory.CreateDirectory(Path.Combine(source.Path, "Properties"));
        File.WriteAllText(
            Path.Combine(source.Path, "Properties", "launchSettings.json"),
            """{ "profiles": { "worker": { "commandName": "Project", "applicationUrl": "http://localhost:5060" } } }""");
        File.WriteAllText(
            Path.Combine(source.Path, "appsettings.json"),
            """{ "Kestrel": { "Endpoints": { "api": { "Url": "http://localhost:5050" } } } }""");
        var builder = CreateBuilder(source.Path);
        builder.Configuration[$"{ProjectOptionsSection}:LaunchProfileName"] = "worker";
        builder.Configuration[$"{ProjectOptionsSection}:ExcludeKestrelEndpoints"] = "true";
        var module = builder.ExportModule("orders", definition =>
            definition.AddDotnetProject("orders-api", projectPath).ExportAsContainer("example/orders-api"));

        builder.AddModule(module);

        var project = Assert.Single(builder.Resources.OfType<DotnetProjectResource>());
        Assert.Equal("worker", Assert.Single(project.Annotations.OfType<LaunchProfileAnnotation>()).LaunchProfileName);
        Assert.Equal(5060, Assert.Single(project.Annotations.OfType<EndpointAnnotation>()).Port);
    }

    [Fact]
    public void Container_mode_runs_the_dotnet_project_image_instead()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path).UseModuleContainers();
        var module = builder.ExportModule("orders", definition =>
            definition.AddDotnetProject("orders-api", projectPath)
                .ConfigureProject((_, _) => throw new InvalidOperationException("Project callback ran in container mode."))
                .ExportAsContainer("example/orders-api"));

        builder.AddModule(module);

        Assert.Empty(builder.Resources.OfType<DotnetProjectResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("example/orders-api", Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>()).Image);
    }

    [Fact]
    public void Auto_mode_runs_an_imported_dotnet_project_as_a_container()
    {
        using var source = TemporaryDirectory.Create();
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        CreateProject(source.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.WithRepository(source.Path);
            definition.AddDotnetProject("orders-api", "Orders.Api.csproj", ModuleProjectPathBase.Repository)
                .ExportAsContainer("example/orders-api");
        });

        builder.ImportModule(module.Name);

        Assert.Empty(builder.Resources.OfType<DotnetProjectResource>());
        Assert.Single(builder.Resources.OfType<ContainerResource>());
    }

    [Fact]
    public void Publish_mode_converts_the_dotnet_project_to_a_native_project_image()
    {
        using var source = TemporaryDirectory.Create();
        var projectPath = CreateProject(source.Path);
        var builder = CreateBuilder(source.Path, ["--publisher", "manifest"]);
        var module = builder.ExportModule("orders", definition =>
            definition.AddDotnetProject("orders-api", projectPath)
                .ExportAsContainer("example/orders-api"));

        builder.AddModule(module);

        Assert.Empty(builder.Resources.OfType<DotnetProjectResource>());
        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var dockerfile = Assert.Single(container.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Equal(source.Path, dockerfile.ContextPath);
        Assert.Single(container.Annotations.OfType<ModuleNativeImagePublisherAnnotation>());
        var image = container.Annotations.OfType<ContainerImageAnnotation>().Last();
        Assert.Equal("example/orders-api", image.Image);
    }

    [Theory]
    [InlineData("Orders.Api")]
    [InlineData("Orders.Api.fsproj")]
    public void Paths_other_than_csproj_and_cs_files_are_rejected(string path)
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.ExportModule("orders", definition => definition.AddDotnetProject("orders-api", path)));

        Assert.Equal("path", exception.ParamName);
    }

    [Theory]
    [InlineData(false, typeof(ProjectResource))]
    [InlineData(true, typeof(DotnetProjectResource))]
    public void Missing_repository_checkout_defers_the_dotnet_project_for_initialization(
        bool declaredByFactory,
        Type expectedResourceType)
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path, ["--publisher", "manifest"]);
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            if (declaredByFactory)
            {
                definition.RequiresRepository();
                definition.AddResource<DotnetProjectResource>("orders-api", context =>
                    context.ApplicationBuilder.AddDotnetProject(
                        context.ResourceName,
                        Path.Combine(context.RepositoryPath, "Orders.Api", "Orders.Api.csproj")));
                return;
            }

            definition.AddDotnetProject("orders-api", "Orders.Api/Orders.Api.csproj", ModuleProjectPathBase.Repository)
                .ExportAsContainer("orders-api");
        });

        var imported = builder.ImportModule("orders");

        var resource = imported.GetResource<IResourceWithEndpoints>("orders-api").Resource;
        Assert.Equal("orders-api", resource.Name);
        Assert.IsType(expectedResourceType, resource);
    }

    public enum FactoryFailure
    {
        BeforeRegistration,
        AfterRegisteringCheckoutProject,
        MissingProjectOutsideCheckout
    }

    [Theory]
    [InlineData(false, false, FactoryFailure.AfterRegisteringCheckoutProject)]
    [InlineData(false, true, FactoryFailure.AfterRegisteringCheckoutProject)]
    [InlineData(true, false, FactoryFailure.AfterRegisteringCheckoutProject)]
    [InlineData(true, true, FactoryFailure.AfterRegisteringCheckoutProject)]
    [InlineData(true, true, FactoryFailure.BeforeRegistration)]
    [InlineData(true, true, FactoryFailure.MissingProjectOutsideCheckout)]
    public void Missing_repository_checkout_keeps_unrelated_project_factory_failures(
        bool dotnet,
        bool publish,
        FactoryFailure failure)
    {
        using var appHost = CreateGitAppHost();
        var builder = CreateBuilder(appHost.Path, publish ? ["--publisher", "manifest"] : null);
        Exception? thrown = null;
        IResourceBuilder<TResource> Fail<TResource>(IDistributedApplicationModuleResourceContext context)
            where TResource : IResource
        {
            thrown = Record.Exception(() => AddFactoryProject(context, dotnet, failure, appHost.Path)) ??
                new DistributedApplicationException("Intentional factory failure.");
            throw thrown;
        }

        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.RequiresRepository();
            _ = dotnet
                ? definition.AddResource<DotnetProjectResource>("orders-api", Fail<DotnetProjectResource>)
                : definition.AddResource<ProjectResource>("orders-api", Fail<ProjectResource>);
        });

        var exception = Record.Exception(() => builder.ImportModule("orders"));

        Assert.NotNull(thrown);
        Assert.Same(thrown, exception);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory, string[]? args = null) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args ?? [],
            DisableDashboard = true,
            ProjectDirectory = projectDirectory
        });

    private static TemporaryDirectory CreateGitAppHost()
    {
        var appHost = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(appHost.Path, ".git"));
        return appHost;
    }

    // A checkout project excludes its launch profile, so Aspire registers it without reading the missing file.
    private static void AddFactoryProject(
        IDistributedApplicationModuleResourceContext context,
        bool dotnet,
        FactoryFailure failure,
        string appHostPath)
    {
        if (failure == FactoryFailure.BeforeRegistration)
        {
            return;
        }

        var inCheckout = failure == FactoryFailure.AfterRegisteringCheckoutProject;
        var projectPath = Path.Combine(inCheckout ? context.RepositoryPath : appHostPath, "Orders.Api.csproj");
        void Configure(ProjectResourceOptions options) => options.ExcludeLaunchProfile = inCheckout;
        if (dotnet)
        {
            context.ApplicationBuilder.AddDotnetProject(context.ResourceName, projectPath, Configure);
        }
        else
        {
            context.ApplicationBuilder.AddProject(context.ResourceName, projectPath, Configure);
        }
    }

    private static string CreateProject(string directory, string fileName = "Orders.Api.csproj")
    {
        var projectPath = Path.Combine(directory, fileName);
        File.WriteAllText(
            projectPath,
            fileName.EndsWith(".cs", StringComparison.Ordinal)
                ? "System.Console.WriteLine(\"orders\");"
                : "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return projectPath;
    }
}
