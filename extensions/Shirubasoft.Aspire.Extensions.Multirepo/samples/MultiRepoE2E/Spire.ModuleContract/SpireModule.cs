using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Spire.ModuleContract;

[GenerateDistributedApplicationModule(Name, Version = "1")]
public static partial class SpireModule
{
    public const string Name = "multi-repo-resource-build";
    public const string ApiResourceName = "multi-repo-api";
    public const string ImageName = "multi-repo-e2e-resource";

    public static void Define(IDistributedApplicationModuleBuilder module)
    {
        module.AddContainer(ApiResourceName, ImageName)
            .WithImagePublishCommand(new ModuleImageCommandOptions(
                imageName: ImageName,
                publishCommand: ModuleImageCommandOptions.ContainerRuntimePlaceholder,
                publishArguments:
                [
                    "build",
                    "--file",
                    "Dockerfile",
                    "--tag",
                    ModuleImageCommandOptions.ImageReferencePlaceholder,
                    "."
                ])
            {
                WorkingDirectory = "."
            })
            .Configure((_, container) => container
                .WithHttpEndpoint(targetPort: 80, name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health.txt"));
    }
}
