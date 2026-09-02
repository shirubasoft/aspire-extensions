using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

/// <summary>
/// A reusable group of Aspire resources that can be added locally or imported from its Git repository.
/// </summary>
public interface IDistributedApplicationModule
{
    /// <summary>Gets the module name.</summary>
    string Name { get; }

    /// <summary>Gets the module contract version.</summary>
    string Version { get; }

    /// <summary>Gets the NuGet package ID that publishes the module contract, when the module declares one.</summary>
    string? PackageId { get; }

    /// <summary>
    /// Gets every resource exported by the module in declaration order, including container resources created by
    /// <see cref="IDistributedApplicationModuleBuilder.AddResource{TResource}(string, Func{IDistributedApplicationModuleResourceContext, IResourceBuilder{TResource}}, ModuleImageCommandOptions)"/>.
    /// </summary>
    IReadOnlyList<IDistributedApplicationModuleResource> Resources { get; }

    /// <summary>Gets the projects exported by the module.</summary>
    IReadOnlyList<IDistributedApplicationModuleProject> Projects { get; }

    /// <summary>
    /// Gets containers declared with <see cref="IDistributedApplicationModuleBuilder.AddContainer"/>. Factory-created
    /// container resources are exposed through <see cref="Resources"/> with a <see cref="IDistributedApplicationModuleResource.ResourceType"/>
    /// assignable to <see cref="ContainerResource"/>.
    /// </summary>
    IReadOnlyList<IDistributedApplicationModuleContainer> Containers { get; }

    /// <summary>Gets a materialized module resource by name.</summary>
    IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource;
}

/// <summary>Controls resource names when a module is imported into an AppHost.</summary>
public sealed class ModuleImportOptions
{
    /// <summary>Gets or sets a prefix prepended to every imported Aspire resource name.</summary>
    public string? ResourcePrefix { get; set; }

    /// <summary>Gets resource-name overrides keyed by the name declared in the module contract.</summary>
    public IDictionary<string, string> ResourceAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Builds an <see cref="IDistributedApplicationModule"/> definition.</summary>
public interface IDistributedApplicationModuleBuilder
{
    /// <summary>Gets the receiving AppHost configuration.</summary>
    IConfiguration Configuration { get; }

    /// <summary>Gets the conventional configuration section for the module being defined.</summary>
    IConfigurationSection ConfigurationSection { get; }

    /// <summary>Gets options bound from <see cref="ConfigurationSection"/>.</summary>
    IOptions<TOptions> GetOptions<TOptions>()
        where TOptions : class, new();

    /// <summary>Gets a previously defined module with the required contract version.</summary>
    IDistributedApplicationModule GetRequiredModule(string name, string version);

    /// <summary>Adds a previously defined module from its local source checkout when this module is materialized.</summary>
    IDistributedApplicationModule AddModule(IDistributedApplicationModule dependency);

    /// <summary>Defines and adds another module from its local source checkout when this module is materialized.</summary>
    IDistributedApplicationModule AddModule(
        string name,
        string version,
        string? packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder);

    /// <summary>Imports a previously defined module with default resource names when this module is materialized.</summary>
    IDistributedApplicationModule ImportModule(IDistributedApplicationModule dependency);

    /// <summary>Imports a previously defined module with resource naming options when this module is materialized.</summary>
    IDistributedApplicationModule ImportModule(
        IDistributedApplicationModule dependency,
        ModuleImportOptions importOptions);

    /// <summary>Defines and imports another module with default resource names when this module is materialized.</summary>
    IDistributedApplicationModule ImportModule(
        string name,
        string version,
        string? packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder);

    /// <summary>Defines and imports another module with resource naming options when this module is materialized.</summary>
    IDistributedApplicationModule ImportModule(
        string name,
        string version,
        string? packageId,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder,
        ModuleImportOptions importOptions);

    /// <summary>
    /// Adds any Aspire resource type through a factory that runs when the module is materialized.
    /// </summary>
    IDistributedApplicationModuleBuilder AddResource<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory)
        where TResource : IResource;

    /// <summary>Adds a factory-created container resource with an advanced image publish command.</summary>
    IDistributedApplicationModuleBuilder AddResource<TResource>(
        string name,
        Func<IDistributedApplicationModuleResourceContext, IResourceBuilder<TResource>> resourceFactory,
        ModuleImageCommandOptions imagePublishOptions)
        where TResource : ContainerResource;

    /// <summary>Adds a generated Aspire project reference to the module.</summary>
    IDistributedApplicationModuleProjectBuilder AddProject<TProject>(string name)
        where TProject : IProjectMetadata, new();

    /// <summary>Adds a project path to the module.</summary>
    IDistributedApplicationModuleProjectBuilder AddProject(string name, string projectPath);

    /// <summary>Adds a project path resolved from the module repository when it is materialized.</summary>
    IDistributedApplicationModuleProjectBuilder AddProject(
        string name,
        string projectPath,
        ModuleProjectPathBase pathBase);

