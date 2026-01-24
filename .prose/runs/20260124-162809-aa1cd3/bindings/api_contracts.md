# SharedResources Library API Contracts Summary

## Overview

This document summarizes the complete API contract specifications for the SharedResources library, which provides automatic container building for Aspire applications using services from external repositories.

## Contract Files Created

| File | Component | Purpose |
|------|-----------|---------|
| `lib-shared-resource-annotation.md` | SharedResourceAnnotation, SharedResourceOptions, SharedResourceConfiguration | Core annotation and configuration classes |
| `lib-shared-resource-extensions.md` | SharedResourceExtensions | Extension methods for IResourceBuilder and IDistributedApplicationBuilder |
| `lib-repository-path-resolver.md` | IRepositoryPathResolver, RepositoryPathResolver | Repository path discovery and resolution |
| `lib-git-operations.md` | IGitOperations, GitOperations | Git CLI operations for commit SHA and state |
| `lib-container-image-service.md` | IContainerImageService, ContainerImageService | Docker operations for image management |
| `lib-shared-resource-build-service.md` | SharedResourceBuildService | Event subscriber for BeforeStartEvent |

## Architecture Summary

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           AppHost Startup                                │
├─────────────────────────────────────────────────────────────────────────┤
│  1. AddSharedResourceSupport() registers services                        │
│  2. AddContainer().WithSharedResourceMetadata() adds annotations        │
│  3. BeforeStartEvent fires                                               │
│  4. SharedResourceBuildService processes annotated resources             │
│  5. AppHost continues with all images available                          │
└─────────────────────────────────────────────────────────────────────────┘
```

## Interface Summary

### IRepositoryPathResolver

```csharp
Task<string> ResolveRepositoryPathAsync(string gitHubRepository, string serviceName, CancellationToken ct);
Task<bool> IsRepositoryAvailableAsync(string gitHubRepository, string serviceName, CancellationToken ct);
Task SaveRepositoryPathAsync(string serviceName, string repositoryPath, CancellationToken ct);
```

### IGitOperations

```csharp
Task<string> GetCurrentCommitShaAsync(string repositoryPath, CancellationToken ct);
Task<string> GetCurrentBranchAsync(string repositoryPath, CancellationToken ct);
Task<bool> HasUncommittedChangesAsync(string repositoryPath, CancellationToken ct);
Task<bool> IsGitRepositoryAsync(string repositoryPath, CancellationToken ct);
```

### IContainerImageService

```csharp
Task<bool> ImageExistsLocallyAsync(string imageName, string tag, CancellationToken ct);
Task BuildImageAsync(string command, string workingDirectory, CancellationToken ct);
Task TagImageAsync(string sourceImage, string targetImage, CancellationToken ct);
Task<bool> IsDockerAvailableAsync(CancellationToken ct);
Task<ContainerImageInfo?> GetImageInfoAsync(string imageName, CancellationToken ct);
```

## Exception Hierarchy

```
Exception
├── RepositoryNotFoundException
│   ├── GitHubRepository
│   ├── ServiceName
│   └── GetDetailedMessage()
├── GitOperationException
│   ├── Command
│   ├── RepositoryPath
│   ├── ExitCode
│   └── StandardError
├── ContainerOperationException
│   ├── Command
│   ├── ExitCode
│   └── StandardError
├── ContainerBuildException : ContainerOperationException
│   ├── WorkingDirectory
│   ├── BuildOutput
│   └── GetDetailedMessage()
└── SharedResourceBuildException
    ├── Errors (IReadOnlyList<SharedResourceError>)
    └── GetDetailedMessage()
```

## Configuration

### SharedResourceConfiguration

```csharp
public class SharedResourceConfiguration
{
    public const string SectionName = "SharedResources";
    public string? RepositoriesBasePath { get; set; }
    public bool PromptForMissingPaths { get; set; } = true;
    public Dictionary<string, string> RepositoryPaths { get; set; } = new();
}
```

### Configuration Priority

1. Explicit path: `SharedResources:RepositoryPaths:{ServiceName}`
2. Environment variable: `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}`
3. Base path: `SharedResources:RepositoriesBasePath` + repo name
4. User prompt via IInteractionService

## Placeholder Substitution

| Placeholder | Description | Example |
|-------------|-------------|---------|
| `{RepoPath}` | Repository root path | `/home/user/repos/repo-1` |
| `{ProjectPath}` | Full path to project file | `/home/user/repos/repo-1/src/Api/Api.csproj` |
| `{ImageName}` | Container image name | `api-1` |
| `{ImageTag}` | Commit SHA (7 chars) | `abc1234` |

## Dependencies

```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting" Version="13.1.0" />
  <PackageReference Include="CliWrap" Version="3.10.0" />
</ItemGroup>
```

## Usage Example

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddSharedResourceSupport();

builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-1",
        serviceName: "api-1",
        imageBuildCommand: "dotnet publish {ProjectPath} /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
        });

builder.Build().Run();
```

## Design Decisions

1. **Git CLI over LibGit2Sharp**: Consistency with Docker approach, no native dependencies
2. **Docker CLI over Docker.DotNet**: Simpler, supports any build command
3. **CliWrap for process execution**: Active maintenance, good async/cancellation support
4. **No dirty suffix**: Logs warning but uses same SHA to simplify caching

## Files Location

All contracts are located at:
```
/home/danielreis/code/aspire-extensions/docs/contracts/
├── lib-shared-resource-annotation.md
├── lib-shared-resource-extensions.md
├── lib-repository-path-resolver.md
├── lib-git-operations.md
├── lib-container-image-service.md
└── lib-shared-resource-build-service.md
```
