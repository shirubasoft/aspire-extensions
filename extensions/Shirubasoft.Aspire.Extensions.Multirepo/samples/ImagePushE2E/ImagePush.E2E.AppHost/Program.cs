#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;

const string ImageTag = "push-test";
const string ProjectResourceName = "image-push-project";

var builder = DistributedApplication.CreateBuilder(args);
builder.UseModuleContainers();
builder.AddDockerComposeEnvironment("compose");

var registryEndpoint = builder.Configuration["ImagePush:RegistryEndpoint"];
if (string.IsNullOrWhiteSpace(registryEndpoint))
{
    throw new InvalidOperationException(
        "ImagePush:RegistryEndpoint must identify the local registry used by the image-push E2E test.");
}

var retagRegistryEndpoint = builder.Configuration["ImagePush:RetagRegistryEndpoint"] ?? registryEndpoint;

var registry = builder.AddContainerRegistry(
    "image-push-registry",
    registryEndpoint,
    "image-push");
var module = builder.ExportModule("image-push-e2e", "Sample.ImagePush.Contract", definition =>
{
    definition.WithRepository(builder.AppHostDirectory);

    definition.AddContainer(
            "image-push-declared",
            $"{registryEndpoint}/image-push/declared",
            ImageTag)
        .WithImagePublishCommand(new ModuleImageCommandOptions(
            "image-push/declared",
            ModuleImageCommandOptions.ContainerRuntimePlaceholder,
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageRegistry = registryEndpoint,
            ImageTag = ImageTag,
            WorkingDirectory = "ImageFixture"
        })
        .Configure((_, container) => container.WithExplicitStart());

    definition.AddContainer(
            "image-pull-mapped",
            $"{retagRegistryEndpoint}/image-pull/local",
            ImageTag)
        .WithImagePullMapping($"{registryEndpoint}/image-pull/source:{ImageTag}")
        .Configure((_, container) => container.WithExplicitStart());

    definition.AddProject(
            ProjectResourceName,
            Path.Combine(builder.AppHostDirectory, "ExportedProject", "ImagePush.ExportedProject.csproj"))
        .ExportAsContainerWithCommand(
            new ModuleImageCommandOptions(
                ProjectResourceName,
                ModuleImageCommandOptions.ContainerRuntimePlaceholder,
                "build",
                "--tag",
                ModuleImageCommandOptions.ImageReferencePlaceholder,
                ".")
            {
                ImageTag = ImageTag,
                WorkingDirectory = "ImageFixture"
            },
            (_, container) => container
                .WithContainerRegistry(registry)
                .WithRemoteImageName("project")
                .WithRemoteImageTag(ImageTag)
                .WithExplicitStart());

    definition.AddResource<ContainerResource>(
        "image-push-factory",
        context => context.ApplicationBuilder
            .AddContainer(context.ResourceName, "placeholder")
            .WithExplicitStart(),
        new ModuleImageCommandOptions(
            "image-push/factory",
            ModuleImageCommandOptions.ContainerRuntimePlaceholder,
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageRegistry = registryEndpoint,
            ImageTag = ImageTag,
            WorkingDirectory = "ImageFixture"
        });

    definition.AddResource<ContainerResource>(
        "image-push-dockerfile",
        context => context.ApplicationBuilder
            .AddDockerfile(
                context.ResourceName,
                Path.Combine(builder.AppHostDirectory, "ImageFixture"))
            .WithContainerRegistry(registry)
            .WithRemoteImageName("dockerfile")
            .WithRemoteImageTag(ImageTag)
            .WithExplicitStart());
});

builder.AddModule(module);

var extraModule = builder.ExportModule("image-push-extra", definition =>
{
    definition.WithRepository(builder.AppHostDirectory);
    definition.AddContainer(
            "image-push-extra",
            $"{registryEndpoint}/image-push/extra",
            ImageTag)
        .WithImagePublishCommand(new ModuleImageCommandOptions(
            "image-push/extra",
            ModuleImageCommandOptions.ContainerRuntimePlaceholder,
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            ".")
        {
            ImageRegistry = registryEndpoint,
            ImageTag = ImageTag,
            WorkingDirectory = "ImageFixture"
        })
        .Configure((_, container) => container.WithExplicitStart());
});

builder.AddModule(extraModule);

var contractOnlyModule = builder.ExportModule(
    "contract-only",
    "Sample.ContractOnly",
    definition => definition.AddResource<ParameterResource>(
        "contract-only-message",
        context => context.ApplicationBuilder.AddParameter(
            context.ResourceName,
            "contract metadata without an image publisher",
            publishValueAsDefault: true)));

builder.AddModule(contractOnlyModule);
await builder.Build().RunAsync();
