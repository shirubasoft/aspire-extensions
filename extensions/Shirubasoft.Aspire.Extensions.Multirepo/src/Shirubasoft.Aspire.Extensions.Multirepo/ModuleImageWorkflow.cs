using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

/// <summary>Describes the remotely pullable module images shared between repository workflows.</summary>
public sealed class ModuleImageWorkflowDocument
{
    /// <summary>The largest workflow document accepted as a GitHub workflow input.</summary>
    public const int MaximumJsonLength = 65_535;

    /// <summary>The default file name written by the workflow image pipeline.</summary>
    public const string DefaultFileName = "module-image-workflow.json";

    /// <summary>The schema version understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the document schema version.</summary>
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets the image overrides in deterministic module and resource order.</summary>
    [JsonPropertyOrder(1)]
    public IList<ModuleImageWorkflowEntry> Images { get; } = [];

    /// <summary>Reads and validates an image workflow document.</summary>
    public static async Task<ModuleImageWorkflowDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Reads and validates an image workflow document from JSON.</summary>
    public static ModuleImageWorkflowDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumJsonLength)
        {
            throw new InvalidDataException(
                $"The module image workflow document exceeds the {MaximumJsonLength}-character workflow input limit.");
        }

        var document = JsonSerializer.Deserialize<ModuleImageWorkflowDocument>(json, SerializerOptions)
            ?? throw new InvalidDataException("The module image workflow document is empty.");
        document.Validate();
        return document;
    }

    /// <summary>Writes deterministic JSON to <paramref name="path"/>.</summary>
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
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, this, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Serializes the workflow document as compact JSON suitable for a workflow input.</summary>
    public string ToJson()
    {
        Validate();
        var json = JsonSerializer.Serialize(this, CompactSerializerOptions);
        if (json.Length > MaximumJsonLength)
        {
            throw new InvalidDataException(
                $"The module image workflow document exceeds the {MaximumJsonLength}-character workflow input limit.");
        }

        return json;
    }

    /// <summary>Validates the schema and every module resource image identity.</summary>
    public void Validate()
    {
        ValidateHeader();
        ValidateImages();
        SortImages();
    }

    private void ValidateHeader()
    {
        ValidateSchemaVersion();
        ValidateImageCount();
    }

    private void ValidateSchemaVersion()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported module image workflow document schema version '{SchemaVersion}'. " +
                $"Expected '{CurrentSchemaVersion}'.");
        }
    }

    private void ValidateImageCount()
    {
        if (Images.Count == 0)
        {
            throw new InvalidDataException("The module image workflow document must contain at least one image.");
        }
    }

    private void ValidateImages()
    {
        var identities = new HashSet<(string Module, string Resource)>(ModuleResourceIdentityComparer.Instance);
        foreach (var image in Images)
        {
            ArgumentNullException.ThrowIfNull(image);
            image.Validate();
            if (!identities.Add((image.Module, image.Resource)))
            {
                throw new InvalidDataException(
                    $"The module image workflow document contains duplicate resource '{image.Module}/{image.Resource}'.");
            }
        }
    }

    private void SortImages()
    {
        var ordered = Images
            .OrderBy(image => image.Module, StringComparer.Ordinal)
            .ThenBy(image => image.Resource, StringComparer.Ordinal)
            .ToArray();
        Images.Clear();
        foreach (var image in ordered)
        {
            Images.Add(image);
        }
    }

    internal static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions(writeIndented: true);

    private static JsonSerializerOptions CompactSerializerOptions { get; } = CreateSerializerOptions(writeIndented: false);

    private static JsonSerializerOptions CreateSerializerOptions(bool writeIndented) =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented,
            Converters = { new JsonStringEnumConverter<ModuleResourceKind>(JsonNamingPolicy.CamelCase, false) }
        };
}

/// <summary>Maps one declared module resource to a complete remotely pullable image identity.</summary>
public sealed class ModuleImageWorkflowEntry
{
    /// <summary>Gets or sets the module name.</summary>
    [JsonPropertyOrder(0)]
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the resource name declared by the module contract.</summary>
    [JsonPropertyOrder(1)]
    public string Resource { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of module resource receiving the image.</summary>
    [JsonPropertyOrder(2)]
    public ModuleResourceKind ResourceKind { get; set; }

    /// <summary>Gets or sets the registry host.</summary>
    [JsonPropertyOrder(3)]
    public string Registry { get; set; } = string.Empty;

    /// <summary>Gets or sets the repository path within the registry.</summary>
    [JsonPropertyOrder(4)]
    public string Repository { get; set; } = string.Empty;

    /// <summary>Gets or sets the image tag when the image is not digest-pinned.</summary>
    [JsonPropertyOrder(5)]
    public string? Tag { get; set; }

    /// <summary>Gets or sets the canonical digest when the image is digest-pinned.</summary>
    [JsonPropertyOrder(6)]
    public string? Digest { get; set; }

    /// <summary>Gets the complete image reference represented by this entry.</summary>
    [JsonIgnore]
    public string Reference => Digest is { Length: > 0 }
        ? $"{Registry}/{Repository}@{Digest}"
        : $"{Registry}/{Repository}:{Tag}";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Module);
        ArgumentException.ThrowIfNullOrWhiteSpace(Resource);
        ModuleImageWorkflowConfiguration.ValidateSegment(Module, nameof(Module));
        ModuleImageWorkflowConfiguration.ValidateSegment(Resource, nameof(Resource));
        ArgumentException.ThrowIfNullOrWhiteSpace(Registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(Repository);
        ValidateResourceKind();
        ValidateRegistry();
        ValidateRepository();
        ValidateImageVersion();
    }

