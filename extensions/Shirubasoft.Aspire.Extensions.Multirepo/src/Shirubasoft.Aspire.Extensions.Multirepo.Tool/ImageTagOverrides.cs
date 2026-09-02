using Aspire.Hosting;
using System.Text.Json;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool;

internal sealed record ResourceTagOverride(string Module, string Resource, string Tag)
{
    public string Identity => $"{Module}/{Resource}";
}

internal sealed class ImageTagOverrides
{
    private readonly Dictionary<string, ResourceTagOverride> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    public ImageTagOverrides(string? globalTag, string resourceTags)
    {
        ArgumentNullException.ThrowIfNull(resourceTags);
        GlobalTag = ParseGlobalTag(globalTag);
        var parsed = ParseResourceTags(resourceTags);
        foreach (var (identity, tag) in parsed)
        {
            AddResourceOverride(Parse(identity, tag));
        }
    }

    private static string? ParseGlobalTag(string? globalTag)
    {
        if (string.IsNullOrWhiteSpace(globalTag))
        {
            return null;
        }

        ValidateTag(globalTag);
        return globalTag;
    }

    private static Dictionary<string, string> ParseResourceTags(string resourceTags) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(resourceTags)
            ?? throw new ToolUsageException("--resource-tags must be a JSON object.");

    private void AddResourceOverride(ResourceTagOverride imageOverride)
    {
        if (!_resources.TryAdd(imageOverride.Identity, imageOverride))
        {
            throw new ToolUsageException(
                $"Resource tag override '{imageOverride.Identity}' is specified more than once.");
        }
    }

    public string? GlobalTag { get; }

    public bool HasOverrides => (GlobalTag is not null) | (_resources.Count > 0);

    public bool HasResourceOverrides => _resources.Count > 0;

    public void Apply(ModuleImageWorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in document.Images)
        {
            ApplyOverride(image, matched);
        }

        ThrowForUnmatched(matched);
        document.Validate();
    }

    private void ApplyOverride(ModuleImageWorkflowEntry image, HashSet<string> matched)
    {
        var tag = GetOverrideTag(image, matched);
        if (tag is null)
        {
            return;
        }

        image.Tag = tag;
        image.Digest = null;
    }

    private string? GetOverrideTag(ModuleImageWorkflowEntry image, HashSet<string> matched)
    {
        var identity = GetIdentity(image.Module, image.Resource);
        if (!_resources.TryGetValue(identity, out var resourceTag))
        {
            return GlobalTag;
        }

        matched.Add(identity);
        return resourceTag.Tag;
    }

    private static ResourceTagOverride Parse(string identity, string tag)
    {
        ValidateOverrideValues(identity, tag);
        var slash = identity.IndexOf('/', StringComparison.Ordinal);
        ValidateIdentitySeparator(identity, slash);
        var module = identity[..slash];
        var resource = identity[(slash + 1)..];
        ValidateTag(tag);
        return new ResourceTagOverride(module, resource, tag);
    }

    private static void ValidateOverrideValues(string identity, string tag)
    {
        ValidateOverrideIdentity(identity);
        ValidateOverrideTag(tag);
    }

    private static void ValidateOverrideIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw CreateEmptyOverrideException();
        }
    }

    private static void ValidateOverrideTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw CreateEmptyOverrideException();
        }
    }

    private static ToolUsageException CreateEmptyOverrideException() =>
        new("Resource tag overrides require non-empty <module>/<resource> keys and tag values.");

    private static void ValidateIdentitySeparator(string identity, int slash)
    {
        ValidateFirstSeparator(identity, slash);
        ValidateLastSeparator(identity, slash);
        ValidateSingleSeparator(identity, slash);
    }

    private static void ValidateFirstSeparator(string identity, int slash)
    {
        if (slash <= 0)
        {
            throw CreateIdentityFormatException(identity);
        }
    }

    private static void ValidateLastSeparator(string identity, int slash)
    {
        if (slash == identity.Length - 1)
        {
            throw CreateIdentityFormatException(identity);
        }
    }

    private static void ValidateSingleSeparator(string identity, int slash)
    {
        if (identity.IndexOf('/', slash + 1) >= 0)
        {
            throw CreateIdentityFormatException(identity);
        }
    }

    private static ToolUsageException CreateIdentityFormatException(string identity) =>
        new($"Resource tag override '{identity}' must use the form <module>/<resource>.");

    private static void ValidateTag(string tag)
    {
        if (!ModuleImageIdentityValidation.IsValidTag(tag))
        {
            throw new ToolUsageException($"Image tag '{tag}' is not a valid OCI distribution tag.");
        }
    }

    private static string GetIdentity(string module, string resource) => $"{module}/{resource}";

    private void ThrowForUnmatched(HashSet<string> matched)
    {
        var unmatched = _resources.Keys
            .Where(key => !matched.Contains(key))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmatched.Length > 0)
        {
            throw new ToolUsageException(
                $"Resource tag overrides do not match selected images: {string.Join(", ", unmatched)}.");
        }
    }
}
