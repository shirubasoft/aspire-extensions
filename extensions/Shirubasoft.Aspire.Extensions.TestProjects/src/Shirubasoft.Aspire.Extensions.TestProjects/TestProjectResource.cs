namespace Aspire.Hosting.ApplicationModel;

/// <summary>A test project whose run command consumes the AppHost's resource references.</summary>
public sealed class TestProjectResource : Resource, IResourceWithEnvironment, IResourceWithArgs, IResourceWithWaitSupport
{
    /// <summary>Creates a test project resource.</summary>
    /// <param name="name">The resource name.</param>
    /// <param name="projectPath">The absolute path to the test project.</param>
    public TestProjectResource(string name, string projectPath) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ProjectPath = Path.GetFullPath(projectPath);
    }

    /// <summary>Gets the absolute test project path.</summary>
    public string ProjectPath { get; }
}