    /// <summary>Adds an existing container image to the module.</summary>
    IDistributedApplicationModuleContainerBuilder AddContainer(
        string name,
        string image,
        string tag = "latest");

    /// <summary>
    /// Overrides the Git repository used by <c>ImportModule</c>. When omitted, the origin remote is inferred
    /// from the projects' common Git worktree.
    /// </summary>
    IDistributedApplicationModuleBuilder WithRepository(string repository);

    /// <summary>Overrides the Git repository and pins its imported branch, tag, or commit.</summary>
    IDistributedApplicationModuleBuilder WithRepository(string repository, string revision);

    /// <summary>Overrides the Git repository and its checkout declaration.</summary>
    IDistributedApplicationModuleBuilder WithRepository(
        string repository,
        ModuleRepositoryOptions options);

    /// <summary>
    /// Marks generic resource factories as dependent on repository content when the module does not declare a project.
    /// </summary>
    IDistributedApplicationModuleBuilder RequiresRepository();
}

/// <summary>Controls the repository declared by a distributed application module.</summary>
public sealed class ModuleRepositoryOptions
{
    /// <summary>Gets or sets the branch, tag, or commit checked out for this module.</summary>
    public string? Revision { get; set; }

    /// <summary>
    /// Gets or sets the direct sibling directory name used for an unpinned remote repository checkout.
    /// </summary>
    public string? CheckoutDirectoryName { get; set; }
}

/// <summary>Describes a resource exported by a distributed application module.</summary>
public interface IDistributedApplicationModuleResource
{
    /// <summary>Gets the Aspire resource name.</summary>
    string Name { get; }

    /// <summary>Gets the resource type returned by the materialization factory.</summary>
    Type ResourceType { get; }
}

/// <summary>Provides state to a resource callback when its module is materialized.</summary>
public interface IDistributedApplicationModuleResourceContext
{
    /// <summary>Gets the AppHost builder receiving the resource.</summary>
    IDistributedApplicationBuilder ApplicationBuilder { get; }

    /// <summary>Gets the effective name, including any import prefix or alias, that the factory must assign.</summary>
    string ResourceName { get; }

    /// <summary>Gets the local source or managed repository path for this module.</summary>
    string RepositoryPath { get; }

    /// <summary>Gets whether the module is being imported rather than added from local source.</summary>
    bool Imported { get; }

    /// <summary>Gets the resolved image for a container resource, when one is available.</summary>
    ModuleResourceImage? Image { get; }

    /// <summary>Gets a previously materialized resource exported by the same module.</summary>
    IResourceBuilder<TResource> GetResource<TResource>(string name)
        where TResource : IResource;
}

/// <summary>A container contained in a distributed application module.</summary>
public interface IDistributedApplicationModuleContainer : IDistributedApplicationModuleResource
{
    /// <summary>Gets the container image name.</summary>
    string Image { get; }

    /// <summary>Gets the container image tag.</summary>
    string Tag { get; }
}

/// <summary>Configures one existing container in a module.</summary>
public interface IDistributedApplicationModuleContainerBuilder
{
    /// <summary>Gets the container being configured.</summary>
    IDistributedApplicationModuleContainer Container { get; }

    /// <summary>
    /// Applies Aspire container-resource configuration when the module is materialized. The context can resolve
    /// resources declared earlier in the same module.
    /// </summary>
    IDistributedApplicationModuleContainerBuilder Configure(
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>> configureContainer);

    /// <summary>Publishes the container image with an advanced command before the container starts.</summary>
    IDistributedApplicationModuleContainerBuilder WithImagePublishCommand(
        ModuleImageCommandOptions options);
}

/// <summary>A project contained in a distributed application module.</summary>
public interface IDistributedApplicationModuleProject : IDistributedApplicationModuleResource
{
    /// <summary>Gets the source project path used when the module is added locally.</summary>
    string ProjectPath { get; }

    /// <summary>Gets whether this project is exported as a container.</summary>
    bool IsExportedAsContainer { get; }
}

/// <summary>Configures one project in a module.</summary>
public interface IDistributedApplicationModuleProjectBuilder
{
    /// <summary>Gets the project being configured.</summary>
    IDistributedApplicationModuleProject Project { get; }

    /// <summary>Applies Aspire project-resource configuration when the project runs directly.</summary>
    IDistributedApplicationModuleProjectBuilder ConfigureProject(
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ProjectResource>> configureProject);

    /// <summary>Exports the project through Aspire's native project container publisher.</summary>
    IDistributedApplicationModuleProjectBuilder ExportAsContainer(
        string imageName,
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? configureContainer = null);

    /// <summary>Exports the project as a container using an advanced arbitrary publish command.</summary>
    IDistributedApplicationModuleProjectBuilder ExportAsContainerWithCommand(
        ModuleImageCommandOptions options,
        Action<IDistributedApplicationModuleResourceContext, IResourceBuilder<ContainerResource>>? configureContainer = null);
}

