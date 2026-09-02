using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace EShop.Modules;

[GenerateDistributedApplicationModule(Name)]
public static partial class CatalogModule
{
    public const string Name = "catalog";
    public const string ApiResourceName = "catalog-api";
    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.RequiresRepository();
        module.AddResource<ProjectResource>(ApiResourceName, context =>
            context.ApplicationBuilder
                .AddProject(
                    context.ResourceName,
                    Path.Combine(context.RepositoryPath, "EShop.Catalog.Api", "EShop.Catalog.Api.csproj"))
                .WithHttpEndpoint(name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health"));
    }
}
