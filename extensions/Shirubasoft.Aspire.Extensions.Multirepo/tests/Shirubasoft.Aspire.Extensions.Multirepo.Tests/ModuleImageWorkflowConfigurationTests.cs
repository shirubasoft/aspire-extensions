using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageWorkflowConfigurationTests
{
    [Fact]
    public void Workflow_document_projects_to_standard_configuration_keys()
    {
        var document = new ModuleImageWorkflowDocument();
        document.Images.Add(new ModuleImageWorkflowEntry
        {
            Module = "orders",
            Resource = "api",
            ResourceKind = ModuleResourceKind.Project,
            Registry = "registry.example.test",
            Repository = "acme/orders-api",
            Tag = "candidate"
        });

        var values = ModuleImageWorkflowConfiguration.Create(document);

        const string prefix = "Aspire:ModularAppHosts:Modules:orders:Projects:api";
        Assert.Equal("registry.example.test", values[$"{prefix}:ImageRegistry"]);
        Assert.Equal("acme/orders-api", values[$"{prefix}:ImageName"]);
        Assert.Equal("candidate", values[$"{prefix}:ImageTag"]);
        Assert.Equal(string.Empty, values[$"{prefix}:ImageSHA256"]);
        Assert.Equal(bool.FalseString, values[$"{prefix}:PublishImage"]);
        Assert.Equal(nameof(ImagePullPolicy.Always), values[$"{prefix}:ImagePullPolicy"]);
        Assert.Equal(nameof(ModuleProjectMode.Container), values[$"{prefix}:ProjectMode"]);
    }

    [Theory]
    [InlineData("orders:shadow")]
    [InlineData("orders__shadow")]
    [InlineData("orders/shadow")]
    [InlineData("orders=shadow")]
    public void Workflow_document_rejects_names_that_can_escape_identity_or_configuration_segments(string module)
    {
        var document = new ModuleImageWorkflowDocument();
        document.Images.Add(new ModuleImageWorkflowEntry
        {
            Module = module,
            Resource = "api",
            ResourceKind = ModuleResourceKind.Container,
            Registry = "registry.example.test",
            Repository = "acme/orders-api",
            Tag = "candidate"
        });

        var exception = Assert.Throws<InvalidDataException>(document.Validate);

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Description_selection_uses_explicit_module_and_resource_names()
    {
        var descriptions = new[]
        {
            CreateDescription("orders", "api", "orders-api"),
            CreateDescription("orders", "worker", "orders-worker"),
            CreateDescription("catalog", "api", "catalog-api")
        };

        Assert.Equal(2, new ModuleImageSelection(["orders"], [])
            .ResolveDescriptions(descriptions, "test images").Count);
        Assert.Equal("catalog-api", Assert.Single(new ModuleImageSelection([], ["catalog-api"])
            .ResolveDescriptions(descriptions, "test images")).EffectiveResource);
        Assert.Equal(2, new ModuleImageSelection([], ["api"])
            .ResolveDescriptions(descriptions, "test images").Count);
    }

    [Fact]
    public void Workflow_configuration_owns_raw_selectors_and_tag_overrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ModuleImageWorkflowConfiguration.ModuleSelectionConfigurationSectionName}:0"] = "orders",
                [$"{ModuleImageWorkflowConfiguration.ResourceSelectionConfigurationSectionName}:0"] = "api",
                [$"{ModuleImageWorkflowConfiguration.ConfigurationSectionName}:{ModuleImageWorkflowConfiguration.TagConfigurationName}"] =
                    "global",
                [$"{ModuleImageWorkflowConfiguration.ConfigurationSectionName}:{ModuleImageWorkflowConfiguration.ResourceTagsConfigurationName}"] =
                    "{\"orders/api\":\"candidate\"}"
            })
            .Build();

        var workflow = ModuleImageWorkflowConfiguration.Read(configuration);

        Assert.True(workflow.Selection.IsScoped);
        Assert.Equal("candidate", workflow.ResolveTag("orders", "api"));
        Assert.Equal("global", workflow.ResolveTag("orders", "worker"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"orders/api/extra\":\"candidate\"}")]
    [InlineData("{\"orders/api\":\"invalid tag\"}")]
    public void Workflow_configuration_rejects_invalid_resource_tags(string resourceTags)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ModuleImageWorkflowConfiguration.ConfigurationSectionName}:{ModuleImageWorkflowConfiguration.ResourceTagsConfigurationName}"] =
                    resourceTags
            })
            .Build();

        Assert.ThrowsAny<Exception>(() => ModuleImageWorkflowConfiguration.Read(configuration));
    }

    private static ModuleImageDescription CreateDescription(
        string module,
        string resource,
        string effectiveResource) => new()
        {
            Module = module,
            Resource = resource,
            EffectiveResource = effectiveResource,
            ResourceKind = ModuleResourceKind.Project,
            Repository = $"acme/{module}-{resource}",
            Reference = $"acme/{module}-{resource}:candidate",
            PullReference = $"acme/{module}-{resource}:candidate"
        };
}
