using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class DistributedApplicationModuleExtensionsTests
{
    [Fact]
    public void ExportModule_registers_definition_without_materializing_resources()
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);

        var module = builder.ExportModule("orders", definition =>
            definition.AddContainer("orders-cache", "redis"));

        Assert.Equal("orders", module.Name);
        Assert.Empty(builder.Resources);
        var catalog = Assert.IsAssignableFrom<IDistributedApplicationModuleCatalog>(
            builder.Services.Last(descriptor =>
                descriptor.ServiceType == typeof(IDistributedApplicationModuleCatalog)).ImplementationInstance);
        Assert.True(catalog.TryGetModule("orders", out var registered));
        Assert.Same(module, registered);
    }

    [Fact]
    public void ImportModule_applies_prefixes_and_aliases_without_changing_typed_lookup_names()
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);
        builder.ExportModule("orders", definition =>
            definition.AddContainer("cache", "redis"));

        var imported = builder.ImportModule("orders", new ModuleImportOptions
        {
            ResourcePrefix = "shop-",
            ResourceAliases = { ["cache"] = "orders-cache" }
        });

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("orders-cache", container.Name);
        Assert.Same(container, imported.GetResource<ContainerResource>("cache").Resource);
    }

    [Fact]
    public void AddModule_rejects_a_definition_from_another_builder()
    {
        using var source = TemporaryDirectory.Create();
        var definitionBuilder = CreateBuilder(source.Path);
        var materializationBuilder = CreateBuilder(source.Path);
        var module = definitionBuilder.ExportModule("orders", definition =>
            definition.AddContainer("cache", "redis"));

        var exception = Assert.Throws<ArgumentException>(() =>
            materializationBuilder.AddModule(module));

        Assert.Contains("different distributed application builder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportModule_rejects_empty_and_unexported_project_definitions()
    {
        using var source = TemporaryDirectory.Create();
        var emptyBuilder = CreateBuilder(source.Path);
        Assert.Throws<InvalidOperationException>(() =>
            emptyBuilder.ExportModule("empty", _ => { }));

        var projectPath = Path.Combine(source.Path, "Orders.Api.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var projectBuilder = CreateBuilder(source.Path);
        Assert.Throws<InvalidOperationException>(() =>
            projectBuilder.ExportModule("orders", definition =>
                definition.AddProject("orders-api", projectPath)));
    }

    [Fact]
    public void Generic_factories_can_resolve_earlier_resources_in_declaration_order()
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);
        var module = builder.ExportModule("portable", definition =>
        {
            definition.AddContainer("cache", "redis");
            definition.AddResource<ContainerResource>("worker", context =>
            {
                var cache = context.GetResource<ContainerResource>("cache");
                return context.ApplicationBuilder
                    .AddContainer(context.ResourceName, "busybox")
                    .WaitFor(cache);
            });
        });

        builder.AddModule(module);

        Assert.Equal(2, builder.Resources.OfType<ContainerResource>().Count());
        Assert.Equal("worker", module.GetResource<ContainerResource>("worker").Resource.Name);
    }

    [Fact]
    public void Configuration_rejects_unknown_resources_and_invalid_values_synchronously()
    {
        using var source = TemporaryDirectory.Create();
        var builder = CreateBuilder(source.Path);
        builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:orders:Containers:missing:ImageName"] =
            "missing";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("orders", definition =>
                definition.AddContainer("cache", "redis")));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);

        var invalidBuilder = CreateBuilder(source.Path);
        var timeoutException = Assert.Throws<InvalidOperationException>(() =>
            invalidBuilder.ConfigureModularAppHosts(options =>
                options.RepositoryCommandTimeout = TimeSpan.Zero));
        Assert.Contains(nameof(ModularAppHostsOptions.RepositoryCommandTimeout), timeoutException.Message);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(DistributedApplicationModuleExtensionsTests).Assembly.FullName,
            ProjectDirectory = projectDirectory,
            DisableDashboard = true
        });
}
