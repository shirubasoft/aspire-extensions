namespace Aspire.Hosting.Testing;

/// <summary>Controls an Aspire-owned Docker Compose test deployment.</summary>
public sealed class DockerComposeDeploymentOptions
{
    /// <summary>
    /// Gets or sets the Aspire deployment environment name. The default is unique per options instance so concurrent
    /// test deployments do not share Compose state.
    /// </summary>
    public string EnvironmentName { get; set; } =
        DockerComposeDeploymentTestingBuilder.CreateDefaultDeploymentEnvironmentName();

    /// <summary>
    /// Gets or sets the deployment output directory. A temporary directory is used and removed on disposal when omitted.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the path to the Aspire CLI executable. The default prefers a restored local Aspire tool manifest
    /// found above the AppHost and otherwise invokes <c>aspire</c> from <c>PATH</c>.
    /// </summary>
    public string AspireCliPath { get; set; } = "aspire";

    /// <summary>Gets or sets how many times a deployment is retried after a detected host-port bind conflict.</summary>
    public int PortConflictRetryCount { get; set; } = 1;

    /// <summary>Gets or sets the maximum time allowed for <c>aspire deploy</c>.</summary>
    public TimeSpan DeploymentTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the maximum time allowed for <c>aspire destroy</c>.</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
