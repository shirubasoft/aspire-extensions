using Aspire.Hosting;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

var samplesRoot = Path.GetFullPath("..", builder.AppHostDirectory);
var appHostASource = Path.Combine(samplesRoot, "AppHostA");
builder.Configuration[DistributedApplicationModuleExtensions.GetRepositoryConfigurationKey(AppHostAModule.Name)] =
    appHostASource;

AppHostAModule.Register(builder, appHostASource);
var imported = builder.ImportAppHostAModule();

var api = imported.Api;
var staticSite = imported.Static;
var generatedStaticSite = imported.GeneratedStatic;
var message = imported.Message;

builder.AddDockerfile("dependency-gateway", "Gateway")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithReference(api.GetEndpoint("http"))
    .WithReference(staticSite.GetEndpoint("http"))
    .WithReference(generatedStaticSite.GetEndpoint("http"))
    .WithEnvironment("UPSTREAM_API", api.GetEndpoint("http"))
    .WithEnvironment("UPSTREAM_STATIC", staticSite.GetEndpoint("http"))
    .WithEnvironment("UPSTREAM_GENERATED_STATIC", generatedStaticSite.GetEndpoint("http"))
    .WithEnvironment("MODULE_MESSAGE", message)
    .WaitFor(api)
    .WaitFor(staticSite)
    .WaitFor(generatedStaticSite)
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
