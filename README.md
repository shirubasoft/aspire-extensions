# Aspire SharedResources

<!-- Badges placeholder -->
<!-- [![NuGet](https://img.shields.io/nuget/v/Aspire.Hosting.SharedResources.svg)](https://www.nuget.org/packages/Aspire.Hosting.SharedResources/) -->
<!-- [![Build Status](https://img.shields.io/github/actions/workflow/status/your-org/aspire-extensions/ci.yml?branch=main)](https://github.com/your-org/aspire-extensions/actions) -->
<!-- [![License](https://img.shields.io/github/license/your-org/aspire-extensions)](LICENSE) -->

A .NET Aspire extension library that enables you to define container resources from external repositories within your Aspire AppHost. The library automatically builds container images from local repository clones, tagged with the current git commit SHA, ensuring version consistency across your distributed application.

## Overview

When building distributed applications with .NET Aspire, you often need to integrate services from external repositories. SharedResources bridges this gap by:

- Allowing you to declare container resources that reference external git repositories
- Automatically resolving local paths to those repositories
- Building container images on-demand using the repository's current commit
- Tagging images with the git commit SHA for reproducible builds
- Caching built images to avoid unnecessary rebuilds

## Quick Start

Get up and running in 5 minutes:

### 1. Install the Package

Add a project reference to the SharedResources library:

```xml
<ProjectReference Include="path/to/src/SharedResources/SharedResources.csproj" />
```

### 2. Enable SharedResources in Your AppHost

```csharp
using Aspire.Hosting.SharedResources;

var builder = DistributedApplication.CreateBuilder(args);

// Enable shared resource support
builder.AddSharedResourceSupport();

// Add a container from an external repository
builder.AddContainer("api-service", "api-service")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/api-service",
        serviceName: "api-service",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
        })
    .WithHttpEndpoint(5001, name: "http");

builder.Build().Run();
```

### 3. Configure Repository Paths

Set up the path to your local repository clone:

```bash
cd your-apphost-project
dotnet user-secrets init
dotnet user-secrets set "SharedResources:RepositoryPaths:api-service" "/path/to/api-service"
```

### 4. Run Your AppHost

```bash
dotnet run
```

The library will automatically:
1. Resolve the repository path
2. Get the current commit SHA
3. Build the container image (if needed)
4. Start your Aspire application

## Features

- **Automatic Image Building**: Builds container images from external repositories before the AppHost starts
- **Git-based Tagging**: Tags images with the 7-character commit SHA for version tracking
- **Smart Caching**: Skips builds when an image with the correct tag already exists
- **Flexible Configuration**: Configure paths via user-secrets, environment variables, or appsettings.json
- **Interactive Prompts**: Optionally prompts for missing paths during development
- **Multiple Build Commands**: Supports both `dotnet publish` and `docker build` workflows
- **Aspire Integration**: Uses Aspire's eventing system (BeforeStartEvent) for seamless integration

## Installation

The SharedResources library is part of the aspire-extensions solution. Add a project reference:

```xml
<ItemGroup>
  <ProjectReference Include="../src/SharedResources/SharedResources.csproj" />
</ItemGroup>
```

Or reference the compiled assembly once published as a NuGet package.

## Basic Usage

### Using Inline Parameters with Options

```csharp
builder.AddContainer("api-service", "api-service")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/api-service",
        serviceName: "api-service",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
            options.DefaultBranch = "main";
            options.ImageName = "my-custom-image-name";
        });
```

### Using a Pre-built Annotation

```csharp
var annotation = new SharedResourceAnnotation
{
    GitHubRepository = "myorg/worker-service",
    ServiceName = "worker-service",
    ImageBuildCommand = "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/Dockerfile {RepoPath}",
    DefaultBranch = "main"
};

builder.AddContainer("worker-service", "worker-service")
    .WithSharedResourceMetadata(annotation);
```

## Configuration

Repository paths can be configured through multiple sources. See the [Configuration Guide](docs/configuration.md) for complete details.

### Quick Configuration Examples

**User Secrets (Recommended for Development):**
```bash
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"
```

**Environment Variables:**
```bash
export SHAREDRESOURCES__REPOSITORIESBASEPATH=/home/user/repos
```

**appsettings.json:**
```json
{
  "SharedResources": {
    "RepositoriesBasePath": "/home/user/repos",
    "PromptForMissingPaths": true
  }
}
```

## Troubleshooting

### Repository Not Found

**Error:** `Cannot resolve path for service 'servicename'`

**Solution:** Configure the repository path using one of these methods:

```bash
# User secrets (individual path)
dotnet user-secrets set "SharedResources:RepositoryPaths:servicename" "/path/to/repo"

# User secrets (base path for all repos)
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"

# Environment variable
export SHAREDRESOURCES__REPOSITORYPATHS__SERVICENAME=/path/to/repo
```

### Git Not Installed

**Error:** `git command not found` or similar

**Solution:** Install git:
- **Ubuntu/Debian:** `sudo apt install git`
- **macOS:** `brew install git` or `xcode-select --install`
- **Windows:** Download from [git-scm.com](https://git-scm.com/)

### Docker Not Running

**Error:** `Docker is not available. Please ensure Docker is running.`

**Solution:** Start the Docker daemon:
- **Linux:** `sudo systemctl start docker`
- **macOS/Windows:** Open Docker Desktop

### Build Fails

**Error:** Build command exits with non-zero status

**Solution:**
1. Check the build output for specific errors
2. Verify the Dockerfile or project file exists at the specified path
3. Ensure all build dependencies are available
4. Try running the build command manually in the repository directory

### Path Does Not Exist

**Error:** `Configured path does not exist: /path/to/repo`

**Solution:**
1. Verify the path is correct
2. Clone the repository: `git clone https://github.com/org/repo.git /path/to/repo`
3. Update your configuration with the correct path

## Samples

See the [SampleAppHost](samples/SampleAppHost) for a complete working example demonstrating:

- Enabling shared resource support
- Configuring multiple shared resources
- Using both inline parameters and pre-built annotations
- Different build command patterns

## Documentation

- [Getting Started Guide](docs/getting-started.md) - Step-by-step setup instructions
- [Configuration Reference](docs/configuration.md) - All configuration options explained

## Tech Stack

- **.NET 10**
- **Aspire 13.1.0**
- **CliWrap 3.10.0** - For executing git and docker commands

## License

<!-- Add your license information here -->
