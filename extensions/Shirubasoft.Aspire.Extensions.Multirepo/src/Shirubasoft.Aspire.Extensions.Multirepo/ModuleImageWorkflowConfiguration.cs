using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>Projects a module image workflow document into standard modular AppHost configuration.</summary>
public static class ModuleImageWorkflowConfiguration
{
    private static readonly string[] ReservedIdentitySeparators =
        [ConfigurationPath.KeyDelimiter, "__", "/", "="];

    /// <summary>Configuration section containing module names selected for a workflow image publish.</summary>
    public const string ModuleSelectionConfigurationSectionName =
        "Aspire:ModularAppHosts:Workflow:Modules";

    /// <summary>Configuration section containing resource names selected for a workflow image publish.</summary>
    public const string ResourceSelectionConfigurationSectionName =
        "Aspire:ModularAppHosts:Workflow:Resources";

    /// <summary>The configuration section used by the workflow image pipeline.</summary>
    public const string ConfigurationSectionName = "Aspire:ModularAppHosts:Workflow";

    /// <summary>The workflow configuration key for a tag applied to every selected image.</summary>
    public const string TagConfigurationName = "Tag";

    /// <summary>The workflow configuration key for JSON module/resource tag overrides.</summary>
    public const string ResourceTagsConfigurationName = "ResourceTags";

    internal static ModuleImageWorkflowOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var modules = ReadSelection(configuration, ModuleSelectionConfigurationSectionName);
        var resources = ReadSelection(configuration, ResourceSelectionConfigurationSectionName);
        var globalTag = GetConfiguredValue(
            configuration[ConfigurationPath.Combine(ConfigurationSectionName, TagConfigurationName)]);
        ValidateTag(globalTag);
        var resourceTagsJson = GetConfiguredValue(
            configuration[ConfigurationPath.Combine(ConfigurationSectionName, ResourceTagsConfigurationName)]);
        var resourceTags = ReadResourceTags(resourceTagsJson);
        var normalizedTags = NormalizeResourceTags(resourceTags);

