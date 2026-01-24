# Getting Started with SharedResources

This guide walks you through setting up the SharedResources library for your .NET Aspire application.

## Prerequisites

Before you begin, ensure you have the following installed:

### Docker

Docker is required for building and running container images.

- **Installation:** [Get Docker](https://www.docker.com/get-started)
- **Verify installation:** `docker --version`

### .NET 10 SDK

The library targets .NET 10.

- **Installation:** [Download .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Verify installation:** `dotnet --version`

### Git

Git is required for determining the current commit SHA used for image tagging.

- **Installation:**
  - Ubuntu/Debian: `sudo apt install git`
  - macOS: `brew install git` or `xcode-select --install`
  - Windows: [Git for Windows](https://git-scm.com/download/windows)
- **Verify installation:** `git --version`

## Step-by-Step Installation

### Step 1: Add the SharedResources Reference

Add a project reference to the SharedResources library in your AppHost project:

```xml
<!-- In your AppHost.csproj -->
<ItemGroup>
  <ProjectReference Include="../src/SharedResources/SharedResources.csproj" />
</ItemGroup>
```

Alternatively, if publishing as a NuGet package:

```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.SharedResources" Version="1.0.0" />
</ItemGroup>
```

### Step 2: Enable SharedResources in Your AppHost

Modify your `Program.cs` to enable shared resource support:

```csharp
using Aspire.Hosting.SharedResources;

var builder = DistributedApplication.CreateBuilder(args);

// Enable shared resource support - must be called before adding shared resources
builder.AddSharedResourceSupport();

// Your resource definitions go here...

builder.Build().Run();
```

### Step 3: Clone Your External Repositories

Clone the repositories you want to reference as shared resources:

```bash
# Create a directory for your repositories
mkdir -p ~/repos

# Clone your repositories
cd ~/repos
git clone https://github.com/your-org/api-service.git
git clone https://github.com/your-org/worker-service.git
```

### Step 4: Initialize User Secrets

User secrets provide a secure way to store repository paths during development:

```bash
cd path/to/your/AppHost
dotnet user-secrets init
```

## First Resource Configuration

Let's add your first shared resource.

### Step 1: Define the Resource in Program.cs

```csharp
using Aspire.Hosting.SharedResources;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddSharedResourceSupport();

// Add a container resource from an external repository
builder.AddContainer("api-service", "api-service")
    .WithSharedResourceMetadata(
        gitHubRepository: "your-org/api-service",
        serviceName: "api-service",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
        })
    .WithHttpEndpoint(5001, name: "http");

builder.Build().Run();
```

### Step 2: Configure the Repository Path

Set the path to your local repository clone:

```bash
dotnet user-secrets set "SharedResources:RepositoryPaths:api-service" "/home/user/repos/api-service"
```

Or set a base path if all repositories are in the same parent directory:

```bash
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"
```

When using `RepositoriesBasePath`, the library extracts the repository name from the `gitHubRepository` parameter (the part after the slash) and appends it to the base path. For example:
- `gitHubRepository: "your-org/api-service"` + `RepositoriesBasePath: "/home/user/repos"`
- Resolves to: `/home/user/repos/api-service`

## Running the Application

### Step 1: Start Docker

Ensure Docker is running on your system.

### Step 2: Run the AppHost

```bash
cd path/to/your/AppHost
dotnet run
```

## What to Expect

### First Run Output

On first run, you should see output similar to:

```
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Checking shared resources...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Found 1 shared resource(s)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-service: repo at /home/user/repos/api-service (commit: abc1234)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Building api-service:abc1234...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-service:abc1234 built successfully
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      All shared resources ready
```

### Subsequent Runs

On subsequent runs, if the image already exists:

```
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Checking shared resources...
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Found 1 shared resource(s)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-service: repo at /home/user/repos/api-service (commit: abc1234)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Image api-service:abc1234 exists
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      All shared resources ready
```

### After New Commits

When you make a new commit in the external repository, the library detects the new commit SHA and automatically rebuilds:

```
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      api-service: repo at /home/user/repos/api-service (commit: def5678)
info: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
        Building api-service:def5678...
```

### Uncommitted Changes Warning

If the repository has uncommitted changes, you'll see a warning:

```
warn: Aspire.Hosting.SharedResources.SharedResourceBuildService[0]
      Repository api-service has uncommitted changes. Image will be tagged with current HEAD SHA (abc1234)
```

### Interactive Path Prompt

If no path is configured and `PromptForMissingPaths` is enabled (default), you'll be prompted:

```
Repository Path Required
The repository path for service 'api-service' is not configured.

Repository Path: [Enter path here]
```

After entering a valid path, you'll be asked if you want to save it:

```
Save Path?
Would you like to save this path for future runs? [Y/n]
```

## Next Steps

Now that you have SharedResources working, you can:

1. **Add More Resources** - Define additional container resources from other external repositories

2. **Explore Configuration Options** - See [Configuration Reference](configuration.md) for all available settings

3. **Use Docker Build** - Try different build commands:
   ```csharp
   imageBuildCommand: "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/Dockerfile {RepoPath}"
   ```

4. **Configure for CI/CD** - Disable interactive prompts for automated environments:
   ```bash
   export SHAREDRESOURCES__PROMPTFORMISSINGPATHS=false
   ```

5. **View the Sample** - Check out [samples/SampleAppHost](../samples/SampleAppHost) for more examples

## Common Issues

### "Cannot resolve path for service"

The repository path is not configured. Set it via user secrets:
```bash
dotnet user-secrets set "SharedResources:RepositoryPaths:servicename" "/path/to/repo"
```

### "Path is not a git repository"

The configured path does not contain a valid git repository. Ensure you've cloned the repository correctly:
```bash
git clone https://github.com/org/repo.git /path/to/repo
```

### "Docker is not available"

Docker is not running. Start Docker Desktop or the Docker daemon:
```bash
sudo systemctl start docker  # Linux
```

### Build command fails

Check that:
1. The project path or Dockerfile exists
2. All build dependencies are installed
3. You can run the build command manually in the repository directory
