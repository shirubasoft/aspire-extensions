namespace Aspire.Hosting;

internal sealed class ModuleResourceNameMap
{
    private readonly Dictionary<string, string> _resourceNames;

    public ModuleResourceNameMap(
        DistributedApplicationModule module,
        ModuleImportOptions? options)
    {
        var definitions = module.ResourceDefinitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateAliases(module, options, definitions);
        var prefix = options?.ResourcePrefix ?? string.Empty;
        _resourceNames = CreateResourceNames(module, options, prefix);
    }

    private static void ValidateAliases(
        DistributedApplicationModule module,
        ModuleImportOptions? options,
        HashSet<string> definitions)
    {
        var unknownAlias = options?.ResourceAliases.Keys.FirstOrDefault(alias => !definitions.Contains(alias));
        if (unknownAlias is not null)
        {
            throw new InvalidOperationException(
                $"Import options for module '{module.Name}' alias unknown resource '{unknownAlias}'. " +
                $"Available resources: {string.Join(", ", definitions.Order(StringComparer.OrdinalIgnoreCase))}.");
        }
    }

    private static Dictionary<string, string> CreateResourceNames(
        DistributedApplicationModule module,
        ModuleImportOptions? options,
        string prefix)
    {
        var resourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in module.ResourceDefinitions)
        {
            AddResourceName(resourceNames, module, options, prefix, definition.Name);
        }

        return resourceNames;
    }

    private static void AddResourceName(
        Dictionary<string, string> resourceNames,
        DistributedApplicationModule module,
        ModuleImportOptions? options,
        string prefix,
        string definitionName)
    {
        var resourceName = ResolveResourceName(options, prefix, definitionName);
        ValidateResourceName(module, definitionName, resourceName);
        ValidateDuplicateResourceName(resourceNames, module, definitionName, resourceName);
        resourceNames.Add(definitionName, resourceName);
    }

    private static string ResolveResourceName(
        ModuleImportOptions? options,
        string prefix,
        string definitionName) =>
        options?.ResourceAliases.TryGetValue(definitionName, out var alias) == true
            ? alias
            : $"{prefix}{definitionName}";

    private static void ValidateResourceName(
        DistributedApplicationModule module,
        string definitionName,
        string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new InvalidOperationException(
                $"Import options for module '{module.Name}' map resource '{definitionName}' to an empty name.");
        }
    }

    private static void ValidateDuplicateResourceName(
        IReadOnlyDictionary<string, string> resourceNames,
        DistributedApplicationModule module,
        string definitionName,
        string resourceName)
    {
        var duplicate = resourceNames.FirstOrDefault(pair =>
            string.Equals(pair.Value, resourceName, StringComparison.OrdinalIgnoreCase));
        if (duplicate.Key is not null)
        {
            throw new InvalidOperationException(
                $"Import options for module '{module.Name}' map both '{duplicate.Key}' and '{definitionName}' " +
                $"to resource name '{resourceName}'.");
        }
    }

    public string this[string declaredName] => _resourceNames[declaredName];
}
