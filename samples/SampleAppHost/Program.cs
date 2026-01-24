using Aspire.Hosting.SharedResources;

var builder = DistributedApplication.CreateBuilder(args);

// Enable shared resource support
builder.AddSharedResourceSupport();

// Example 1: Using inline parameters with options
builder.AddContainer("api-service", "api-service")
    .WithSharedResourceMetadata(
        gitHubRepository: "example-org/api-service",
        serviceName: "api-service",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
            options.DefaultBranch = "main";
        })
    .WithHttpEndpoint(5001, name: "http");

// Example 2: Using pre-built annotation
var workerAnnotation = new SharedResourceAnnotation
{
    GitHubRepository = "example-org/worker-service",
    ServiceName = "worker-service",
    ImageBuildCommand = "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/Dockerfile {RepoPath}",
    DefaultBranch = "main"
};

builder.AddContainer("worker-service", "worker-service")
    .WithSharedResourceMetadata(workerAnnotation);

builder.Build().Run();
