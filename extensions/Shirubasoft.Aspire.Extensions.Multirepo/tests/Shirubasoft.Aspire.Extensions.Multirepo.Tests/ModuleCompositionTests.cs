using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleCompositionTests
{
    [Fact]
    public void Added_module_is_materialized_before_the_composing_modules_resources()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        IDistributedApplicationModule? catalog = null;
        IResourceBuilder<ContainerResource>? resolvedCatalog = null;
        var orders = builder.DefineModule("orders", "1", definition =>
        {
            catalog = definition.AddModule(
                "catalog",
                "2",
                "Sample.Catalog",
                catalogDefinition => catalogDefinition.AddContainer("catalog-api", "catalog"));
            definition.AddResource<ContainerResource>("orders-api", context =>
            {
                resolvedCatalog = catalog.GetResource<ContainerResource>("catalog-api");
                return context.ApplicationBuilder.AddContainer(context.ResourceName, "orders");
            });
        });

        builder.AddModule(orders);

        Assert.NotNull(catalog);
        Assert.Equal("catalog-api", Assert.IsType<ContainerResource>(resolvedCatalog!.Resource).Name);
        Assert.Equal(
            ["catalog-api", "orders-api"],
            builder.Resources.OfType<ContainerResource>().Select(resource => resource.Name));
        Assert.All(
            builder.Resources.OfType<ContainerResource>(),
            resource => Assert.False(Assert.Single(
                resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>()).Imported));
    }

    [Fact]
    public void Imported_module_snapshots_resource_naming_options()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var importOptions = new ModuleImportOptions { ResourcePrefix = "dependency-" };
        IDistributedApplicationModule? catalog = null;
        var storefront = builder.DefineModule("storefront", "1", definition =>
        {
            catalog = definition.ImportModule(
                "catalog",
                "1",
                packageId: null,
                catalogDefinition => catalogDefinition.AddContainer("api", "catalog"),
                importOptions);
        });
        importOptions.ResourcePrefix = "mutated-";
        importOptions.ResourceAliases["api"] = "mutated-api";

        builder.AddModule(storefront);

        var resource = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("dependency-api", resource.Name);
        Assert.True(Assert.Single(
            resource.Annotations.OfType<DistributedApplicationModuleResourceAnnotation>()).Imported);
        Assert.Equal("dependency-api", catalog!.GetResource<ContainerResource>("api").Resource.Name);
    }

    [Fact]
    public void Composition_only_module_can_add_a_generated_module_reference()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var catalog = builder.DefineModule(
            "catalog",
            "1",
            definition => definition.AddContainer("api", "catalog"));
        var reference = new TestModuleReference(catalog);
        var storefront = builder.DefineModule("storefront", "1", definition =>
            Assert.Same(reference, definition.AddModule(reference)));

        builder.AddModule(storefront);

        Assert.Equal("api", reference.GetResource<ContainerResource>("api").Resource.Name);
        Assert.Single(builder.Resources.OfType<ContainerResource>());
    }

    [Fact]
    public void Composed_module_rejects_a_conflicting_existing_materialization()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var catalog = builder.DefineModule(
            "catalog",
            "1",
            definition => definition.AddContainer("api", "catalog"));
        var storefront = builder.DefineModule("storefront", "1", definition =>
            definition.ImportModule(catalog));
        builder.AddModule(catalog);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddModule(storefront));

        Assert.Contains("already materialized with different", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_module_definitions_reject_cycles()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.DefineModule("orders", "1", orders =>
                orders.AddModule("catalog", "1", packageId: null, catalog =>
                    catalog.AddModule("orders", "1", packageId: null, _ =>
                        throw new InvalidOperationException("The cyclic definition must not run.")))));

        Assert.Equal(
            "Module definition cycle detected: 'orders' -> 'catalog' -> 'orders'.",
            exception.Message);
    }

    [Fact]
    public void Composed_module_must_belong_to_the_same_application_builder()
    {
        using var firstAppHost = TemporaryDirectory.Create();
        using var secondAppHost = TemporaryDirectory.Create();
        var firstBuilder = CreateBuilder(firstAppHost.Path);
        var secondBuilder = CreateBuilder(secondAppHost.Path);
        var dependency = secondBuilder.DefineModule(
            "catalog",
            "1",
            definition => definition.AddContainer("api", "catalog"));

        var exception = Assert.Throws<ArgumentException>(() =>
            firstBuilder.DefineModule("storefront", "1", definition => definition.AddModule(dependency)));

        Assert.Contains("different distributed application builder", exception.Message, StringComparison.Ordinal);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(ModuleCompositionTests).Assembly.FullName,
            ProjectDirectory = projectDirectory,
            DisableDashboard = true
        });

    private sealed class TestModuleReference(IDistributedApplicationModule module)
        : DistributedApplicationModuleReference(module);
}