/// <summary>Controls the advanced command used to build a module container image.</summary>
public sealed class ModuleImageCommandOptions(
    string imageName,
    string publishCommand,
    params string[] publishArguments)
{
    /// <summary>
    /// Placeholder for the Docker or Podman executable selected when the image publish command runs.
    /// </summary>
    public const string ContainerRuntimePlaceholder = "{container-runtime}";

    /// <summary>Placeholder for the effective image name in a publish argument.</summary>
    public const string ImageNamePlaceholder = "{image-name}";

    /// <summary>Placeholder for the effective image tag in a publish argument.</summary>
    public const string ImageTagPlaceholder = "{image-tag}";

    /// <summary>Placeholder for the complete effective image reference in a publish argument.</summary>
    public const string ImageReferencePlaceholder = "{image}";

    /// <summary>Placeholder for the explicit image registry, or an empty string when none is configured.</summary>
    public const string ImageRegistryPlaceholder = "{image-registry}";

    /// <summary>Placeholder for the effective image repository, including the registry when configured.</summary>
    public const string ImageRepositoryPlaceholder = "{image-repository}";

    /// <summary>Gets the image name that the publish command must create.</summary>
    public string ImageName { get; } = imageName;

    /// <summary>
    /// Gets or sets the explicit registry host. When set, <see cref="ImageName"/> is the registry-relative repository.
    /// </summary>
    public string? ImageRegistry { get; set; }

    /// <summary>
    /// Gets or sets the image reference produced by the advanced publish command. When it differs from the effective
    /// image reference, image preparation tags it after the command succeeds.
    /// </summary>
    public string? ProducedImageReference { get; set; }

    /// <summary>Gets or sets whether a missing clean image is pulled before the publish command is run.</summary>
    public bool PullBeforeBuild { get; set; }

    /// <summary>
    /// Gets the executable invoked during advanced image preparation. Use <see cref="ContainerRuntimePlaceholder"/> to defer
    /// Docker or Podman selection until the image publish command runs.
    /// </summary>
    public string PublishCommand { get; } = publishCommand;

    /// <summary>
    /// Gets the arguments supplied to <see cref="PublishCommand"/>. Image placeholders are resolved before invocation.
    /// </summary>
    public IReadOnlyList<string> PublishArguments { get; } = publishArguments;

    /// <summary>
    /// Gets or sets the clean image tag. When omitted, the sanitized repository branch and 12-character commit are used.
    /// The effective tag has <c>-dirty</c> appended for a dirty repository.
    /// </summary>
    public string? ImageTag { get; set; }

    /// <summary>
    /// Gets or sets the publish working directory relative to the effective build repository root. When this is not
    /// set, exported projects use their project directory for the module repository and the repository root for a
    /// separate build repository. Container publishers always use the repository root.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the Git repository that contains the image build inputs. When omitted, the module repository is used.
    /// </summary>
    public string? BuildRepository { get; set; }

    /// <summary>
    /// Gets or sets the branch, tag, or commit checked out from <see cref="BuildRepository"/> before publishing.
    /// </summary>
    public string? BuildRepositoryRevision { get; set; }

    /// <summary>
    /// Gets or sets the direct sibling directory name used for an unpinned remote build repository checkout.
    /// </summary>
    public string? CheckoutDirectoryName { get; set; }
}

/// <summary>Describes the effective image assigned to a factory-created container resource.</summary>
public sealed class ModuleResourceImage
{
    internal ModuleResourceImage(string? registry, string name, string tag, string? digest = null)
    {
        Registry = registry;
        Name = name;
        Tag = tag;
        Digest = digest;
        Repository = string.IsNullOrWhiteSpace(registry) ? name : $"{registry}/{name}";
        Reference = digest is null ? $"{Repository}:{tag}" : $"{Repository}@{digest}";
    }

    /// <summary>Gets the explicit registry host, or <see langword="null"/> for an unqualified image.</summary>
    public string? Registry { get; }

    /// <summary>Gets the image repository path without the explicit registry.</summary>
    public string Name { get; }

    /// <summary>Gets the effective image tag, including the dirty suffix when applicable.</summary>
    public string Tag { get; }

    /// <summary>Gets the configured immutable image digest, including its algorithm prefix, when present.</summary>
    public string? Digest { get; }

    /// <summary>Gets the image repository, including the registry when configured.</summary>
    public string Repository { get; }

    /// <summary>Gets the complete effective image reference, preferring <see cref="Digest"/> when present.</summary>
    public string Reference { get; }
}

/// <summary>Controls the directory used to resolve a module project path.</summary>
public enum ModuleProjectPathBase
{
    /// <summary>Resolves the path from the AppHost that defines the module.</summary>
    AppHost,

    /// <summary>Resolves the path from the local or imported module repository during materialization.</summary>
    Repository
}
