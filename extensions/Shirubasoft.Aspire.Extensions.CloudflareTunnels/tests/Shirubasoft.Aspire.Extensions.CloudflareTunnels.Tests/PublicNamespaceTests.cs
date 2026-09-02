using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class PublicNamespaceTests
{
    [Fact]
    public void PackageOnlyExportsTypesFromNamespacesDefinedByAspire()
    {
        var aspireNamespaces = typeof(IDistributedApplicationBuilder).Assembly
            .DefinedTypes
            .Select(type => type.Namespace)
            .Where(@namespace => @namespace is not null)
            .ToHashSet(StringComparer.Ordinal);

        var unexpectedTypes = typeof(CloudflareTunnelResourceBuilderExtensions).Assembly
            .ExportedTypes
            .Where(type => type.Namespace is null || !aspireNamespaces.Contains(type.Namespace))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedTypes);
    }
}
