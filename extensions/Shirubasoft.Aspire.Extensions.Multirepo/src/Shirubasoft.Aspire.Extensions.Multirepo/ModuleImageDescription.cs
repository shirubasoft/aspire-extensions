using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

/// <summary>Identifies the kind of module resource that publishes a container image.</summary>
public enum ModuleResourceKind
{
    /// <summary>A project exported as a container image.</summary>
    Project,

    /// <summary>A container resource with an image publisher.</summary>
    Container
}

/// <summary>Describes the effective module container images materialized by an AppHost.</summary>
public sealed class ModuleImageDescriptionDocument
{
    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Gets or sets the document schema version.</summary>
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets the materialized modules in deterministic module-name order.</summary>
    [JsonPropertyOrder(1)]
    public IList<ModuleImageModuleDescription> Modules { get; } = [];

    /// <summary>Gets the effective module images in deterministic resource-name order.</summary>
    [JsonPropertyOrder(2)]
    public IList<ModuleImageDescription> Images { get; } = [];

    /// <summary>Reads and validates an image description document.</summary>
    public static async Task<ModuleImageDescriptionDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var document = await JsonSerializer.DeserializeAsync<ModuleImageDescriptionDocument>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The module image description document is empty.");
            document.Validate();
            return document;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Writes the document as deterministic, indented JSON.</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await JsonSerializer.SerializeAsync(stream, this, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Validates the document schema and required image identities.</summary>
    public void Validate()
    {
        ValidateSchemaVersion();
        var modules = ValidateModules();
        ValidateImages(modules);
    }

    private void ValidateSchemaVersion()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported module image description schema version '{SchemaVersion}'. " +
                $"Expected '{CurrentSchemaVersion}'.");
        }
    }

    private HashSet<string> ValidateModules()
    {
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in Modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            module.Validate();
            if (!modules.Add(module.Name))
            {
                throw new InvalidDataException(
                    $"Module image description contains duplicate module '{module.Name}'.");
            }
        }

        return modules;
    }

    private void ValidateImages(HashSet<string> modules)
    {
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in Images)
        {
            ArgumentNullException.ThrowIfNull(image);
            image.Validate();
            ValidateKnownModule(modules, image);
            AddResource(resources, image);
        }
    }

    private static void ValidateKnownModule(HashSet<string> modules, ModuleImageDescription image)
    {
        if (!modules.Contains(image.Module))
        {
            throw new InvalidDataException(
                $"Module image '{image.EffectiveResource}' refers to unknown module '{image.Module}'.");
        }
    }

    private static void AddResource(HashSet<string> resources, ModuleImageDescription image)
    {
        if (!resources.Add(image.EffectiveResource))
        {
            throw new InvalidDataException(
                $"Module image description contains duplicate effective resource '{image.EffectiveResource}'.");
        }
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ModuleResourceKind>(JsonNamingPolicy.CamelCase, false) }
    };
}

/// <summary>Describes one module represented in an AppHost image inventory.</summary>
public sealed class ModuleImageModuleDescription
{
    /// <summary>Gets or sets the module name.</summary>
    [JsonPropertyOrder(0)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the NuGet package ID that publishes the module contract, when declared.</summary>
    [JsonPropertyOrder(1)]
    public string? ContractPackageId { get; set; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (ContractPackageId is not null && !IsValidPackageId(ContractPackageId))
        {
            throw new InvalidDataException(
                $"Module image contract package ID '{ContractPackageId}' is not a valid NuGet package ID.");
        }
    }

    internal static bool IsValidPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        return packageId.Length <= 100 && packageId.All(IsValidPackageIdCharacter);
    }

    private static bool IsValidPackageIdCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || ".-_".Contains(character, StringComparison.Ordinal);
}

/// <summary>Describes one effective module container image and its pipeline identities.</summary>
public sealed class ModuleImageDescription
{
    /// <summary>Gets or sets the module name.</summary>
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource name declared by the module.</summary>
    [JsonPropertyOrder(1)]
    public string Resource { get; set; } = string.Empty;

    /// <summary>Gets or sets the materialized resource name, including any import prefix or alias.</summary>
    [JsonPropertyOrder(2)]
    public string EffectiveResource { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of module resource that publishes the image.</summary>
    [JsonPropertyOrder(3)]
    public ModuleResourceKind ResourceKind { get; set; }

    /// <summary>Gets or sets the effective registry, if the run identity is qualified.</summary>
    [JsonPropertyOrder(4)]
    public string? Registry { get; set; }

    /// <summary>Gets or sets the repository path without registry, tag, or digest.</summary>
    [JsonPropertyOrder(5)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective tag when the image is not digest-pinned.</summary>
    [JsonPropertyOrder(6)]
    public string? Tag { get; set; }

    /// <summary>Gets or sets the canonical digest when the image is digest-pinned.</summary>
    [JsonPropertyOrder(7)]
    public string? Digest { get; set; }

    /// <summary>Gets or sets the complete effective run reference.</summary>
    [JsonPropertyOrder(8)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>Gets or sets the complete reference pulled by the pull pipeline.</summary>
    [JsonPropertyOrder(9)]
    public string PullReference { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolved remote push target, or <see langword="null"/> when no push step exists.</summary>
    [JsonPropertyOrder(10)]
    public ModuleImagePushDescription? Push { get; set; }

    /// <summary>Gets or sets the image build origin, or <see langword="null"/> for images without a publisher.</summary>
    [JsonPropertyOrder(11)]
    public ModuleImageBuildDescription? Build { get; set; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Module);
        ArgumentException.ThrowIfNullOrWhiteSpace(Resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(EffectiveResource);
        ArgumentException.ThrowIfNullOrWhiteSpace(Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(PullReference);
        if (!Enum.IsDefined(ResourceKind))
        {
            throw new InvalidDataException($"Unsupported resource kind '{ResourceKind}'.");
        }

        Push?.Validate();
    }
}

/// <summary>Describes the structured tagged image target used by the push pipeline.</summary>
public sealed class ModuleImagePushDescription
{
    /// <summary>Gets or sets the registry host.</summary>
    [JsonPropertyOrder(0)]
    public string Registry { get; set; } = string.Empty;

    /// <summary>Gets or sets the repository path within the registry.</summary>
    [JsonPropertyOrder(1)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the tag pushed by the pipeline.</summary>
    [JsonPropertyOrder(2)]
    public string Tag { get; set; } = string.Empty;

    /// <summary>Gets the complete tagged reference pushed by the pipeline.</summary>
    [JsonIgnore]
    public string Reference => $"{Registry}/{Repository}:{Tag}";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(Tag);
    }
}

/// <summary>Describes the command and source used to build a module container image.</summary>
public sealed class ModuleImageBuildDescription
{
    /// <summary>Gets or sets the build executable.</summary>
    [JsonPropertyOrder(0)]
    public string Command { get; set; } = string.Empty;

    /// <summary>Gets the effective build arguments.</summary>
    [JsonPropertyOrder(1)]
    public IList<string> Arguments { get; } = [];

    /// <summary>Gets or sets the absolute build working directory.</summary>
    [JsonPropertyOrder(2)]
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the source repository identity or local path.</summary>
    [JsonPropertyOrder(3)]
    public string? Repository { get; set; }

    /// <summary>Gets or sets the source repository revision.</summary>
    [JsonPropertyOrder(4)]
    public string? Revision { get; set; }

    /// <summary>Gets or sets the Aspire pipeline step that builds the image.</summary>
    [JsonPropertyOrder(5)]
    public string Step { get; set; } = string.Empty;
}
