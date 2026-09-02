using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace EShop.Modules;

[GenerateDistributedApplicationModule(Name)]
public static partial class OrdersModule
{
    public const string Name = "orders";
    public const string ApiResourceName = "orders-api";
    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        var catalog = module.AddCatalogModule();

        module.RequiresRepository();
        module.AddResource<ProjectResource>(ApiResourceName, context =>
            context.ApplicationBuilder
                .AddProject(
                    context.ResourceName,
                    Path.Combine(context.RepositoryPath, "EShop.Orders.Api", "EShop.Orders.Api.csproj"))
                .WithHttpEndpoint(name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health")
                .WithReference(catalog.Api.GetEndpoint("http"))
                .WithEnvironment("Catalog__Endpoint", catalog.Api.GetEndpoint("http"))
                .WaitFor(catalog.Api));
    }
}
