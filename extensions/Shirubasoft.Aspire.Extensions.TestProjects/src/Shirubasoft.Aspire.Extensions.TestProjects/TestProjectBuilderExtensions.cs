using Aspire.Hosting.ApplicationModel;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>Registers explicitly started Microsoft.Testing.Platform projects with Markdown results.</summary>
public static class TestProjectBuilderExtensions
{
    /// <summary>Adds an executable Microsoft.Testing.Platform project that starts only on request.</summary>
    /// <typeparam name="TProject">The generated project metadata type for a test project.</typeparam>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="options">Optional report settings.</param>
    /// <returns>The test resource builder.</returns>
    [SuppressMessage("ApiDesign", "RS0026", Justification = "Generic metadata and path overloads have distinct required parameters.")]
    public static IResourceBuilder<ProjectResource> AddTestProject<TProject>(
        this IDistributedApplicationBuilder builder, string name, TestProjectOptions? options = null)
        where TProject : IProjectMetadata, new() =>
        builder.AddTestProject(name, new TProject().ProjectPath, options);

    /// <summary>Adds an explicitly started Microsoft.Testing.Platform project by path.</summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="projectPath">The project path, relative to the AppHost directory or absolute.</param>
    /// <param name="options">Optional report settings.</param>
    /// <returns>The test resource builder.</returns>
    [SuppressMessage("ApiDesign", "RS0026", Justification = "Generic metadata and path overloads have distinct required parameters.")]
    public static IResourceBuilder<ProjectResource> AddTestProject(
        this IDistributedApplicationBuilder builder, string name, string projectPath, TestProjectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        options ??= new();
        options.Validate();
        var result = builder.AddProject(name, projectPath).WithExplicitStart().ExcludeFromManifest();
        TestProjectCommands.Register(result, options);
        return result;
    }
}
