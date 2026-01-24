namespace Aspire.Hosting.SharedResources;

/// <summary>
/// Options for configuring shared resource metadata.
/// </summary>
/// <remarks>
/// Used with the <see cref="SharedResourceExtensions.WithSharedResourceMetadata"/>
/// extension method to provide optional configuration.
/// </remarks>
public class SharedResourceOptions
{
    /// <summary>
    /// Default branch name (e.g., "main", "master").
    /// </summary>
    /// <value>Defaults to "main".</value>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Path to the project file relative to repository root.
    /// </summary>
    /// <remarks>
    /// Use forward slashes for cross-platform compatibility.
    /// </remarks>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// Container image name (without tag).
    /// </summary>
    /// <remarks>
    /// If not set, the service name from the extension method is used.
    /// </remarks>
    public string? ImageName { get; set; }
}
