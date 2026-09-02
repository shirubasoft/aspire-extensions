using Aspire.Hosting;
using Aspire.Hosting.Testing;
using EShop.Modules;

var builder = DistributedApplication.CreateBuilder(args);
var sampleRoot = Path.GetFullPath("..", builder.AppHostDirectory);
builder.Configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] =
    "gh-is-not-needed-for-same-repository-modules";
builder.Configuration[DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(CatalogModule.Name)] =
    sampleRoot;
builder.Configuration[DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(OrdersModule.Name)] =
    sampleRoot;

var compose = builder.AddDockerComposeEnvironment("e2e")
    .WithDashboard(false);
var ordersApiKey = builder.AddParameter("orders-api-key", secret: true);

var orders = builder.AddOrdersModule();
var catalog = builder.AddCatalogModule();

orders.Api
    .WithEnvironment("Orders__ApiKey", ordersApiKey);

compose
    .WithTestEndpoint(
        CatalogModule.ApiResourceName,
        catalog.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestEndpoint(
        OrdersModule.ApiResourceName,
        orders.Api.GetEndpoint("http"),
        healthCheckPath: "/health")
    .WithTestValue("Parameters:orders-api-key", ordersApiKey.Resource);

await builder.Build().RunAsync();