        return new ModuleImageWorkflowOptions(
            CreateSelection(modules, resources),
            globalTag,
            normalizedTags);
    }

    private static string[] ReadSelection(IConfiguration configuration, string sectionName) =>
        configuration.GetSection(sectionName).Get<string[]>() ?? [];

    private static Dictionary<string, string> ReadResourceTags(string? resourceTagsJson)
    {
        if (resourceTagsJson is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return DeserializeResourceTags(resourceTagsJson);
    }

    private static Dictionary<string, string> DeserializeResourceTags(string resourceTagsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(resourceTagsJson)
                ?? throw new InvalidDataException("Workflow resource tags must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Workflow resource tags must be a JSON object.", exception);
        }
    }

    private static Dictionary<string, string> NormalizeResourceTags(
        Dictionary<string, string> resourceTags)
    {
        var normalizedTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, tag) in resourceTags)
        {
            var normalizedIdentity = ValidateIdentity(identity);
            ValidateTag(tag);
            normalizedTags.Add(normalizedIdentity, tag);
        }

        return normalizedTags;
    }

    private static ModuleImageSelection CreateSelection(string[] modules, string[] resources)
    {
        if (modules.Length == 0 && resources.Length == 0)
        {
            return ModuleImageSelection.All;
        }

        return new ModuleImageSelection(modules, resources);
    }

    /// <summary>Creates the configuration overrides represented by <paramref name="document"/>.</summary>
    public static IReadOnlyDictionary<string, string> Create(ModuleImageWorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in document.Images)
        {
            AddImageConfiguration(values, image);
        }

        return values;
    }

    private static void AddImageConfiguration(
        Dictionary<string, string> values,
        ModuleImageWorkflowEntry image)
    {
        var prefix = GetResourceKey(image.Module, image.Resource, image.ResourceKind);
        values[ConfigurationPath.Combine(prefix, "ImageRegistry")] = image.Registry;
        values[ConfigurationPath.Combine(prefix, "ImageName")] = image.Repository;
        values[ConfigurationPath.Combine(prefix, "ImageTag")] = image.Tag ?? string.Empty;
        values[ConfigurationPath.Combine(prefix, "ImageSHA256")] = image.Digest ?? string.Empty;
        values[ConfigurationPath.Combine(prefix, "PublishImage")] = bool.FalseString;
        values[ConfigurationPath.Combine(prefix, "ImagePullPolicy")] = ImagePullPolicy.Always.ToString();
        AddProjectModeConfiguration(values, image, prefix);
    }

    private static void AddProjectModeConfiguration(
        Dictionary<string, string> values,
        ModuleImageWorkflowEntry image,
        string prefix)
    {
        if (image.ResourceKind == ModuleResourceKind.Project)
        {
            values[ConfigurationPath.Combine(prefix, "ProjectMode")] = ModuleProjectMode.Container.ToString();
        }
    }

    /// <summary>Gets the standard configuration key for a declared module resource.</summary>
    public static string GetResourceKey(
        string module,
        string resource,
        ModuleResourceKind resourceKind)
    {
        ValidateSegment(module, nameof(module));
        ValidateSegment(resource, nameof(resource));
        var collection = resourceKind switch
        {
            ModuleResourceKind.Project => "Projects",
            ModuleResourceKind.Container => "Containers",
            _ => throw new InvalidDataException($"Unsupported module resource kind '{resourceKind}'.")
        };
        return ConfigurationPath.Combine(
            ModularAppHostsOptions.ConfigurationSectionName,
            "Modules",
            module,
            collection,
            resource);
    }

    internal static void ValidateSegment(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (ReservedIdentitySeparators.Any(separator => value.Contains(separator, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Module image {name} '{value}' contains a reserved identity or configuration separator.");
        }
    }

    private static string ValidateIdentity(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var slash = identity.IndexOf('/', StringComparison.Ordinal);
        if (!HasValidIdentityFormat(identity, slash))
        {
            throw new InvalidDataException(
                $"Workflow resource tag '{identity}' must use the form <module>/<resource>.");
        }

        var module = identity[..slash];
        var resource = identity[(slash + 1)..];
        ValidateSegment(module, nameof(module));
        ValidateSegment(resource, nameof(resource));
        return $"{module}/{resource}";
    }

    private static bool HasValidIdentityFormat(string identity, int slash)
    {
        if (slash <= 0 || slash == identity.Length - 1)
        {
            return false;
        }

        return identity.IndexOf('/', slash + 1) < 0;
    }

    private static void ValidateTag(string? tag)
    {
        if (tag is not null && !ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new InvalidDataException($"Workflow image tag '{tag}' is not a valid OCI distribution tag.");
        }
    }

    private static string? GetConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed record ModuleImageWorkflowOptions(
    ModuleImageSelection Selection,
    string? GlobalTag,
    IReadOnlyDictionary<string, string> ResourceTags)
{
    public string? ResolveTag(string module, string resource) =>
        ResourceTags.TryGetValue($"{module}/{resource}", out var tag) ? tag : GlobalTag;

    public void ValidateSelectedResources(IReadOnlySet<IResource> selectedResources)
    {
        ArgumentNullException.ThrowIfNull(selectedResources);
        var selectedIdentities = selectedResources
            .Select(resource => resource.Annotations
                .OfType<DistributedApplicationModuleResourceAnnotation>()
                .LastOrDefault())
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .Select(annotation => $"{annotation.ModuleName}/{annotation.ResourceName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatched = ResourceTags.Keys
            .Where(identity => !selectedIdentities.Contains(identity))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmatched.Length > 0)
        {
            throw new InvalidOperationException(
                $"Workflow resource tag overrides do not match selected images: {string.Join(", ", unmatched)}.");
        }
    }
}
