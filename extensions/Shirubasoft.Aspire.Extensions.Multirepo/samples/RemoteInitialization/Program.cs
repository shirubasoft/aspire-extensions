var builder = DistributedApplication.CreateBuilder(args);

var git = builder.AddRequiredTool("git-cli", "git")
    .WithWebsite("https://git-scm.com/downloads")
    .WithInstallCommand(GetGitInstallOptions());

builder.ExportModule("remote-notifications", module =>
{
    module.WithRepository("https://github.com/shirubasoft/spire-external-repo-sample.git");
    module
        .AddProject(
            "notification-service",
            Path.Combine("NotificationService", "NotificationService.csproj"),
            ModuleProjectPathBase.Repository)
        .ConfigureProject((_, project) =>
            project
                .WithHttpEndpoint(name: "http")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health"))
        .ExportAsContainerWithCommand(
            new ModuleImageCommandOptions(
                "notification-service",
                "dotnet",
                "publish",
                "NotificationService.csproj",
                "/t:PublishContainer",
                $"-p:ContainerRepository={ModuleImageCommandOptions.ImageNamePlaceholder}",
                $"-p:ContainerImageTag={ModuleImageCommandOptions.ImageTagPlaceholder}"),
            (_, container) =>
                container
                    .WithHttpEndpoint(targetPort: 8080, name: "http")
                    .WithExternalHttpEndpoints());
});

var notifications = builder.ImportModule("remote-notifications");
notifications
    .GetResource<IResourceWithWaitSupport>("notification-service")
    .WaitFor(git);

await builder.Build().RunAsync();

static RequiredToolInstallOptions GetGitInstallOptions()
{
    if (OperatingSystem.IsWindows())
    {
        return new RequiredToolInstallOptions(
            "winget",
            "install",
            "--id",
            "Git.Git",
            "--exact",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements");
    }

    if (OperatingSystem.IsMacOS())
    {
        return new RequiredToolInstallOptions("brew", "install", "git");
    }

    return new RequiredToolInstallOptions("sudo", "apt-get", "install", "--yes", "git");
}
