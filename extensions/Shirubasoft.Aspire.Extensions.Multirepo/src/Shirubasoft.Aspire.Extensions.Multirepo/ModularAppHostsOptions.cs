using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>Controls how exported modules are materialized by a receiving AppHost.</summary>
/// <remarks>
/// Values are loaded from <c>Aspire:ModularAppHosts</c>. Configure these options before calling
/// <c>AddModule</c> or <c>ImportModule</c> because they shape the Aspire application model.
/// </remarks>
public sealed class ModularAppHostsOptions
{
    /// <summary>The configuration section bound to these options.</summary>
    public const string ConfigurationSectionName = "Aspire:ModularAppHosts";

    /// <summary>
    /// Gets or sets the GitHub CLI executable used as the process-scoped credential provider for authenticated
    /// GitHub HTTPS operations.
    /// </summary>
    public string GitHubCliPath { get; set; } = "gh";

    /// <summary>Gets or sets the Git executable used to synchronize managed repositories.</summary>
    public string GitExecutablePath { get; set; } = "git";

    /// <summary>Gets or sets the maximum duration of one repository CLI command.</summary>
    public TimeSpan RepositoryCommandTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets the maximum duration of one module image build command.</summary>
    public TimeSpan ImageBuildTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets the maximum duration of one image pull, push, or tag operation.</summary>
    public TimeSpan ImageTransferTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets whether clean unpinned repositories are fast-forwarded by <c>aspire do initialize</c>.</summary>
    public bool UpdateRepositoriesOnInitialize { get; set; } = true;

    /// <summary>
    /// Gets or sets whether image preparation may fetch and fast-forward clean unpinned build repositories during run.
    /// </summary>
    public bool RefreshBuildRepositoriesOnRun { get; set; }

    /// <summary>Gets or sets how exported projects run in Aspire run mode.</summary>
    public ModuleProjectMode ProjectMode { get; set; } = ModuleProjectMode.Auto;

    /// <summary>Gets module-specific overrides keyed by module name.</summary>
    public IDictionary<string, DistributedApplicationModuleOptions> Modules { get; } =
        new Dictionary<string, DistributedApplicationModuleOptions>(StringComparer.OrdinalIgnoreCase);

    internal static ModularAppHostsOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new ModularAppHostsOptions();
        configuration.GetSection(ConfigurationSectionName).Bind(options);
        return options;
    }

    internal DistributedApplicationModuleOptions? FindModule(string name) =>
        Modules.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

internal sealed class ModularAppHostsOptionsModel
{
    private readonly List<Action<ModularAppHostsOptions>> _configurations = [];
    private bool _frozen;

    public ModularAppHostsOptions ConfigureAndCreatePreview(
        Action<ModularAppHostsOptions> configure,
        IConfiguration configuration,
        Action<ModularAppHostsOptions> validate)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(validate);
        if (_frozen)
        {
            throw new InvalidOperationException(
                "Configure modular AppHost options before defining or materializing modules.");
        }

        _configurations.Add(configure);
        try
        {
            var preview = CreateSnapshot(configuration);
            validate(preview);
            return preview;
        }
        catch
        {
            _configurations.RemoveAt(_configurations.Count - 1);
            throw;
        }
    }

    public void Apply(ModularAppHostsOptions options)
    {
        foreach (var configure in _configurations)
        {
            configure(options);
        }
    }

    public ModularAppHostsOptions CreateSnapshot(IConfiguration configuration)
    {
        var options = ModularAppHostsOptions.FromConfiguration(configuration);
        Apply(options);
        PostConfigure(options);
        return options;
    }

    public void Freeze() => _frozen = true;

    public static void PostConfigure(ModularAppHostsOptions options)
    {
        options.GitExecutablePath = string.IsNullOrWhiteSpace(options.GitExecutablePath)
            ? "git"
            : options.GitExecutablePath.Trim();
        options.GitHubCliPath = string.IsNullOrWhiteSpace(options.GitHubCliPath)
            ? "gh"
            : options.GitHubCliPath.Trim();
    }
}

/// <summary>Overrides materialization behavior for one distributed application module.</summary>
public sealed class DistributedApplicationModuleOptions
{
    /// <summary>
    /// Gets or sets the Git repository used when the module is imported. Configuration values are
    /// represented by an Aspire parameter; values set in code are used directly.
    /// </summary>
    public string? Repository { get; set; }

    /// <summary>Gets or sets the branch, tag, or commit checked out for this module.</summary>
    public string? RepositoryRevision { get; set; }

    /// <summary>
    /// Gets or sets the direct sibling directory name used for this unpinned remote repository checkout.
    /// </summary>
    public string? CheckoutDirectoryName { get; set; }

