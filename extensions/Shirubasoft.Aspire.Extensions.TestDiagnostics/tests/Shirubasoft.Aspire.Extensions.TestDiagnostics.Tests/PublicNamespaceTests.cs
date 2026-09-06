using Xunit;
using Aspire.Hosting.Testing;

namespace Aspire.Hosting.Tests;

public sealed class PublicNamespaceTests
{
    [Fact]
    public void PackageOnlyExportsTypesFromNamespacesDefinedByAspire()
    {
        var aspireNamespaces = typeof(IDistributedApplicationTestingBuilder).Assembly
            .DefinedTypes
            .Select(type => type.Namespace)
            .Where(@namespace => @namespace is not null)
            .ToHashSet(StringComparer.Ordinal);

        var unexpectedTypes = typeof(DiagnosticsTestingBuilder).Assembly
            .ExportedTypes
            .Where(type => type.Namespace is null || !aspireNamespaces.Contains(type.Namespace))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedTypes);
    }
}
