# runnable_api

kind: let

source:
```prose
let runnable_api = do verify-runnable("api-contracts", ...)
```

---

## Verification Results

**Build Status**: SUCCESS - Project compiles without errors

```
dotnet build src/SharedResources/SharedResources.csproj
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:00.58
```

---

### 1. SharedResourceAnnotation

**Status**: PASS
**File Location**: `/home/danielreis/code/aspire-extensions/src/SharedResources/Annotations/SharedResourceAnnotation.cs`

**Expected Properties**:
| Property | Expected | Found | Match |
|----------|----------|-------|-------|
| GitHubRepository | YES | `public required string GitHubRepository { get; init; }` | YES |
| ServiceName | YES | `public required string ServiceName { get; init; }` | YES |
| ImageBuildCommand | YES | `public required string ImageBuildCommand { get; init; }` | YES |
| ProjectPath | YES | `public string? ProjectPath { get; init; }` | YES |
| ImageName | YES | `public string? ImageName { get; init; }` | YES |
| DefaultBranch | YES | `public string DefaultBranch { get; init; } = "main";` | YES |

**Additional Methods Found**:
- `GetEffectiveImageName()` - Returns ImageName if specified, otherwise ServiceName

**Match with Expected**: YES

**Notes**: Class implements `IResourceAnnotation` interface. All required properties use `required` modifier. `GitHubRepository`, `ServiceName`, and `ImageBuildCommand` are required. `ProjectPath`, `ImageName` are optional (nullable). `DefaultBranch` defaults to "main".

---

### 2. IRepositoryPathResolver Interface

**Status**: PASS
**File Location**: `/home/danielreis/code/aspire-extensions/src/SharedResources/Services/IRepositoryPathResolver.cs`

**Expected Methods**:
| Method | Expected | Found | Match |
|--------|----------|-------|-------|
| ResolveRepositoryPathAsync | YES | `Task<string> ResolveRepositoryPathAsync(string gitHubRepository, string serviceName, CancellationToken cancellationToken = default)` | YES |
| IsRepositoryAvailableAsync | YES | `Task<bool> IsRepositoryAvailableAsync(string gitHubRepository, string serviceName, CancellationToken cancellationToken = default)` | YES |

**Additional Methods Found**:
- `SaveRepositoryPathAsync(string serviceName, string repositoryPath, CancellationToken cancellationToken = default)` - Saves path to user secrets

**Match with Expected**: YES

**Implementation**: `RepositoryPathResolver` class at `/home/danielreis/code/aspire-extensions/src/SharedResources/Services/RepositoryPathResolver.cs`

**Notes**: Interface is fully documented with XML comments. Implementation uses priority-based resolution: explicit config > env var > base path derivation > user prompt.

---

### 3. IGitOperations Interface

**Status**: PASS
**File Location**: `/home/danielreis/code/aspire-extensions/src/SharedResources/Services/IGitOperations.cs`

**Expected Methods**:
| Method | Expected | Found | Match |
|--------|----------|-------|-------|
| GetCurrentCommitShaAsync | YES | `Task<string> GetCurrentCommitShaAsync(string repositoryPath, CancellationToken cancellationToken = default)` | YES |
| GetCurrentBranchAsync | YES | `Task<string> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default)` | YES |

**Additional Methods Found**:
- `HasUncommittedChangesAsync(string repositoryPath, CancellationToken cancellationToken = default)` - Checks for uncommitted changes
- `IsGitRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default)` - Validates if path is a git repository

**Match with Expected**: YES

**Implementation**: `GitOperations` class at `/home/danielreis/code/aspire-extensions/src/SharedResources/Services/GitOperations.cs`

**Notes**: Implementation uses CliWrap to execute git CLI commands. Returns 7-character short SHA for commits. Returns "HEAD" for detached HEAD state.

---

### 4. IContainerImageService Interface

**Status**: PASS
**File Location**: `/home/danielreis/code/aspire-extensions/src/SharedResources/Services/IContainerImageService.cs`

**Expected Methods**:
| Method | Expected | Found | Match |
|--------|----------|-------|-------|
| ImageExistsLocallyAsync | YES | `Task<bool> ImageExistsLocallyAsync(string imageName, string tag, CancellationToken cancellationToken = default)` | YES |
| BuildImageAsync | YES | `Task BuildImageAsync(string command, string workingDirectory, CancellationToken cancellationToken = default)` | YES |
| IsDockerAvailableAsync | YES | `Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)` | YES |

**Additional Methods Found**:
- `TagImageAsync(string sourceImage, string targetImage, CancellationToken cancellationToken = default)` - Tags existing image
- `GetImageInfoAsync(string imageName, CancellationToken cancellationToken = default)` - Gets image metadata

**Match with Expected**: YES

**Implementation**: `ContainerImageService` class at `/home/danielreis/code/aspire-extensions/src/SharedResources/Services/ContainerImageService.cs`

**Notes**: Implementation uses CliWrap to execute Docker CLI commands. Build timeout is 10 minutes. Quick operations timeout is 30 seconds. Docker availability check timeout is 10 seconds.

---

### 5. Extension Methods

**Status**: PASS
**File Location**: `/home/danielreis/code/aspire-extensions/src/SharedResources/Extensions/SharedResourceExtensions.cs`

**Expected Methods**:
| Method | Expected | Found | Match |
|--------|----------|-------|-------|
| AddSharedResourceSupport | YES | `public static IDistributedApplicationBuilder AddSharedResourceSupport(this IDistributedApplicationBuilder builder)` | YES |
| WithSharedResourceMetadata | YES | Found (2 overloads) | YES |

**WithSharedResourceMetadata Overloads**:
1. `WithSharedResourceMetadata(this IResourceBuilder<ContainerResource> builder, string gitHubRepository, string serviceName, string imageBuildCommand, Action<SharedResourceOptions>? configure = null)`
2. `WithSharedResourceMetadata(this IResourceBuilder<ContainerResource> builder, SharedResourceAnnotation annotation)`

**Match with Expected**: YES

**Notes**:
- `AddSharedResourceSupport` registers: `SharedResourceConfiguration` options, `IRepositoryPathResolver`, `IGitOperations`, `IContainerImageService`, `SharedResourceBuildService`, and subscribes to `BeforeStartEvent`
- `WithSharedResourceMetadata` validates GitHub repository format (must be "org/repo")
- Private helper method `ValidateGitHubRepository` ensures proper format

---

## Summary

| Component | Status | Match |
|-----------|--------|-------|
| SharedResourceAnnotation | PASS | YES |
| IRepositoryPathResolver | PASS | YES |
| IGitOperations | PASS | YES |
| IContainerImageService | PASS | YES |
| Extension Methods | PASS | YES |

**Overall Status**: PASS

All expected API contracts exist and are correctly implemented. The project compiles successfully with zero warnings and zero errors. Each interface has a concrete implementation class that uses CliWrap for CLI operations (git, docker, dotnet).
