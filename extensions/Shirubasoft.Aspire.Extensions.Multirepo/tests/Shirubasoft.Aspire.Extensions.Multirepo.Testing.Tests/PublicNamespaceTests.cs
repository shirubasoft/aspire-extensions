using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.Multirepo.Testing.Tests;

public sealed class PublicNamespaceTests
{
    [Fact]
    public void Runtime_packages_only_export_types_from_namespaces_defined_by_Aspire()
    {
        var aspireNamespaces = new[]
            {
                typeof(IDistributedApplicationBuilder).Assembly,
                typeof(IDistributedApplicationTestingBuilder).Assembly
            }
            .SelectMany(assembly => assembly.DefinedTypes)
            .Select(type => type.Namespace)
            .Where(@namespace => @namespace is not null)
            .ToHashSet(StringComparer.Ordinal);
        var packageAssemblies = new[]
        {
            typeof(DistributedApplicationModuleExtensions).Assembly,
            typeof(DockerComposeDeploymentTestingBuilder).Assembly
        };

        var unexpectedTypes = packageAssemblies
            .SelectMany(assembly => assembly.ExportedTypes)
            .Where(type => type.Namespace is null || !aspireNamespaces.Contains(type.Namespace))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedTypes);
    }
}