    private void ValidateResourceKind()
    {
        if (!Enum.IsDefined(ResourceKind))
        {
            throw new InvalidDataException($"Unsupported module resource kind '{ResourceKind}'.");
        }
    }

    private void ValidateRegistry()
    {
        ValidateRegistryIsNotUrl();
        ValidateRegistryIsNotPath();
    }

    private void ValidateRegistryIsNotUrl()
    {
        if (Registry.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Image registry '{Registry}' must be a host name with an optional port, not a URL or path.");
        }
    }

    private void ValidateRegistryIsNotPath()
    {
        if (Registry.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Image registry '{Registry}' must be a host name with an optional port, not a URL or path.");
        }
    }

    private void ValidateRepository()
    {
        if (HasInvalidRepositoryFormat(Repository))
        {
            throw new InvalidDataException($"Image repository '{Repository}' is not a valid repository path.");
        }
    }

    private static bool HasInvalidRepositoryFormat(string repository) =>
        repository.StartsWith('/') || HasInvalidRepositorySuffix(repository);

    private static bool HasInvalidRepositorySuffix(string repository) =>
        repository.EndsWith('/') || repository.Contains('@', StringComparison.Ordinal);

    private void ValidateImageVersion()
    {
        var tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag;
        var digest = string.IsNullOrWhiteSpace(Digest) ? null : Digest;
        ValidateTagOrDigestChoice(tag, digest);
        ValidateTag(tag);
        ValidateDigest(digest);
    }

    private void ValidateTagOrDigestChoice(string? tag, string? digest)
    {
        if ((tag is null) == (digest is null))
        {
            throw new InvalidDataException(
                $"Module image '{Module}/{Resource}' must specify exactly one tag or digest.");
        }
    }

    private static void ValidateTag(string? tag)
    {
        if (tag is null)
        {
            return;
        }

        if (!ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new InvalidDataException($"Image tag '{tag}' is not a valid OCI distribution tag.");
        }
    }

    private static void ValidateDigest(string? digest)
    {
        if (digest is null)
        {
            return;
        }

        if (!ModuleImageIdentityValidation.IsValidDigest(digest))
        {
            throw new InvalidDataException(
                $"Image digest '{digest}' must use the form 'sha256:<64 lowercase hexadecimal characters>'.");
        }
    }
}

/// <summary>Validates OCI image tags and supported image digests.</summary>
public static class ModuleImageIdentityValidation
{
    /// <summary>Returns whether <paramref name="value"/> is a valid OCI distribution tag.</summary>
    public static bool IsValidTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return HasValidTagLength(value.Length) && HasValidTagCharacters(value);
    }

    private static bool HasValidTagLength(int length) => (uint)(length - 1) < 128;

    private static bool HasValidTagCharacters(string value)
    {
        if (!IsTagFirstCharacter(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(IsTagCharacter);
    }

    private static bool IsTagCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || "_.-".Contains(character, StringComparison.Ordinal);

    /// <summary>Returns whether <paramref name="value"/> is a supported canonical image digest.</summary>
    public static bool IsValidDigest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return HasValidDigestSuffix(value, prefix.Length);
    }

    private static bool HasValidDigestSuffix(string value, int prefixLength)
    {
        if (value.Length != prefixLength + 64)
        {
            return false;
        }

        return value[prefixLength..].All(character =>
            "0123456789abcdef".Contains(character, StringComparison.Ordinal));
    }

    private static bool IsTagFirstCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '_';
}

internal sealed class ModuleResourceIdentityComparer : IEqualityComparer<(string Module, string Resource)>
{
    public static ModuleResourceIdentityComparer Instance { get; } = new();

    public bool Equals(
        (string Module, string Resource) x,
        (string Module, string Resource) y) =>
        string.Equals(x.Module, y.Module, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Resource, y.Resource, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Module, string Resource) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Module),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Resource));
}
