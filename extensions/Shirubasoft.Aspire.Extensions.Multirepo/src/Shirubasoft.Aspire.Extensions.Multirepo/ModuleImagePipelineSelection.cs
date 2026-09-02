using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal enum ModuleImageSelectorKind
{
    Resource,
    Module
}

internal sealed record ModuleImageSelector(
    ModuleImageSelectorKind Kind,
    string Name);

/// <summary>Parses and resolves module image selectors consistently across AppHost pipelines and tools.</summary>
public sealed class ModuleImageSelection
{
    /// <summary>Gets a selection that includes every available image.</summary>
    public static ModuleImageSelection All { get; } = new([], []);

    /// <summary>Creates a selection from explicit module and resource names.</summary>
    public ModuleImageSelection(
        IEnumerable<string> modules,
        IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(resources);
        Selectors = modules
            .Select(name => CreateSelector(ModuleImageSelectorKind.Module, name))
            .Concat(resources.Select(name => CreateSelector(ModuleImageSelectorKind.Resource, name)))
            .Distinct()
            .ToArray();
    }

    internal IReadOnlyList<ModuleImageSelector> Selectors { get; }

    /// <summary>Gets whether the selection contains one or more selectors.</summary>
    public bool IsScoped => Selectors.Count > 0;

    /// <summary>Resolves selectors against structured image descriptions.</summary>
    public IReadOnlyList<ModuleImageDescription> ResolveDescriptions(
        IEnumerable<ModuleImageDescription> descriptions,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        var candidates = descriptions
            .Select(description => new SelectionCandidate<ModuleImageDescription>(
                description,
                description.Module,
                description.Resource,
                description.EffectiveResource))
            .ToArray();
        return Resolve(candidates, operation)
            .OrderBy(description => description.Module, StringComparer.Ordinal)
            .ThenBy(description => description.Resource, StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlySet<IResource> ResolveResources(
        IEnumerable<IResource> resources,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var candidates = resources
            .Distinct()
            .Select(resource => new SelectionCandidate<IResource>(
                resource,
                GetModuleName(resource),
                GetDeclaredResourceName(resource),
                resource.Name))
            .ToArray();
        return Resolve(candidates, operation).ToHashSet();
    }

    private HashSet<T> Resolve<T>(
        IReadOnlyList<SelectionCandidate<T>> available,
        string operation)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!IsScoped)
        {
            return GetAllCandidates(available);
        }

        return ResolveScoped(available, operation);
    }

    private static HashSet<T> GetAllCandidates<T>(IEnumerable<SelectionCandidate<T>> available)
        where T : notnull =>
        available.Select(candidate => candidate.Value).ToHashSet();

    private HashSet<T> ResolveScoped<T>(
        IReadOnlyList<SelectionCandidate<T>> available,
        string operation)
        where T : notnull
    {
        var selected = new HashSet<T>();
        var unknown = new List<string>();
        foreach (var selector in Selectors)
        {
            AddSelectorMatches(selected, unknown, available, selector);
        }

        ThrowIfSelectorsAreUnknown(unknown, available, operation);
        return selected;
    }

    private static void AddSelectorMatches<T>(
        HashSet<T> selected,
        List<string> unknown,
        IReadOnlyList<SelectionCandidate<T>> available,
        ModuleImageSelector selector)
        where T : notnull
    {
        var matches = GetMatches(available, selector);
        if (matches.Length == 0)
        {
            unknown.Add($"{GetSelectorKindName(selector.Kind)} '{selector.Name}'");
            return;
        }

        selected.UnionWith(matches.Select(candidate => candidate.Value));
    }

    private static SelectionCandidate<T>[] GetMatches<T>(
        IReadOnlyList<SelectionCandidate<T>> available,
        ModuleImageSelector selector)
        where T : notnull =>
        selector.Kind switch
        {
            ModuleImageSelectorKind.Module => available.Where(candidate =>
                string.Equals(candidate.Module, selector.Name, StringComparison.OrdinalIgnoreCase)).ToArray(),
            ModuleImageSelectorKind.Resource => available.Where(candidate =>
                NameMatches(candidate.DeclaredResource, candidate.EffectiveResource, selector.Name)).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported image selector kind '{selector.Kind}'.")
        };

    private static string GetSelectorKindName(ModuleImageSelectorKind kind) =>
        kind == ModuleImageSelectorKind.Module ? "module" : "resource";

    private static void ThrowIfSelectorsAreUnknown<T>(
        List<string> unknown,
        IReadOnlyList<SelectionCandidate<T>> available,
        string operation)
        where T : notnull
    {
        if (unknown.Count == 0)
        {
            return;
        }

        throw CreateUnknownSelectorsException(unknown, available, operation);
    }

    private static InvalidOperationException CreateUnknownSelectorsException<T>(
        IEnumerable<string> unknown,
        IReadOnlyList<SelectionCandidate<T>> available,
        string operation)
        where T : notnull
    {
        var availableModules = available
            .Select(candidate => candidate.Module)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return new InvalidOperationException(
            $"The following selectors do not match {operation}: {string.Join(", ", unknown)}. " +
            $"Available image resources: {FormatAvailable(available.SelectMany(candidate => candidate.Names))}. " +
            $"Available modules: {FormatAvailable(availableModules)}.");
    }

    internal static string? GetModuleName(IResource resource)
    {
        var moduleResource = resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .LastOrDefault();
        if (moduleResource is not null)
        {
            return moduleResource.ModuleName;
        }

        return GetPublisherModuleName(resource);
    }

    private static string? GetPublisherModuleName(IResource resource) =>
        resource.Annotations
            .OfType<ModuleImagePublisherAnnotation>()
            .Select(annotation => annotation.ModuleName)
            .LastOrDefault();

    private static string GetDeclaredResourceName(IResource resource)
    {
        var moduleResource = resource.Annotations
            .OfType<DistributedApplicationModuleResourceAnnotation>()
            .LastOrDefault();
        if (moduleResource is not null)
        {
            return moduleResource.ResourceName;
        }

        var publisher = resource.Annotations.OfType<ModuleImagePublisherAnnotation>().LastOrDefault();
        return publisher is null ? resource.Name : publisher.ResourceName;
    }

    private static ModuleImageSelector CreateSelector(
        ModuleImageSelectorKind kind,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ModuleImageSelector(kind, name.Trim());
    }

    private static bool NameMatches(
        string declaredResourceName,
        string effectiveResourceName,
        string selector) =>
        string.Equals(declaredResourceName, selector, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(effectiveResourceName, selector, StringComparison.OrdinalIgnoreCase);

    private static string FormatAvailable(IEnumerable<string> values)
    {
        var materialized = values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return materialized.Length == 0 ? "none" : string.Join(", ", materialized);
    }

    private sealed record SelectionCandidate<T>(
        T Value,
        string? Module,
        string DeclaredResource,
        string EffectiveResource)
        where T : notnull
    {
        public IEnumerable<string> Names =>
            new[] { DeclaredResource, EffectiveResource }
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
