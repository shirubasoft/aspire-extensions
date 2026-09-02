using Aspire.Hosting;

namespace AspireModuleNamespace;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class CatalogModule
{
    public const string Name = "catalog-module-name";
    public const string ApiResourceName = "catalog-api";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, "nginx", "alpine")
            .Configure((_, container) => container
                .WithHttpEndpoint(targetPort: 80, name: "http"));
    }
}