    /// <summary>Gets or sets whether this repository is fast-forwarded by <c>aspire do initialize</c>.</summary>
    public bool? UpdateRepositoryOnInitialize { get; set; }

    /// <summary>Gets or sets how this module's exported projects run in Aspire run mode.</summary>
    public ModuleProjectMode? ProjectMode { get; set; }

    /// <summary>Gets project-specific overrides keyed by resource name.</summary>
    public IDictionary<string, DistributedApplicationModuleProjectOptions> Projects { get; } =
        new Dictionary<string, DistributedApplicationModuleProjectOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets container-specific overrides keyed by resource name.</summary>
    public IDictionary<string, DistributedApplicationModuleContainerOptions> Containers { get; } =
        new Dictionary<string, DistributedApplicationModuleContainerOptions>(StringComparer.OrdinalIgnoreCase);

    internal DistributedApplicationModuleProjectOptions? FindProject(string name) =>
        Projects.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    internal DistributedApplicationModuleContainerOptions? FindContainer(string name) =>
        Containers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Overrides image identity and publishing for one module resource.</summary>
public abstract class DistributedApplicationModuleImageOptions
{
    /// <summary>Gets or sets the effective container image name.</summary>
    public string? ImageName { get; set; }

    /// <summary>Gets or sets the explicit container registry host.</summary>
    public string? ImageRegistry { get; set; }

    /// <summary>Gets or sets the image reference produced by an advanced publish command before final retagging.</summary>
    public string? ProducedImageReference { get; set; }

    /// <summary>Gets or sets whether a missing clean image is pulled before its publish command runs.</summary>
    public bool? PullBeforeBuild { get; set; }

    /// <summary>Gets or sets the effective container image tag.</summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets the immutable OCI digest in <c>sha256:&lt;64 lowercase hex&gt;</c> form.</summary>
    public string? ImageSHA256 { get; set; }

    /// <summary>Gets or sets whether the declared image publish command may run.</summary>
    public bool? PublishImage { get; set; }

    /// <summary>Gets or sets the executable used to publish the image.</summary>
    public string? PublishCommand { get; set; }

    /// <summary>Gets or sets the complete argument list used to publish the image.</summary>
    public IReadOnlyList<string>? PublishArguments { get; set; }

    /// <summary>Gets or sets the publish working directory relative to the effective build repository.</summary>
    public string? PublishWorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the Git repository containing this resource's image build inputs. This overrides the repository
    /// declared by <see cref="ModuleImageCommandOptions.BuildRepository"/>.
    /// </summary>
    public string? BuildRepository { get; set; }

    /// <summary>Gets or sets the branch, tag, or commit checked out from the build repository.</summary>
    public string? BuildRepositoryRevision { get; set; }

    /// <summary>
    /// Gets or sets the direct sibling directory name used for this resource's unpinned remote build repository.
    /// </summary>
    public string? CheckoutDirectoryName { get; set; }

    /// <summary>
    /// Gets or sets whether image preparation may fetch and fast-forward this clean unpinned build repository.
    /// </summary>
    public bool? RefreshBuildRepositoryOnRun { get; set; }

    /// <summary>Gets or sets the container runtime image pull policy.</summary>
    public ImagePullPolicy? ImagePullPolicy { get; set; }
}

/// <summary>Overrides materialization behavior for one exported project.</summary>
public sealed class DistributedApplicationModuleProjectOptions : DistributedApplicationModuleImageOptions
{
    /// <summary>Gets or sets how this project runs in Aspire run mode.</summary>
    public ModuleProjectMode? ProjectMode { get; set; }

    /// <summary>Gets or sets the launch profile used when the project runs directly.</summary>
    public string? LaunchProfileName { get; set; }

    /// <summary>Gets or sets whether launch profile discovery is disabled when the project runs directly.</summary>
    public bool? ExcludeLaunchProfile { get; set; }

    /// <summary>Gets or sets whether Kestrel endpoints are excluded when the project runs directly.</summary>
    public bool? ExcludeKestrelEndpoints { get; set; }
}

/// <summary>Overrides materialization behavior for one declared container.</summary>
public sealed class DistributedApplicationModuleContainerOptions : DistributedApplicationModuleImageOptions;

/// <summary>Controls how an exported project is represented in Aspire run mode.</summary>
public enum ModuleProjectMode
{
    /// <summary>Runs local modules as projects and imported modules as containers.</summary>
    Auto,

    /// <summary>Runs the project directly for local debugging.</summary>
    Project,

    /// <summary>Runs the project's exported container representation.</summary>
    Container
}
