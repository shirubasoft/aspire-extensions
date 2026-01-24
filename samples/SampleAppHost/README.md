# SampleAppHost

A sample Aspire AppHost project demonstrating the SharedResources library.

## Overview

This sample shows how to use the SharedResources library to manage container resources that are built from external repositories. It demonstrates:

- Enabling shared resource support with `AddSharedResourceSupport()`
- Using `WithSharedResourceMetadata()` with inline parameters and options
- Using `WithSharedResourceMetadata()` with a pre-built annotation object
- Configuring multiple shared resources

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/get-started)
- [Git](https://git-scm.com/)

## Configuration

The SharedResources library supports multiple configuration methods:

### Option 1: User Secrets (Recommended for Development)

```bash
# Initialize user secrets
cd samples/SampleAppHost
dotnet user-secrets init

# Set base path for all repositories
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/path/to/your/repos"

# Or set individual repository paths
dotnet user-secrets set "SharedResources:RepositoryPaths:api-service" "/path/to/api-service"
dotnet user-secrets set "SharedResources:RepositoryPaths:worker-service" "/path/to/worker-service"
```

### Option 2: Environment Variables

```bash
# Set base path
export SHAREDRESOURCES__REPOSITORIESBASEPATH=/path/to/your/repos

# Or set individual paths
export SHAREDRESOURCES__REPOSITORYPATHS__api-service=/path/to/api-service
export SHAREDRESOURCES__REPOSITORYPATHS__worker-service=/path/to/worker-service
```

### Option 3: appsettings.Development.json

Edit `appsettings.Development.json`:

```json
{
  "SharedResources": {
    "RepositoriesBasePath": "/path/to/your/repos",
    "RepositoryPaths": {
      "api-service": "/path/to/api-service",
      "worker-service": "/path/to/worker-service"
    }
  }
}
```

## Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| `RepositoriesBasePath` | Base directory where repositories are located. Paths are resolved as `{base}/{repo-name}` | null |
| `RepositoryPaths` | Dictionary of explicit paths keyed by service name. Takes precedence over `RepositoriesBasePath` | empty |
| `PromptForMissingPaths` | Whether to interactively prompt for missing paths. Set to `false` in CI/CD | `true` |

## How to Run

1. Clone the required repositories to your local machine
2. Configure the repository paths using one of the methods above
3. Run the AppHost:

```bash
cd samples/SampleAppHost
dotnet run
```

The SharedResources library will:
1. Resolve the local paths for each shared resource
2. Get the current git commit SHA from each repository
3. Build container images tagged with the commit SHA
4. Start the Aspire application with the built containers

## Build Command Placeholders

The `imageBuildCommand` supports the following placeholders:

| Placeholder | Description |
|-------------|-------------|
| `{ProjectPath}` | Full path to the project file (RepoPath + ProjectPath property) |
| `{ImageName}` | Container image name (ImageName property or ServiceName) |
| `{ImageTag}` | The 7-character git commit SHA of the repository HEAD |
| `{RepoPath}` | Absolute path to the repository root directory |

## Example Build Commands

### .NET Container Publishing

```csharp
imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}"
```

### Docker Build

```csharp
imageBuildCommand: "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/Dockerfile {RepoPath}"
```

### Docker Build with Custom Dockerfile

```csharp
imageBuildCommand: "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/docker/Dockerfile.prod {RepoPath}"
```
