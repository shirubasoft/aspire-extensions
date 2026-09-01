using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class PublicNamespaceTests
{
    [Fact]
    public void PackageOnlyExportsTypesFromNamespacesDefinedByAspire()
    {
        var aspireNamespaces = new[]
            {
                typeof(IDistributedApplicationBuilder).Assembly,
                typeof(KafkaServerResource).Assembly,
            }
            .SelectMany(assembly => assembly.DefinedTypes)
            .Select(type => type.Namespace)
            .Where(@namespace => @namespace is not null)
            .ToHashSet(StringComparer.Ordinal);

        var unexpectedTypes = typeof(KafkaResourceBuilderExtensions).Assembly
            .ExportedTypes
            .Where(type => type.Namespace is null || !aspireNamespaces.Contains(type.Namespace))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedTypes);
    }
}
