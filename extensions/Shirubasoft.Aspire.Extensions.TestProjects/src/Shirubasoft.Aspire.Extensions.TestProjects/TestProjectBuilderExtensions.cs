using Aspire.Hosting.ApplicationModel;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>Registers test projects with run and results commands.</summary>
public static class TestProjectBuilderExtensions
{
    /// <summary>Adds a test project that receives references from this AppHost.</summary>
    /// <typeparam name="TProject">The generated project metadata type for a test project.</typeparam>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="options">Optional runner and report settings.</param>
    /// <returns>The test resource builder.</returns>
    [SuppressMessage("ApiDesign", "RS0026", Justification = "Generic metadata and path overloads have distinct required parameters.")]
    public static IResourceBuilder<TestProjectResource> AddTestProject<TProject>(
        this IDistributedApplicationBuilder builder, string name, TestProjectOptions? options = null)
        where TProject : IProjectMetadata, new() =>
        builder.AddTestProject(name, new TProject().ProjectPath, options);

    /// <summary>Adds a test project by path without creating an MSBuild project reference.</summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="projectPath">The project path, relative to the AppHost directory or absolute.</param>
    /// <param name="options">Optional runner and report settings.</param>
    /// <returns>The test resource builder.</returns>
    [SuppressMessage("ApiDesign", "RS0026", Justification = "Generic metadata and path overloads have distinct required parameters.")]
    public static IResourceBuilder<TestProjectResource> AddTestProject(
        this IDistributedApplicationBuilder builder, string name, string projectPath, TestProjectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        options ??= new();
        options.Validate();
        var resource = new TestProjectResource(name, Path.GetFullPath(projectPath, builder.AppHostDirectory));
        var result = builder.AddResource(resource).ExcludeFromManifest().WithInitialState(new()
        {
            ResourceType = "Test project",
            State = new("Not run", KnownResourceStateStyles.Info),
            Properties = [new("Project", resource.ProjectPath), new("Runner", options.Runner.ToString())],
        });
        TestProjectCommands.Register(result, options);
        return result;
    }
}
